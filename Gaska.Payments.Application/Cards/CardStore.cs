using Gaska.Payments.Domain.Model;
using Gaska.Payments.Erp;

using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace Gaska.Payments.Application.Cards;

/// <summary>A document payment a card transaction may close, found by the number in its notes.</summary>
public sealed record CardDocument(
    string Note,
    int DocType,
    int DocId,
    int DocLp,
    string DocNumber,
    int ContractorId,
    decimal Amount,
    decimal Remaining,
    int PaymentType);

/// <summary>One entry to be booked on the card register: a transaction, or a batch's commission.</summary>
public sealed record CardEntry(
    long PaymentId,
    string ExternalId,
    string Register,
    string Category,
    DateTime BookingDate,
    decimal Amount,
    bool IsIncoming,
    int ContractorId,
    string Description,
    string Remark,
    string Confidence,
    string Notes,
    string SourceFile,
    string Batch,
    CardDocument? Document,
    int PayoutEntryId = 0);

/// <summary>A batch whose commission is not booked yet, with what its transactions came to.</summary>
public sealed record CardBatch(string Point, string Batch, decimal Sales, decimal Refunds, int Count, DateTime LastDay)
{
    public string Key => $"{Point}/{Batch}";

    /// <summary>What the batch comes to after refunds - what Fiserv owes before its commission.</summary>
    public decimal Net => Sales - Refunds;
}

/// <summary>Reads and writes what the card register needs, in ERP and in our own tables.</summary>
public sealed class CardStore(IOptions<ErpOptions> erpOptions)
{
    private readonly string _connectionString = erpOptions.Value.ConnectionString;

    /// <summary>
    /// The terminal payments' documents: receipts and invoices, and their corrections for refunds,
    /// whose payment notes carry one of the given transaction numbers.
    /// </summary>
    /// <remarks>
    /// The cashier types the number the terminal printed into the payment's notes, and that has
    /// always been the one link between a card payment and what it paid for - the half-automat
    /// found documents the same way. Purchase documents carry numbers in their notes too (company
    /// card spending), so the search keeps to the sales types.
    /// </remarks>
    public async Task<IReadOnlyDictionary<string, List<CardDocument>>> FindDocumentsAsync(
        IReadOnlyCollection<string> numbers, DateTime from, DateTime to, CancellationToken cancellationToken = default)
    {
        var found = new Dictionary<string, List<CardDocument>>(StringComparer.Ordinal);
        if (numbers.Count == 0) return found;

        var parameters = numbers.Select((_, i) => $"@n{i}").ToArray();

        var sql = $"""
            SELECT RTRIM(pl.TrP_Notatki),
                   CAST(pl.TrP_GIDTyp AS INT), pl.TrP_GIDNumer, CAST(pl.TrP_GIDLp AS INT),
                   CDN.NumerDokumentu(n.TrN_GIDTyp, n.TrN_SpiTyp, n.TrN_TrNTyp,
                                      n.TrN_TrNNumer, n.TrN_TrNRok, n.TrN_TrNSeria, 0),
                   pl.TrP_KntNumer, pl.TrP_Kwota, pl.TrP_Pozostaje, CAST(pl.TrP_Typ AS INT)
            FROM CDN.TraNag AS n
            INNER JOIN CDN.TraPlat AS pl
                ON pl.TrP_GIDTyp = n.TrN_GIDTyp AND pl.TrP_GIDNumer = n.TrN_GIDNumer
            WHERE n.TrN_GIDTyp IN ({SalesDocumentTypes})
              AND n.TrN_Data2 BETWEEN @from AND @to
              AND pl.TrP_KntTyp = @contractorType
              AND pl.TrP_Rozliczona = 0
              AND pl.TrP_Pozostaje > 0
              AND RTRIM(pl.TrP_Notatki) IN ({string.Join(", ", parameters)})
            """;

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 120 };

        command.Parameters.AddWithValue("@from", XlDate.FromDateTime(from));
        command.Parameters.AddWithValue("@to", XlDate.FromDateTime(to));
        command.Parameters.AddWithValue("@contractorType", PaymentSettlement.ContractorParty);

        var index = 0;
        foreach (var number in numbers) command.Parameters.AddWithValue(parameters[index++], number);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            var document = new CardDocument(
                reader.GetString(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3),
                reader.IsDBNull(4) ? string.Empty : reader.GetString(4).Trim(),
                reader.GetInt32(5), reader.GetDecimal(6), reader.GetDecimal(7), reader.GetInt32(8));

            if (!found.TryGetValue(document.Note, out var list)) found[document.Note] = list = [];
            list.Add(document);
        }

        return found;
    }

    /// <summary>
    /// Writes one entry to be posted, with the document it is to close when there is one.
    /// </summary>
    /// <returns>True when the row is new; false when it was there already or an operator has ruled on it.</returns>
    public async Task<bool> SaveEntryAsync(CardEntry entry, int runId, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);

        var action = await SaveEntryAsync(connection, transaction, entry, runId, cancellationToken);

        if (action is null)
        {
            // Posted already, or an operator has decided on it - either way it stays as it is.
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        await transaction.CommitAsync(cancellationToken);
        return action == "INSERT";
    }

    /// <returns>"INSERT" or "UPDATE", or null when the row was left alone.</returns>
    private static async Task<string?> SaveEntryAsync(
        SqlConnection connection, SqlTransaction transaction, CardEntry entry, int runId,
        CancellationToken cancellationToken)
    {
        const string upsert = """
            MERGE pay.Payment AS target
            USING (SELECT @paymentId AS PaymentId) AS source
                ON target.PaymentId = source.PaymentId
            WHEN MATCHED AND target.Status IN ('Proposed', 'SettledInErp', 'NoSettlement')
            THEN UPDATE SET
                ContractorId = @contractorId, Confidence = @confidence, Notes = @notes,
                AllocatedAmount = @allocated, UnallocatedAmount = @unallocated,
                LastUpdatedAt = SYSDATETIME(), LastRunId = @runId
            WHEN NOT MATCHED THEN INSERT
                (PaymentId, BankExternalId, CreditedAccount, BookingDate, Amount, Currency,
                 PayerName, PayerAccount, Description, ContractorId, ContractorSource,
                 ContractorFromBankAccount, Confidence, Strategy, AllocatedAmount, UnallocatedAmount,
                 Notes, Status, Direction, RegisterSeries, PostingCategory, SourceFile,
                 CardBatch, CodPayoutEntryId, FirstSeenAt, LastUpdatedAt, LastRunId)
            VALUES
                (@paymentId, @externalId, '', @bookingDate, @amount, 'PLN',
                 @remark, '', @description, @contractorId, @contractorSource,
                 1, @confidence, @strategy, @allocated, @unallocated,
                 @notes, 'Proposed', @direction, @register, @category, @sourceFile,
                 @batch, @payoutEntryId, SYSDATETIME(), SYSDATETIME(), @runId)
            OUTPUT $action;
            """;

        var allocated = entry.Document is null ? 0m : Math.Min(entry.Document.Remaining, entry.Amount);

        await using (var command = new SqlCommand(upsert, connection, transaction))
        {
            command.Parameters.AddWithValue("@paymentId", entry.PaymentId);
            command.Parameters.AddWithValue("@externalId", Cut(entry.ExternalId, 64));
            command.Parameters.AddWithValue("@bookingDate", entry.BookingDate.Date);
            command.Parameters.AddWithValue("@amount", entry.Amount);
            command.Parameters.AddWithValue("@remark", Cut(entry.Remark, 140));
            command.Parameters.AddWithValue("@description", Cut(entry.Description, 500));
            command.Parameters.AddWithValue("@contractorId", entry.ContractorId);
            command.Parameters.AddWithValue("@contractorSource",
                entry.Category == PaymentCategory.PolcardFee ? "prowizja Fiserv" : "terminal kartowy");
            command.Parameters.AddWithValue("@confidence", entry.Confidence);
            command.Parameters.AddWithValue("@strategy", entry.Category);
            command.Parameters.AddWithValue("@allocated", allocated);
            command.Parameters.AddWithValue("@unallocated", Math.Max(0m, entry.Amount - allocated));
            command.Parameters.AddWithValue("@notes", Cut(entry.Notes, 1000));
            command.Parameters.AddWithValue("@direction", entry.IsIncoming ? "P" : "R");
            command.Parameters.AddWithValue("@register", entry.Register);
            command.Parameters.AddWithValue("@category", entry.Category);
            command.Parameters.AddWithValue("@sourceFile", Cut(entry.SourceFile, 400));
            command.Parameters.AddWithValue("@batch", entry.Batch.Length == 0 ? DBNull.Value : Cut(entry.Batch, 40));
            command.Parameters.AddWithValue("@payoutEntryId", entry.PayoutEntryId == 0 ? DBNull.Value : entry.PayoutEntryId);
            command.Parameters.AddWithValue("@runId", runId);

            if (await command.ExecuteScalarAsync(cancellationToken) is not string action) return null;

            await SaveAllocationAsync(connection, transaction, entry, allocated, cancellationToken);
            return action;
        }
    }

    private static async Task SaveAllocationAsync(
        SqlConnection connection, SqlTransaction transaction, CardEntry entry, decimal amount,
        CancellationToken cancellationToken)
    {
        await using (var clear = new SqlCommand(
            "DELETE FROM pay.Allocation WHERE PaymentId = @paymentId AND SettlementId IS NULL",
            connection, transaction))
        {
            clear.Parameters.AddWithValue("@paymentId", entry.PaymentId);
            await clear.ExecuteNonQueryAsync(cancellationToken);
        }

        if (entry.Document is not { } document || amount <= 0m) return;

        const string insert = """
            INSERT INTO pay.Allocation
                (PaymentId, DocType, DocId, DocLp, DocNumber, ContractorId, Amount, Score, Reason)
            VALUES (@paymentId, @docType, @docId, @docLp, @docNumber, @contractorId, @amount, 1.0, @reason)
            """;

        await using var command = new SqlCommand(insert, connection, transaction);
        command.Parameters.AddWithValue("@paymentId", entry.PaymentId);
        command.Parameters.AddWithValue("@docType", document.DocType);
        command.Parameters.AddWithValue("@docId", document.DocId);
        command.Parameters.AddWithValue("@docLp", document.DocLp);
        command.Parameters.AddWithValue("@docNumber", Cut(document.DocNumber, 50));
        command.Parameters.AddWithValue("@contractorId", document.ContractorId);
        command.Parameters.AddWithValue("@amount", amount);
        command.Parameters.AddWithValue("@reason", Cut($"transakcja kartą nr {document.Note}", 200));

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>The batches whose commission has not been booked, with their totals.</summary>
    public async Task<IReadOnlyList<CardBatch>> GetUnpaidBatchesAsync(CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT CardBatch,
                   SUM(CASE WHEN Direction = 'P' THEN Amount ELSE 0 END),
                   SUM(CASE WHEN Direction = 'R' THEN Amount ELSE 0 END),
                   COUNT(*), MAX(BookingDate)
            FROM pay.Payment
            WHERE PostingCategory = @category
              AND CardBatch IS NOT NULL
              AND CodPayoutEntryId IS NULL
            GROUP BY CardBatch
            """;

        var batches = new List<CardBatch>();

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 60 };
        command.Parameters.AddWithValue("@category", PaymentCategory.Polcard);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            var key = reader.GetString(0);
            var slash = key.IndexOf('/');
            if (slash <= 0) continue;

            batches.Add(new CardBatch(
                key[..slash], key[(slash + 1)..],
                reader.GetDecimal(1), reader.GetDecimal(2), reader.GetInt32(3), reader.GetDateTime(4)));
        }

        return batches;
    }

    /// <summary>The transfers already turned into a commission - one transfer, one commission.</summary>
    public async Task<HashSet<int>> GetUsedPayoutsAsync(CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT DISTINCT CodPayoutEntryId FROM pay.Payment
            WHERE PostingCategory IN (@sale, @fee) AND CodPayoutEntryId IS NOT NULL
            """;

        var used = new HashSet<int>();

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 60 };
        command.Parameters.AddWithValue("@sale", PaymentCategory.Polcard);
        command.Parameters.AddWithValue("@fee", PaymentCategory.PolcardFee);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) used.Add(reader.GetInt32(0));

        return used;
    }

    /// <summary>
    /// Books a transfer's commission and marks its batches paid by it, in one transaction: a batch
    /// marked without its commission, or a commission whose batches could be counted again, would
    /// each be wrong in its own way.
    /// </summary>
    /// <param name="bookFee">
    /// False when the commission is nothing: the batches are marked paid and no entry is written,
    /// because XL takes no entry for an amount of zero.
    /// </param>
    public async Task<bool> SaveCommissionAsync(
        CardEntry fee, IReadOnlyCollection<string> batchKeys, int runId, bool bookFee,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);

        if (bookFee && await SaveEntryAsync(connection, transaction, fee, runId, cancellationToken) is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        var parameters = batchKeys.Select((_, i) => $"@b{i}").ToArray();

        var sql = $"""
            UPDATE pay.Payment
            SET CodPayoutEntryId = @payout, LastUpdatedAt = SYSDATETIME()
            WHERE PostingCategory = @category
              AND CardBatch IN ({string.Join(", ", parameters)})
              AND CodPayoutEntryId IS NULL
            """;

        await using (var command = new SqlCommand(sql, connection, transaction) { CommandTimeout = 60 })
        {
            command.Parameters.AddWithValue("@payout", fee.PayoutEntryId);
            command.Parameters.AddWithValue("@category", PaymentCategory.Polcard);

            var index = 0;
            foreach (var key in batchKeys) command.Parameters.AddWithValue(parameters[index++], key);

            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    /// <summary>
    /// Messages whose XML attachment the couriers' step could not read and set aside.
    /// </summary>
    /// <remarks>
    /// Fiserv's reports reached the mailbox before the service knew them, and the couriers' step
    /// records whatever it does not recognise so as not to look at it twice. Those messages are
    /// looked at once more by this step - only those, as the XML is a rarity among the attachments
    /// and everything else in the mailbox stays read.
    /// </remarks>
    public async Task<HashSet<string>> GetUnrecognisedXmlMessagesAsync(CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT DISTINCT Uid FROM pay.CourierReport
            WHERE Status = 'Ignored' AND Format = '' AND FileName LIKE '%.xml'
            """;

        var uids = new HashSet<string>(StringComparer.Ordinal);

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 60 };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken)) uids.Add(reader.GetString(0));

        return uids;
    }

    /// <summary>
    /// Records that an XML attachment set aside by the couriers' step was looked at here and is
    /// not Fiserv's either, so that its message is not fetched again on every pass.
    /// </summary>
    public async Task MarkNotOursAsync(string uid, string fileName, CancellationToken cancellationToken = default)
    {
        const string sql = """
            UPDATE pay.CourierReport SET Format = 'NotFiserv'
            WHERE Uid = @uid AND FileName = @fileName AND Status = 'Ignored' AND Format = ''
            """;

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 60 };
        command.Parameters.AddWithValue("@uid", uid);
        command.Parameters.AddWithValue("@fileName", fileName);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Receipts, sales invoices and export invoices, and the corrections of each.</summary>
    private const string SalesDocumentTypes = "2033, 2034, 2037, 2041, 2042, 2045";

    private static string Cut(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}
