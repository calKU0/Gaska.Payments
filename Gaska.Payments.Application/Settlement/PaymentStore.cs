using System.Text;
using Gaska.Payments.Domain.Model;
using Microsoft.Data.SqlClient;

namespace Gaska.Payments.Application.Settlement;

/// <summary>
/// Writing settlement proposals to the service's own <c>pay.*</c> tables.
/// </summary>
public sealed class PaymentStore(string connectionString)
{
    /// <summary>Creates the service's missing tables.</summary>
    public Task EnsureSchemaAsync(CancellationToken cancellationToken = default) =>
        PaymentSchema.EnsureCreatedAsync(connectionString, cancellationToken);

    /// <summary>Opens a new run and returns its identifier.</summary>
    public async Task<int> StartRunAsync(DateTime periodFrom, DateTime periodTo, CancellationToken cancellationToken = default)
    {
        const string sql = """
            INSERT INTO pay.Run (StartedAt, PeriodFrom, PeriodTo, Status)
            OUTPUT INSERTED.RunId
            VALUES (SYSDATETIME(), @periodFrom, @periodTo, @status);
            """;

        await using var connection = await OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@periodFrom", periodFrom.Date);
        command.Parameters.AddWithValue("@periodTo", periodTo.Date);
        command.Parameters.AddWithValue("@status", RunStatus.Running);

        return (int)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    public async Task CompleteRunAsync(
        int runId, int fetched, int proposed, string? error, CancellationToken cancellationToken = default)
    {
        const string sql = """
            UPDATE pay.Run
            SET FinishedAt = SYSDATETIME(),
                PaymentsFetched = @fetched,
                PaymentsProposed = @proposed,
                Status = @status,
                ErrorMessage = @error
            WHERE RunId = @runId;
            """;

        await using var connection = await OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@runId", runId);
        command.Parameters.AddWithValue("@fetched", fetched);
        command.Parameters.AddWithValue("@proposed", proposed);
        command.Parameters.AddWithValue("@status", error is null ? RunStatus.Succeeded : RunStatus.Failed);
        command.Parameters.AddWithValue("@error", (object?)Truncate(error, 2000) ?? DBNull.Value);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// How many proposals go into one command.
    /// </summary>
    /// <remarks>
    /// SQL Server takes 2100 parameters per command and a proposal costs 28 of them plus nine per
    /// allocation, so a hundred leaves room to spare even when every one of them carries several
    /// documents; the builder closes a batch early if it ever gets close.
    /// </remarks>
    private const int BatchSize = 100;

    /// <summary>The ceiling the builder keeps away from - the limit is 2100.</summary>
    private const int ParameterCeiling = 1900;

    /// <summary>
    /// Writes the proposals. Rows the operator has already approved, rejected or posted are left
    /// untouched - a human decision outweighs another run of the engine.
    /// </summary>
    /// <remarks>
    /// The proposals go over in batches rather than one at a time. Each one used to cost two or
    /// three round trips - the upsert, then either the allocations or a backfill - and on a
    /// thousand-operation day that was two minutes, four times the rest of the cycle put together
    /// and by far its slowest step. The work is the same; what changed is how often we wait for the
    /// network.
    /// </remarks>
    /// <returns>The number of rows written, new or refreshed.</returns>
    public async Task<int> SaveAsync(
        int runId, IReadOnlyList<SettlementProposal> proposals, CancellationToken cancellationToken = default)
    {
        if (proposals.Count == 0) return 0;

        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            var saved = 0;

            foreach (var batch in Batches(proposals))
            {
                saved += await SaveBatchAsync(connection, transaction, runId, batch, cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            return saved;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    /// <summary>
    /// Splits the proposals into batches small enough for one command, counting the parameters
    /// rather than only the rows - a proposal with a dozen documents is not the same size as one
    /// with none.
    /// </summary>
    private static IEnumerable<List<SettlementProposal>> Batches(IReadOnlyList<SettlementProposal> proposals)
    {
        var batch = new List<SettlementProposal>(BatchSize);
        var parameters = 0;

        foreach (var proposal in proposals)
        {
            var cost = 28 + (proposal.Result.Allocations.Count * 9);

            if (batch.Count > 0 && (batch.Count >= BatchSize || parameters + cost > ParameterCeiling))
            {
                yield return batch;
                batch = new List<SettlementProposal>(BatchSize);
                parameters = 0;
            }

            batch.Add(proposal);
            parameters += cost;
        }

        if (batch.Count > 0) yield return batch;
    }

    private static async Task<int> SaveBatchAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        int runId,
        List<SettlementProposal> batch,
        CancellationToken cancellationToken)
    {
        var sql = new StringBuilder();
        await using var command = new SqlCommand { Connection = connection, Transaction = transaction };

        // One table variable for the whole batch: the upserts record what they did in it, and each
        // proposal then reads back its own row to decide between writing allocations and filling in
        // the facts on a row somebody else owns.
        sql.AppendLine("DECLARE @acted TABLE (PaymentId BIGINT PRIMARY KEY);");
        sql.AppendLine();

        command.Parameters.AddWithValue("@runId", runId);
        command.Parameters.AddWithValue("@proposedStatus", SettlementStatus.Proposed);
        command.Parameters.AddWithValue("@settledStatus", SettlementStatus.SettledInErp);
        command.Parameters.AddWithValue("@noSettlementStatus", SettlementStatus.NoSettlement);

        for (var i = 0; i < batch.Count; i++) Append(sql, command, i, batch[i]);

        sql.AppendLine("SELECT COUNT(*) FROM @acted;");

        command.CommandText = sql.ToString();
        command.CommandTimeout = 300;

        return await command.ExecuteScalarAsync(cancellationToken) as int? ?? 0;
    }

    /// <summary>Writes one proposal into the batch: the upsert, then whichever follow-up applies.</summary>
    private static void Append(StringBuilder sql, SqlCommand command, int index, SettlementProposal proposal)
    {
        var result = proposal.Result;
        var payment = result.Payment;
        var p = $"@p{index}_";

        void Add(string name, object? value) => command.Parameters.AddWithValue(p + name, value ?? DBNull.Value);

        Add("paymentId", payment.Id);
        Add("externalId", Truncate(payment.ExternalId, 64) ?? string.Empty);
        Add("creditedAccount", Truncate(payment.CreditedAccount, 34) ?? string.Empty);
        Add("bookingDate", payment.BookingDate.Date);
        Add("amount", payment.Amount);
        Add("currency", Truncate(payment.Currency, 3) ?? string.Empty);
        Add("payerName", Truncate(payment.PayerName, 140) ?? string.Empty);
        Add("payerAccount", Truncate(payment.PayerAccount, 34) ?? string.Empty);
        Add("description", Truncate(payment.Description, 500) ?? string.Empty);
        Add("contractorId", result.ContractorId);
        Add("contractorSource", Truncate(result.ContractorSource, 80) ?? string.Empty);
        Add("fromBankAccount", result.ContractorFromBankAccount);
        Add("endToEndId", Truncate(payment.EndToEndId, 64) ?? string.Empty);
        Add("bankBic", Truncate(payment.CounterpartyBankBic, 20) ?? string.Empty);
        Add("bankName", Truncate(payment.CounterpartyBankName, 100) ?? string.Empty);
        Add("bankClearing", Truncate(payment.CounterpartyBankClearing, 20) ?? string.Empty);
        Add("confidence", result.Confidence.ToString());
        Add("strategy", result.Strategy.ToString());
        Add("allocated", result.AllocatedTotal);
        Add("unallocated", result.Unallocated);
        Add("notes", Truncate(string.Join(" ", result.Notes), 1000) ?? string.Empty);
        Add("status", proposal.Status);
        Add("erpEntryId", proposal.ErpEntryId);
        Add("direction", payment.IsIncoming ? "P" : "R");
        Add("registerSeries", proposal.RegisterSeries);
        Add("postingCategory", proposal.PostingCategory);

        // Every state the engine assigned is overwritten: proposals, payments settled outside the
        // service, and operations that are not settled at all. Only Accepted, Rejected and Posted
        // are protected - the operator's decision and what we posted in ERP.
        //
        // Lowering the confidence level is deliberately NOT blocked. An earlier "never make a
        // proposal worse" rule meant a change to the engine's rules never reached existing rows.
        sql.AppendLine($"""
            MERGE pay.Payment AS target
            USING (SELECT {p}paymentId AS PaymentId) AS source
                ON target.PaymentId = source.PaymentId
            WHEN MATCHED AND target.Status IN (@proposedStatus, @settledStatus, @noSettlementStatus)
            THEN UPDATE SET
                ContractorId      = {p}contractorId,
                ContractorSource  = {p}contractorSource,
                ContractorFromBankAccount = {p}fromBankAccount,
                EndToEndId        = {p}endToEndId,
                BankBic           = {p}bankBic,
                BankName          = {p}bankName,
                BankClearing      = {p}bankClearing,
                Confidence        = {p}confidence,
                Strategy          = {p}strategy,
                AllocatedAmount   = {p}allocated,
                UnallocatedAmount = {p}unallocated,
                Notes             = {p}notes,
                Status            = {p}status,
                ErpEntryId        = ISNULL(target.ErpEntryId, {p}erpEntryId),
                Direction         = {p}direction,
                RegisterSeries    = {p}registerSeries,
                PostingCategory   = {p}postingCategory,
                LastUpdatedAt     = SYSDATETIME(),
                LastRunId         = @runId
            WHEN NOT MATCHED THEN INSERT
                (PaymentId, BankExternalId, CreditedAccount, BookingDate, Amount, Currency,
                 PayerName, PayerAccount, Description, ContractorId, ContractorSource,
                 ContractorFromBankAccount, EndToEndId, BankBic, BankName, BankClearing,
                 Confidence, Strategy, AllocatedAmount, UnallocatedAmount, Notes,
                 Status, ErpEntryId, Direction, RegisterSeries, PostingCategory,
                 FirstSeenAt, LastUpdatedAt, LastRunId)
            VALUES
                ({p}paymentId, {p}externalId, {p}creditedAccount, {p}bookingDate, {p}amount, {p}currency,
                 {p}payerName, {p}payerAccount, {p}description, {p}contractorId, {p}contractorSource,
                 {p}fromBankAccount, {p}endToEndId, {p}bankBic, {p}bankName, {p}bankClearing,
                 {p}confidence, {p}strategy, {p}allocated, {p}unallocated, {p}notes,
                 {p}status, {p}erpEntryId, {p}direction, {p}registerSeries, {p}postingCategory,
                 SYSDATETIME(), SYSDATETIME(), @runId)
            OUTPUT inserted.PaymentId INTO @acted;
            """);

        sql.AppendLine($"IF EXISTS (SELECT 1 FROM @acted WHERE PaymentId = {p}paymentId)");
        sql.AppendLine("BEGIN");
        sql.AppendLine($"    DELETE FROM pay.Allocation WHERE PaymentId = {p}paymentId;");

        if (result.Allocations.Count > 0)
        {
            var rows = new List<string>(result.Allocations.Count);

            for (var a = 0; a < result.Allocations.Count; a++)
            {
                var allocation = result.Allocations[a];
                var q = $"{p}a{a}_";

                command.Parameters.AddWithValue($"{q}docType", allocation.Receivable.PaymentDocType);
                command.Parameters.AddWithValue($"{q}docId", allocation.Receivable.PaymentDocId);
                command.Parameters.AddWithValue($"{q}docLp", allocation.Receivable.PaymentLp);
                command.Parameters.AddWithValue($"{q}docNumber",
                    Truncate(allocation.Receivable.DocumentNumber, 50) ?? string.Empty);
                command.Parameters.AddWithValue($"{q}contractorId", allocation.Receivable.ContractorId);
                command.Parameters.AddWithValue($"{q}amount", allocation.Amount);
                command.Parameters.AddWithValue($"{q}score", Math.Round((decimal)allocation.Score, 3));
                command.Parameters.AddWithValue($"{q}reason", Truncate(allocation.Reason, 200) ?? string.Empty);

                rows.Add($"({p}paymentId, {q}docType, {q}docId, {q}docLp, {q}docNumber, "
                         + $"{q}contractorId, {q}amount, {q}score, {q}reason)");
            }

            sql.AppendLine("    INSERT INTO pay.Allocation");
            sql.AppendLine("        (PaymentId, DocType, DocId, DocLp, DocNumber, ContractorId, Amount, Score, Reason)");
            sql.AppendLine("    VALUES " + string.Join(", ", rows) + ";");
        }

        sql.AppendLine("END");

        // Nothing happened: the row exists and the operator has already worked on it. The proposal
        // is left alone, but direction, register and kind are filled in - those are facts about the
        // bank operation, and without a register it could never be posted.
        sql.AppendLine("ELSE");
        sql.AppendLine("BEGIN");
        sql.AppendLine($"""
                UPDATE pay.Payment
                SET Direction = {p}direction,
                    RegisterSeries = {p}registerSeries,
                    PostingCategory = {p}postingCategory
                WHERE PaymentId = {p}paymentId
                  AND (Direction <> {p}direction
                       OR ISNULL(RegisterSeries, '') <> ISNULL({p}registerSeries, '')
                       OR PostingCategory <> {p}postingCategory);
            """);
        sql.AppendLine("END");
        sql.AppendLine();
    }

    private async Task<SqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static string? Truncate(string? value, int maxLength) =>
        value is null || value.Length <= maxLength ? value : value[..maxLength];
}
