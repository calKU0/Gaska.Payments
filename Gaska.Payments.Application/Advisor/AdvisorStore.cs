using Gaska.Payments.Application.Settlement;
using Gaska.Payments.Erp;
using Gaska.Payments.Integrations.Advisor;

using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace Gaska.Payments.Application.Advisor;

/// <summary>A transfer taken off the queue to be asked about.</summary>
public sealed record AdvisorCase(long PaymentId, int ContractorId, int Attempt, DateTime BookingDate, decimal Amount);

/// <summary>
/// Which transfers are waiting for the model, and what it said - <c>pay.AdvisorReview</c> and
/// <c>pay.AdvisorAllocation</c>.
/// </summary>
/// <remarks>
/// The answers live apart from <c>pay.Allocation</c> on purpose. That table is the engine's and is
/// rewritten on every cycle, so the model's answer - minutes of work - would be gone an hour later.
/// And the two must never mix: the service settles by itself from its own allocations, while what
/// the model says is only ever shown to an accountant.
/// </remarks>
public sealed class AdvisorStore(IOptions<ErpOptions> erpOptions, IOptions<AdvisorOptions> options)
{
    private readonly string _connectionString = erpOptions.Value.ConnectionString;
    private readonly AdvisorOptions _options = options.Value;

    /// <summary>
    /// Puts back the questions a stopped service left open, so they are asked again at once rather
    /// than counted as asked.
    /// </summary>
    public async Task<int> ReleaseInterruptedAsync(CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE pay.AdvisorReview
            SET Status = @failed, Attempts = CASE WHEN Attempts > 0 THEN Attempts - 1 ELSE 0 END,
                NextAttemptAt = SYSDATETIME(), Error = N'Przerwane zatrzymaniem serwisu.'
            WHERE Status = @running;
            """;

        await using var connection = await OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@failed", AdvisorStatus.Failed);
        command.Parameters.AddWithValue("@running", AdvisorStatus.Running);

        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Takes the next transfer worth asking about, marks it as being asked, and returns it. Null
    /// when there is none.
    /// </summary>
    /// <remarks>
    /// Worth asking about means an incoming transfer the engine only guessed at or found nothing
    /// for, whose contractor is known - the model works from the contractor's documents - and whose
    /// cash entry is still open in ERP: once an accountant has settled it, an answer helps nobody.
    ///
    /// The newest first, because that is what the accountants have in front of them today; within
    /// a day the largest first. A transfer is asked about once, and again only when the engine has
    /// since put it on another contractor - the answer was about somebody else's documents. A
    /// failed question waits longer after each attempt and is given up after the last.
    /// </remarks>
    public async Task<AdvisorCase?> TakeNextAsync(IReadOnlyCollection<int> excludedContractors, CancellationToken cancellationToken)
    {
        var confidences = _options.Confidences.Select((_, i) => $"@c{i}").ToArray();
        var excluded = excludedContractors.Select((_, i) => $"@x{i}").ToArray();

        var sql = $"""
            DECLARE @taken TABLE (PaymentId BIGINT, ContractorId INT, Attempts INT, BookingDate DATE, Amount DECIMAL(19,2));

            WITH kolejny AS (
                SELECT TOP 1 p.PaymentId, p.ContractorId, p.BookingDate, p.Amount
                FROM pay.Payment AS p
                INNER JOIN CDN.Zapisy AS z ON z.KAZ_GIDNumer = p.ErpEntryId
                LEFT JOIN pay.AdvisorReview AS r ON r.PaymentId = p.PaymentId
                WHERE p.Status = @proposed
                  AND p.Direction = 'P'
                  AND p.PostingCategory = 'Standard'
                  AND p.ContractorId <> 0
                  {(confidences.Length == 0 ? string.Empty : $"AND p.Confidence IN ({string.Join(", ", confidences)})")}
                  {(excluded.Length == 0 ? string.Empty : $"AND p.ContractorId NOT IN ({string.Join(", ", excluded)})")}
                  AND p.BookingDate >= DATEADD(DAY, -@lookback, CAST(GETDATE() AS DATE))
                  AND z.KAZ_Rozliczony = 0 AND z.KAZ_Pozostaje > 0.004
                  AND (r.PaymentId IS NULL
                       OR (r.Status = @failed AND r.Attempts < @maxAttempts AND r.NextAttemptAt <= SYSDATETIME())
                       OR (r.Status = @done AND r.ContractorId <> p.ContractorId))
                ORDER BY p.BookingDate DESC, p.Amount DESC
            )
            MERGE pay.AdvisorReview AS target
            USING kolejny AS source ON target.PaymentId = source.PaymentId
            WHEN MATCHED THEN UPDATE SET
                Status = @running,
                ContractorId = source.ContractorId,
                Attempts = CASE WHEN target.Status = @done THEN 1 ELSE target.Attempts + 1 END,
                RequestedAt = SYSDATETIME(),
                Error = NULL
            WHEN NOT MATCHED THEN INSERT (PaymentId, ContractorId, Status, Attempts, RequestedAt)
                VALUES (source.PaymentId, source.ContractorId, @running, 1, SYSDATETIME())
            OUTPUT inserted.PaymentId, inserted.ContractorId, inserted.Attempts, source.BookingDate, source.Amount
                INTO @taken;

            SELECT PaymentId, ContractorId, Attempts, BookingDate, Amount FROM @taken;
            """;

        await using var connection = await OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 120 };
        command.Parameters.AddWithValue("@proposed", SettlementStatus.Proposed);
        command.Parameters.AddWithValue("@running", AdvisorStatus.Running);
        command.Parameters.AddWithValue("@failed", AdvisorStatus.Failed);
        command.Parameters.AddWithValue("@done", AdvisorStatus.Done);
        command.Parameters.AddWithValue("@lookback", _options.LookbackDays);
        command.Parameters.AddWithValue("@maxAttempts", _options.MaxAttempts);

        var i = 0;
        foreach (var confidence in _options.Confidences) command.Parameters.AddWithValue(confidences[i++], confidence);

        i = 0;
        foreach (var contractor in excludedContractors) command.Parameters.AddWithValue(excluded[i++], contractor);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;

        return new AdvisorCase(
            reader.GetInt64(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetDateTime(3), reader.GetDecimal(4));
    }

    /// <summary>
    /// Records the answer. Returns how many of the documents it proposed were kept.
    /// </summary>
    /// <remarks>
    /// Only the documents it proposes to settle something against - see
    /// <see cref="SettlementAdvice.Proposed"/>. A document is kept only when it exists as a payment
    /// in ERP: the model reads the database but writes free text, and a number it got wrong would
    /// otherwise sit here pointing at nothing. The answer replaces the previous one whole, in one
    /// transaction, so the application never sees half of each.
    /// </remarks>
    public async Task<int> SaveAnswerAsync(long paymentId, SettlementAdvice advice, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);

        await using (var clear = new SqlCommand(
            "DELETE FROM pay.AdvisorAllocation WHERE PaymentId = @paymentId;", connection, transaction))
        {
            clear.Parameters.AddWithValue("@paymentId", paymentId);
            await clear.ExecuteNonQueryAsync(cancellationToken);
        }

        const string insert = """
            INSERT INTO pay.AdvisorAllocation (PaymentId, DocType, DocId, DocLp, DocNumber, Amount, Reason)
            SELECT @paymentId, pl.TrP_GIDTyp, pl.TrP_GIDNumer, pl.TrP_GIDLp,
                   ISNULL(LEFT(CDN.NumerDokumentu(n.TrN_GIDTyp, n.TrN_SpiTyp, n.TrN_TrNTyp,
                                                  n.TrN_TrNNumer, n.TrN_TrNRok, n.TrN_TrNSeria, 0), 50), ''),
                   @amount, @reason
            FROM CDN.TraPlat AS pl
            LEFT JOIN CDN.TraNag AS n ON n.TrN_GIDTyp = pl.TrP_GIDTyp AND n.TrN_GIDNumer = pl.TrP_GIDNumer
            WHERE pl.TrP_GIDTyp = @docType AND pl.TrP_GIDNumer = @docId AND pl.TrP_GIDLp = @docLp
              AND NOT EXISTS (SELECT 1 FROM pay.AdvisorAllocation AS a
                              WHERE a.PaymentId = @paymentId AND a.DocType = @docType
                                AND a.DocId = @docId AND a.DocLp = @docLp);
            """;

        var kept = 0;

        foreach (var document in advice.Proposed)
        {
            await using var command = new SqlCommand(insert, connection, transaction);
            command.Parameters.AddWithValue("@paymentId", paymentId);
            command.Parameters.AddWithValue("@docType", document.DocType);
            command.Parameters.AddWithValue("@docId", document.DocId);
            command.Parameters.AddWithValue("@docLp", document.DocLp);
            command.Parameters.AddWithValue("@amount", Math.Round(document.Amount, 2));
            command.Parameters.AddWithValue("@reason", Truncate(document.Reason, 1000));

            kept += await command.ExecuteNonQueryAsync(cancellationToken);
        }

        const string review = """
            UPDATE pay.AdvisorReview
            SET Status = @done, AnsweredAt = SYSDATETIME(), NextAttemptAt = NULL, Error = NULL,
                Summary = @summary, Answer = @answer
            WHERE PaymentId = @paymentId;
            """;

        await using (var command = new SqlCommand(review, connection, transaction))
        {
            command.Parameters.AddWithValue("@paymentId", paymentId);
            command.Parameters.AddWithValue("@done", AdvisorStatus.Done);
            command.Parameters.AddWithValue("@summary", advice.Summary);
            command.Parameters.AddWithValue("@answer", advice.Raw);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return kept;
    }

    /// <summary>Records a failed question and when to try again.</summary>
    public async Task SaveFailureAsync(long paymentId, string error, DateTime retryAt, CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE pay.AdvisorReview
            SET Status = @failed, Error = @error, NextAttemptAt = @retryAt
            WHERE PaymentId = @paymentId;
            """;

        await using var connection = await OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@paymentId", paymentId);
        command.Parameters.AddWithValue("@failed", AdvisorStatus.Failed);
        command.Parameters.AddWithValue("@error", Truncate(error, 1000));
        command.Parameters.AddWithValue("@retryAt", retryAt);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<SqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}

/// <summary>Where a question to the model stands.</summary>
public static class AdvisorStatus
{
    /// <summary>Sent, the answer not back yet.</summary>
    public const string Running = "Running";

    /// <summary>Answered - with documents or without.</summary>
    public const string Done = "Done";

    /// <summary>No usable answer; tried again later, up to the limit.</summary>
    public const string Failed = "Failed";
}
