using Gaska.Payments.Application.Settlement;
using Gaska.Payments.Domain.Model;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

using Gaska.Payments.Domain.Couriers;
using Gaska.Payments.Erp;
using Gaska.Payments.Integrations.Couriers;
using Gaska.Payments.Integrations.Archive;

namespace Gaska.Payments.Application.Couriers;

/// <summary>What was made of one parcel, ready to be written down.</summary>
/// <param name="Confidence">
/// <c>High</c> when the documents were found and add up, <c>None</c> when they were not. Nothing in
/// between: a parcel either closes its invoice or waits for somebody to say what it closes.
/// </param>
public sealed record CodEntry(
    long PaymentId,
    string Register,
    string Courier,
    DateTime PayoutDate,
    string Waybill,
    decimal Amount,
    string Recipient,
    string Description,
    int ContractorId,
    string Confidence,
    string Notes,
    string SourceFile,
    IReadOnlyList<CodDocument> Documents);

/// <summary>
/// Writes the cash on delivery parcels into the service's own tables, and remembers which
/// messages have been read.
/// </summary>
/// <remarks>
/// The parcels go into <c>pay.Payment</c> alongside the bank operations rather than
/// into a table of their own. They are the same kind of thing - money in, to be matched against a
/// document - and everything downstream already handles that shape: posting to ERP, settling,
/// and the accountant's queue in the application. What tells them apart is
/// <c>PostingCategory = 'Cod'</c>.
/// </remarks>
public sealed class CodStore(IOptions<ErpOptions> options)
{
    private readonly string _connectionString = options.Value.ConnectionString;

    /// <summary>The UIDLs of messages whose attachments have already been read.</summary>
    public async Task<HashSet<string>> GetReadMessagesAsync(CancellationToken cancellationToken = default)
    {
        const string sql = "SELECT DISTINCT Uid FROM pay.CourierReport";

        var uids = new HashSet<string>(StringComparer.Ordinal);

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 60 };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken)) uids.Add(reader.GetString(0));

        return uids;
    }

    /// <summary>Records what became of an attachment, whatever that was.</summary>
    public async Task SaveAsync(CodReportRow row, CancellationToken cancellationToken = default)
    {
        const string sql = """
            MERGE pay.CourierReport AS target
            USING (SELECT @uid AS Uid, @fileName AS FileName) AS source
                ON target.Uid = source.Uid AND target.FileName = source.FileName
            WHEN MATCHED THEN UPDATE SET
                Courier = @courier, Format = @format, PayoutDate = @payoutDate,
                PayoutTotal = @payoutTotal, ParcelCount = @parcelCount, FilePath = @path,
                Status = @status, PayoutEntryId = @payoutEntryId, PayoutBookedOn = @bookedOn,
                PayoutAmount = @payoutAmount, Note = @note,
                NotifiedAt = CASE WHEN @notified = 1 THEN ISNULL(target.NotifiedAt, SYSDATETIME()) END,
                ReadAt = SYSDATETIME()
            WHEN NOT MATCHED THEN INSERT
                (Uid, FileName, ReceivedAt, Sender, Subject, Courier, Format,
                 PayoutDate, PayoutTotal, ParcelCount, FilePath, Status,
                 PayoutEntryId, PayoutBookedOn, PayoutAmount, Note, NotifiedAt, ReadAt)
            VALUES
                (@uid, @fileName, @receivedAt, @sender, @subject, @courier, @format,
                 @payoutDate, @payoutTotal, @parcelCount, @path, @status,
                 @payoutEntryId, @bookedOn, @payoutAmount, @note,
                 CASE WHEN @notified = 1 THEN SYSDATETIME() END, SYSDATETIME());
            """;

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 60 };

        command.Parameters.AddWithValue("@uid", Cut(row.Uid, 70));
        command.Parameters.AddWithValue("@fileName", Cut(row.FileName, 200));
        command.Parameters.AddWithValue("@receivedAt", row.ReceivedAt.UtcDateTime);
        command.Parameters.AddWithValue("@sender", Cut(row.Sender, 200));
        command.Parameters.AddWithValue("@subject", Cut(row.Subject, 300));
        command.Parameters.AddWithValue("@courier", Cut(row.Courier, 40));
        command.Parameters.AddWithValue("@format", Cut(row.Format, 20));
        command.Parameters.AddWithValue("@payoutDate", (object?)row.PayoutDate ?? DBNull.Value);
        command.Parameters.AddWithValue("@payoutTotal", (object?)row.PayoutTotal ?? DBNull.Value);
        command.Parameters.AddWithValue("@parcelCount", row.ParcelCount);
        command.Parameters.AddWithValue("@path", Cut(row.FilePath, 400));
        command.Parameters.AddWithValue("@status", row.Status);
        command.Parameters.AddWithValue("@payoutEntryId", (object?)row.PayoutEntryId ?? DBNull.Value);
        command.Parameters.AddWithValue("@bookedOn", (object?)row.PayoutBookedOn ?? DBNull.Value);
        command.Parameters.AddWithValue("@payoutAmount", (object?)row.PayoutAmount ?? DBNull.Value);
        command.Parameters.AddWithValue("@note", (object?)Cut(row.Note, 500) ?? DBNull.Value);
        command.Parameters.AddWithValue("@notified", row.Notified);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>The courier transfers already answering to a report.</summary>
    /// <remarks>
    /// One transfer pays for one report. Two reports could otherwise both recognise it - DPD and
    /// GLS name no reference and are matched on the waybills their titles list, and a title covering
    /// parcels from two files would answer to both.
    /// </remarks>
    public async Task<HashSet<int>> GetClaimedPayoutsAsync(CancellationToken cancellationToken = default)
    {
        const string sql = "SELECT PayoutEntryId FROM pay.CourierReport WHERE PayoutEntryId IS NOT NULL";

        var claimed = new HashSet<int>();

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 60 };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken)) claimed.Add(reader.GetInt32(0));

        return claimed;
    }

    /// <summary>The transfer one report ended up with, or null when it is still waiting.</summary>
    public async Task<int?> GetPayoutOfAsync(
        string uid, string fileName, CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT PayoutEntryId FROM pay.CourierReport WHERE Uid = @uid AND FileName = @fileName
            """;

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 60 };
        command.Parameters.AddWithValue("@uid", uid);
        command.Parameters.AddWithValue("@fileName", fileName);

        return await command.ExecuteScalarAsync(cancellationToken) is int entry ? entry : null;
    }

    /// <summary>
    /// The reports still waiting for their courier's transfer.
    /// </summary>
    /// <remarks>
    /// They are retried from the archived file rather than from the mailbox: POP3 has already been
    /// told, in effect, that the message was read, and a report can wait days - DPD sends its file
    /// with the payout date still in the future. The file on disk is the copy that lasts.
    /// </remarks>
    public async Task<IReadOnlyList<CodReportRow>> GetPendingAsync(CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT Uid, FileName, ReceivedAt, Sender, Subject, Courier, Format, FilePath, Status,
                   PayoutDate, PayoutTotal, ParcelCount
            FROM pay.CourierReport
            WHERE Status = 'Pending' AND FilePath <> ''
            ORDER BY PayoutDate, FileName
            """;

        var rows = new List<CodReportRow>();

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 60 };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new CodReportRow(
                reader.GetString(0), reader.GetString(1),
                new DateTimeOffset(reader.GetDateTime(2), TimeSpan.Zero),
                reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6),
                reader.GetString(7), reader.GetString(8))
            {
                PayoutDate = reader.IsDBNull(9) ? null : reader.GetDateTime(9),
                PayoutTotal = reader.IsDBNull(10) ? null : reader.GetDecimal(10),
                ParcelCount = reader.GetInt32(11),
            });
        }

        return rows;
    }

    /// <summary>
    /// The reports whose every parcel has been settled and whose courier transfer is still open.
    /// </summary>
    /// <remarks>
    /// This is what decides whether the collective transfer may be set aside. The rule is all or
    /// nothing: one parcel left unsettled means the transfer still has something to answer for, so
    /// it is left exactly as it is.
    ///
    /// Parcels are tied back to their report by the archived file they came out of - the path is
    /// unique per payout, carrying the courier, the date and the courier's own reference.
    /// </remarks>
    public async Task<IReadOnlyList<CodReportRow>> GetFullySettledAsync(
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT r.Uid, r.FileName, r.ReceivedAt, r.Sender, r.Subject, r.Courier, r.Format,
                   r.FilePath, r.Status, r.PayoutDate, r.PayoutTotal, r.ParcelCount,
                   r.PayoutEntryId, r.PayoutBookedOn, r.PayoutAmount
            FROM pay.CourierReport AS r
            CROSS APPLY (
                SELECT COUNT(*) AS Wszystkich,
                       SUM(CASE WHEN p.SettledAt IS NOT NULL THEN 1 ELSE 0 END) AS Rozliczonych
                FROM pay.Payment AS p
                WHERE p.PostingCategory = 'Cod' AND p.SourceFile = r.FilePath
            ) AS paczki
            WHERE r.Status = 'Posted'
              AND r.PayoutEntryId IS NOT NULL
              AND r.PayoutClosedAt IS NULL
              AND paczki.Wszystkich > 0
              AND paczki.Wszystkich = paczki.Rozliczonych
            """;

        var rows = new List<CodReportRow>();

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 120 };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new CodReportRow(
                reader.GetString(0), reader.GetString(1),
                new DateTimeOffset(reader.GetDateTime(2), TimeSpan.Zero),
                reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6),
                reader.GetString(7), reader.GetString(8))
            {
                PayoutDate = reader.IsDBNull(9) ? null : reader.GetDateTime(9),
                PayoutTotal = reader.IsDBNull(10) ? null : reader.GetDecimal(10),
                ParcelCount = reader.GetInt32(11),
                PayoutEntryId = reader.IsDBNull(12) ? null : reader.GetInt32(12),
                PayoutBookedOn = reader.IsDBNull(13) ? null : reader.GetDateTime(13),
                PayoutAmount = reader.IsDBNull(14) ? null : reader.GetDecimal(14),
            });
        }

        return rows;
    }

    /// <summary>
    /// Sets the courier's collective transfer aside as not subject to settlement, and says on it
    /// where the money went.
    /// </summary>
    /// <remarks>
    /// The same cash is already accounted for parcel by parcel in the COD register, so leaving the
    /// transfer open would leave a five-figure unidentified receipt in the accountant's queue for
    /// ever. The flag is only ever set once every parcel of the report has been settled.
    ///
    /// NOTE - this writes straight to a <c>CDN.*</c> table, as the split payment linking does, and
    /// for the same reason: the XL API has no function that modifies an existing cash entry. Only
    /// an entry that is still untouched (<c>KAZ_Rozliczony = 0</c>) is changed.
    /// </remarks>
    /// <returns>True when the entry was actually changed.</returns>
    public async Task<bool> CloseCodPayoutAsync(
        int entryId, string description, CancellationToken cancellationToken = default)
    {
        const string sql = """
            UPDATE CDN.Zapisy
            SET KAZ_Rozliczony = 2,
                KAZ_Opis = LEFT(LTRIM(RTRIM(ISNULL(KAZ_Opis, ''))
                           + CASE WHEN LTRIM(RTRIM(ISNULL(KAZ_Opis, ''))) = '' THEN '' ELSE ' | ' END
                           + @description), 255)
            WHERE KAZ_GIDNumer = @entry AND KAZ_Rozliczony = 0;

            UPDATE pay.CourierReport
            SET PayoutClosedAt = SYSDATETIME()
            WHERE PayoutEntryId = @entry;
            """;

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 60 };
        command.Parameters.AddWithValue("@entry", entryId);
        command.Parameters.AddWithValue("@description", Cut(description, 200));

        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    /// <summary>
    /// The waybills that already have a cash entry on the COD register.
    /// </summary>
    /// <remarks>
    /// This is what keeps the service off work the accountants have already done by hand, and what
    /// stops a report read twice from posting twice. It reads ERP, not our own table, because the
    /// entries made by hand are only in ERP - 167 030 of them - and they carry the waybill in the
    /// entry's text, in the form the accountants have always used.
    /// </remarks>
    public async Task<HashSet<string>> GetPostedWaybillsAsync(
        string register, DateTime from, CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT RTRIM(z.KAZ_Tresc)
            FROM CDN.Zapisy AS z
            INNER JOIN CDN.Raporty AS rap
                ON rap.KRP_GIDNumer = z.KAZ_KRPNumer AND rap.KRP_GIDTyp = z.KAZ_KRPTyp
            WHERE RTRIM(rap.KRP_Seria) = @register
              AND rap.KRP_DataOtwarcia >= DATEDIFF(DAY, '1800-12-28', @from)
              AND z.KAZ_Tresc LIKE 'Nr wys:%'
            """;

        var waybills = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 120 };
        command.Parameters.AddWithValue("@register", register);

        // Reports are looked at from a fortnight before the first payout we handle: a payout is
        // dated after the parcels it pays for, so an entry made by hand can sit on an earlier day.
        command.Parameters.AddWithValue("@from", from.Date.AddDays(-14));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            if (CodDescription.WaybillOf(reader.GetString(0)) is { Length: > 0 } waybill) waybills.Add(waybill);
        }

        return waybills;
    }

    /// <summary>
    /// Writes one parcel and the documents it is to close.
    /// </summary>
    /// <remarks>
    /// An operator's decision is protected exactly as it is for bank operations: a row they have
    /// accepted, rejected or that we have already posted is left alone, so re-reading a report
    /// cannot undo anybody's work.
    /// </remarks>
    public async Task<bool> SaveEntryAsync(CodEntry entry, int runId, CancellationToken cancellationToken = default)
    {
        const string upsert = """
            MERGE pay.Payment AS target
            USING (SELECT @paymentId AS PaymentId) AS source
                ON target.PaymentId = source.PaymentId
            WHEN MATCHED AND target.Status IN ('Proposed', 'SettledInErp', 'NoSettlement')
            THEN UPDATE SET
                ContractorId = @contractorId, ContractorSource = @contractorSource,
                Confidence = @confidence, Notes = @notes, SourceFile = @sourceFile,
                AllocatedAmount = @allocated, UnallocatedAmount = @unallocated,
                LastUpdatedAt = SYSDATETIME(), LastRunId = @runId
            WHEN NOT MATCHED THEN INSERT
                (PaymentId, BankExternalId, CreditedAccount, BookingDate, Amount, Currency,
                 PayerName, PayerAccount, Description, ContractorId, ContractorSource,
                 ContractorFromBankAccount, Confidence, Strategy, AllocatedAmount, UnallocatedAmount,
                 Notes, Status, Direction, RegisterSeries, PostingCategory, SourceFile,
                 FirstSeenAt, LastUpdatedAt, LastRunId)
            VALUES
                (@paymentId, @waybill, '', @bookingDate, @amount, 'PLN',
                 @payerName, '', @description, @contractorId, @contractorSource,
                 1, @confidence, @strategy, @allocated, @unallocated,
                 @notes, 'Proposed', 'P', @register, @category, @sourceFile,
                 SYSDATETIME(), SYSDATETIME(), @runId)
            OUTPUT $action;
            """;

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);

        var allocated = entry.Documents.Sum(d => d.Remaining);

        await using (var command = new SqlCommand(upsert, connection, transaction))
        {
            command.Parameters.AddWithValue("@paymentId", entry.PaymentId);
            command.Parameters.AddWithValue("@waybill", Cut(entry.Waybill, 64));
            command.Parameters.AddWithValue("@bookingDate", entry.PayoutDate.Date);
            command.Parameters.AddWithValue("@amount", entry.Amount);
            command.Parameters.AddWithValue("@payerName", Cut(entry.Recipient, 140));
            command.Parameters.AddWithValue("@description", Cut(entry.Description, 500));
            command.Parameters.AddWithValue("@contractorId", entry.ContractorId);
            command.Parameters.AddWithValue("@contractorSource", $"pobranie {entry.Courier}");
            command.Parameters.AddWithValue("@confidence", entry.Confidence);
            command.Parameters.AddWithValue("@strategy", "CashOnDelivery");
            command.Parameters.AddWithValue("@allocated", Math.Min(allocated, entry.Amount));
            command.Parameters.AddWithValue("@unallocated", Math.Max(0m, entry.Amount - allocated));
            command.Parameters.AddWithValue("@notes", Cut(entry.Notes, 1000));
            command.Parameters.AddWithValue("@register", entry.Register);
            command.Parameters.AddWithValue("@category", PaymentCategory.Cod);
            command.Parameters.AddWithValue("@sourceFile", Cut(entry.SourceFile, 400));
            command.Parameters.AddWithValue("@runId", runId);

            if (await command.ExecuteScalarAsync(cancellationToken) is not string action)
            {
                // Nothing matched and nothing was inserted: the operator has ruled on this parcel
                // and their decision stands.
                await transaction.RollbackAsync(cancellationToken);
                return false;
            }

            await SaveAllocationsAsync(connection, transaction, entry, cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            return action == "INSERT";
        }
    }

    private static async Task SaveAllocationsAsync(
        SqlConnection connection, SqlTransaction transaction, CodEntry entry,
        CancellationToken cancellationToken)
    {
        await using (var clear = new SqlCommand(
            "DELETE FROM pay.Allocation WHERE PaymentId = @paymentId AND SettlementId IS NULL",
            connection, transaction))
        {
            clear.Parameters.AddWithValue("@paymentId", entry.PaymentId);
            await clear.ExecuteNonQueryAsync(cancellationToken);
        }

        const string insert = """
            INSERT INTO pay.Allocation
                (PaymentId, DocType, DocId, DocLp, DocNumber, ContractorId, Amount, Score, Reason)
            VALUES (@paymentId, @docType, @docId, @docLp, @docNumber, @contractorId, @amount, 1.0, @reason)
            """;

        // The parcel pays as much of each document as is left on it, never more than the parcel
        // itself is worth - two invoices in one parcel share the amount in the order they were
        // named.
        var left = entry.Amount;

        foreach (var document in entry.Documents)
        {
            var amount = Math.Min(document.Remaining, left);
            if (amount <= 0m) break;

            left -= amount;

            await using var command = new SqlCommand(insert, connection, transaction);
            command.Parameters.AddWithValue("@paymentId", entry.PaymentId);
            command.Parameters.AddWithValue("@docType", document.DocType);
            command.Parameters.AddWithValue("@docId", document.DocId);
            command.Parameters.AddWithValue("@docLp", document.DocLp);
            command.Parameters.AddWithValue("@docNumber", Cut(document.DocNumber, 50));
            command.Parameters.AddWithValue("@contractorId", document.ContractorId);
            command.Parameters.AddWithValue("@amount", amount);
            command.Parameters.AddWithValue("@reason", Cut($"pobranie {entry.Courier}, list {entry.Waybill}", 200));

            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static string? Cut(string? value, int maxLength) =>
        value is null || value.Length <= maxLength ? value : value[..maxLength];
}
