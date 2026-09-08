using Gaska.Payments.Erp;
using Microsoft.Data.SqlClient;

using Gaska.Payments.Domain.Model;

namespace Gaska.Payments.Application.Posting;

/// <summary>A bank operation waiting for its ERP cash entry to be created.</summary>
/// <param name="OperationSymbol">
/// The cash operation symbol derived from the register's configuration - it depends on the
/// direction of the operation and on whether the contractor was recognised.
/// </param>
public sealed record PendingOperation(
    long PaymentId,
    string ExternalId,
    string RegisterSeries,
    DateTime BookingDate,
    decimal Amount,
    string Currency,
    bool IsIncoming,
    int ContractorId,
    string Description,
    string PayerName,
    string Confidence,
    string Status,
    string OperationSymbol,
    string PostingCategory,
    bool ContractorFromBankAccount,
    string DocumentNumbers = "")
{
    /// <summary>The number written on the ERP entry (<c>KAZ_NumerDokumentu</c>).</summary>
    /// <remarks>
    /// For a bank operation it is the bank's own reference, which is what identifies the entry
    /// again should the process break off. Cash on delivery carries the invoice number instead:
    /// that is what the accountants have always put there and what they look for on the register,
    /// while the parcel itself is identified by the waybill in the entry's text.
    /// </remarks>
    public string EntryNumber =>
        PostingCategory == PaymentCategory.Cod && DocumentNumbers.Length > 0
            ? DocumentNumbers
            : ExternalId;

    /// <summary>
    /// The party that may be put on the cash entry (0 means the entry is booked on the
    /// anonymous party).
    /// </summary>
    /// <remarks>
    /// A contractor recognised from a payment title or from the payer name is circumstantial
    /// evidence only - on our side it stays as a hint for the operator, but the ERP entry goes in
    /// with no party. Split payment legs are the exception: there the party is always our own
    /// company, not the sender.
    ///
    /// Operations from registers without settlement - cards and auxiliary accounts - always go on
    /// the anonymous party, even when the counterparty account did identify a contractor: there
    /// is nothing there to settle with them.
    ///
    /// Cash on delivery is the other exception. There the party is not guessed at all: the parcel
    /// is tied to its invoice through the shipment ERP itself recorded, so whoever the invoice is
    /// made out to is who paid.
    /// </remarks>
    public int ErpContractorId => PostingCategory switch
    {
        PaymentCategory.Card or PaymentCategory.PostOnly => 0,
        PaymentCategory.SplitPayment or PaymentCategory.Cod => ContractorId,
        _ => ContractorFromBankAccount ? ContractorId : 0,
    };
}

/// <summary>An entry already created in ERP that is waiting only to be settled.</summary>
public sealed record PendingSettlement(
    long PaymentId, int EntryId, decimal Amount, string RegisterSeries, int ContractorId);

/// <summary>A settlement line: the document payment part of the entry is to go to.</summary>
public sealed record PendingAllocation(
    int AllocationId,
    int DocType,
    int DocId,
    int DocLp,
    string DocNumber,
    decimal Amount);

/// <summary>
/// Data access for posting: it reads from our own <c>dbo</c> tables and from <c>CDN.Raporty</c>,
/// and writes to <c>dbo</c> only. The ERP documents themselves are created through the XL API.
/// </summary>
public sealed class PostingRepository(string connectionString)
{
    /// <summary>
    /// Clears pointers to cash entries that are no longer in ERP because someone deleted them by
    /// hand. The next pass then creates them again instead of treating the operation as posted.
    /// </summary>
    /// <returns>The number of operations released for posting again.</returns>
    public async Task<int> ReleaseMissingEntriesAsync(CancellationToken cancellationToken = default)
    {
        const string sql = """
            UPDATE p
            SET p.ErpEntryId = NULL, p.ErpReportId = NULL, p.PostedAt = NULL,
                p.SettledAt = NULL,
                p.Status = CASE WHEN p.Status = 'Posted' THEN 'Proposed' ELSE p.Status END
            FROM pay.Payment AS p
            WHERE p.ErpEntryId IS NOT NULL
              AND NOT EXISTS (SELECT 1 FROM CDN.Zapisy AS z WHERE z.KAZ_GIDNumer = p.ErpEntryId);

            UPDATE a
            SET a.SettlementId = NULL
            FROM pay.Allocation AS a
            JOIN pay.Payment AS p ON p.PaymentId = a.PaymentId
            WHERE a.SettlementId IS NOT NULL AND p.ErpEntryId IS NULL;
            """;

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 120 };
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Finds cash entries that were created in ERP but never recorded on our side - after the
    /// process broke off midway, for instance.
    /// </summary>
    /// <remarks>
    /// For bank operations the link runs through the bank reference, which we store in
    /// <c>KAZ_NumerDokumentu</c> - up until the entry is settled, when that column is rewritten
    /// with the numbers of the documents it closed (see
    /// <see cref="CashEntrySql.SetEntryDocumentNumber"/>). Nothing is lost by that: a settled
    /// operation already holds its <c>ErpEntryId</c> and never reaches this pass, and were our
    /// table ever rebuilt the content-based pairing below would find the entry anyway.
    ///
    /// Cash on delivery is matched on the waybill in the entry's text instead, and only within its
    /// own register. Its entries carry the invoice number in <c>KAZ_NumerDokumentu</c> - the form
    /// the accountants have always used and which we keep - so that column identifies nothing
    /// there. This is also what stops the service from posting a parcel somebody entered by hand:
    /// the entry is found and adopted rather than created a second time.
    /// </remarks>
    /// <returns>The number of links recovered.</returns>
    public async Task<int> AdoptOrphanedEntriesAsync(CancellationToken cancellationToken = default)
    {
        var sql = $"""
            UPDATE p
            SET p.ErpEntryId = z.KAZ_GIDNumer, p.PostedAt = SYSDATETIME()
            FROM pay.Payment AS p
            CROSS APPLY (
                SELECT TOP 1 z.KAZ_GIDNumer
                FROM CDN.Zapisy AS z
                WHERE z.KAZ_NumerDokumentu = p.BankExternalId
                ORDER BY z.KAZ_GIDNumer DESC
            ) AS z
            WHERE p.ErpEntryId IS NULL AND p.BankExternalId <> ''
              AND p.PostingCategory <> 'Cod';

            UPDATE p
            SET p.ErpEntryId = z.KAZ_GIDNumer, p.PostedAt = SYSDATETIME()
            FROM pay.Payment AS p
            CROSS APPLY (
                SELECT TOP 1 z.KAZ_GIDNumer
                FROM CDN.Zapisy AS z
                INNER JOIN CDN.Raporty AS rap
                    ON rap.KRP_GIDNumer = z.KAZ_KRPNumer AND rap.KRP_GIDTyp = z.KAZ_KRPTyp
                WHERE RTRIM(rap.KRP_Seria) = p.RegisterSeries
                  AND z.KAZ_Tresc LIKE 'Nr wys:' + p.BankExternalId + '%'
                ORDER BY z.KAZ_GIDNumer DESC
            ) AS z
            WHERE p.ErpEntryId IS NULL AND p.BankExternalId <> ''
              AND p.PostingCategory = 'Cod';

            -- Somebody else's entries for our operations: entered by hand, or written by ERP's
            -- own statement import. Paired off one to one, so a group of look-alikes cannot all
            -- claim the same entry.
            {PairWithExistingEntries}
            """;

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 300 };
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Pairs our unposted operations with the entries somebody else already put in ERP for them.
    /// </summary>
    /// <remarks>
    /// The bank's reference is no help. The accountants enter operations by hand - all 31 VAT legs
    /// on FORPL for 2026-08-31 were theirs - and ERP's own statement import writes its own
    /// numbering, so neither puts anything in <c>KAZ_NumerDokumentu</c>, which is where we keep it.
    /// The operation has to be recognised by what the bank actually sent: register, day, direction,
    /// amount and payment title.
    ///
    /// The title is compared with its whitespace removed and cut to 40 characters, because the bank
    /// breaks the same text at different places on the two sides and ERP stores the pieces run
    /// together.
    ///
    /// That key does not identify an operation uniquely, and cannot: seven bank commissions of
    /// 0,09 EUR arrived on FORUE on one day, word for word alike. So the two sides are numbered
    /// within each group and paired off one to one - the n-th of ours takes the n-th free entry.
    /// Whichever way round they are paired makes no difference, because within a group they are
    /// indistinguishable; what matters is that four of ours can never all claim one entry, and that
    /// three entries leave four operations still to post.
    /// </remarks>
    private const string PairWithExistingEntries = """
        WITH ours AS (
            SELECT p.PaymentId,
                   p.RegisterSeries, p.BookingDate, p.Amount, p.Direction,
                   LEFT(REPLACE(p.Description, ' ', ''), 40) AS TitleKey,
                   ROW_NUMBER() OVER (
                       PARTITION BY p.RegisterSeries, p.BookingDate, p.Amount, p.Direction,
                                    LEFT(REPLACE(p.Description, ' ', ''), 40)
                       ORDER BY p.PaymentId) AS Ordinal
            FROM pay.Payment AS p
            WHERE p.ErpEntryId IS NULL
              AND p.PostingCategory <> 'Cod'
              AND p.Description <> ''
        ),
        unclaimed AS (
            SELECT z.KAZ_GIDNumer AS EntryId,
                   RTRIM(rap.KRP_Seria) AS Series,
                   rap.KRP_DataOtwarcia AS Day,
                   z.KAZ_Kwota AS Amount,
                   z.KAZ_RP AS Side,
                   LEFT(REPLACE(RTRIM(ISNULL(z.KAZ_Tresc, '')), ' ', ''), 40) AS TitleKey,
                   ROW_NUMBER() OVER (
                       PARTITION BY RTRIM(rap.KRP_Seria), rap.KRP_DataOtwarcia, z.KAZ_Kwota, z.KAZ_RP,
                                    LEFT(REPLACE(RTRIM(ISNULL(z.KAZ_Tresc, '')), ' ', ''), 40)
                       ORDER BY z.KAZ_GIDNumer) AS Ordinal
            FROM CDN.Zapisy AS z
            INNER JOIN CDN.Raporty AS rap
                ON rap.KRP_GIDNumer = z.KAZ_KRPNumer AND rap.KRP_GIDTyp = z.KAZ_KRPTyp
            WHERE NOT EXISTS (SELECT 1 FROM pay.Payment AS claimed
                              WHERE claimed.ErpEntryId = z.KAZ_GIDNumer)
        )
        UPDATE p
        SET p.ErpEntryId = u.EntryId, p.PostedAt = SYSDATETIME()
        FROM pay.Payment AS p
        INNER JOIN ours AS o ON o.PaymentId = p.PaymentId
        INNER JOIN unclaimed AS u
            ON u.Series = o.RegisterSeries
           AND u.Day = DATEDIFF(DAY, '1800-12-28', o.BookingDate)
           AND u.Amount = o.Amount
           AND u.Side = CASE WHEN o.Direction = 'P' THEN 2 ELSE 1 END
           AND u.TitleKey = o.TitleKey
           AND u.Ordinal = o.Ordinal;
        """;

    /// <summary>
    /// Whether the register has a report from a later day than the operation - which is what makes
    /// the operation impossible to post.
    /// </summary>
    /// <remarks>
    /// ERP refuses an entry whose day is not the register's newest report (<c>XLDodajZapis</c>
    /// answers 8158), and a day once passed never comes back. Such an operation would fail on every
    /// pass for ever - a wasted API call each time and a line in the error log that hides the real
    /// problems - so it is kept out of the posting query altogether and reported separately.
    ///
    /// It does not apply in buffer mode: an entry in the buffer hangs off the register rather than
    /// off a report, and the day does not come into it.
    /// </remarks>
    private const string PastDay = """
        EXISTS (
            SELECT 1
            FROM CDN.Raporty AS newer
            WHERE RTRIM(newer.KRP_Seria) = p.RegisterSeries
              AND newer.KRP_DataOtwarcia > DATEDIFF(DAY, '1800-12-28', p.BookingDate)
        )
        """;

    /// <summary>
    /// The operations no longer possible to post, because their register has moved on to a later
    /// day. They are money that has not reached ERP and that nothing will put there by itself.
    /// </summary>
    public async Task<IReadOnlyList<(string Register, DateTime Day, int Count, decimal Amount)>>
        GetStrandedAsync(
            DateTime from, IReadOnlyCollection<string> registers,
            CancellationToken cancellationToken = default)
    {
        if (registers.Count == 0) return [];

        var parameters = registers.Select((_, i) => $"@r{i}").ToArray();

        var sql = $"""
            SELECT p.RegisterSeries, p.BookingDate, COUNT(*), SUM(p.Amount)
            FROM pay.Payment AS p
            WHERE p.ErpEntryId IS NULL
              AND p.BookingDate >= @from
              AND p.RegisterSeries IN ({string.Join(", ", parameters)})
              AND {PastDay}
            GROUP BY p.RegisterSeries, p.BookingDate
            ORDER BY p.RegisterSeries, p.BookingDate
            """;

        var rows = new List<(string, DateTime, int, decimal)>();

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 120 };
        command.Parameters.AddWithValue("@from", from.Date);

        var index = 0;
        foreach (var register in registers) command.Parameters.AddWithValue(parameters[index++], register);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add((reader.GetString(0), reader.GetDateTime(1), reader.GetInt32(2), reader.GetDecimal(3)));
        }

        return rows;
    }

    /// <summary>
    /// Nothing in the posting query guards against creating a second entry for an operation ERP
    /// already holds, and nothing needs to: the pairing above runs first on every pass and gives
    /// such an operation its <c>ErpEntryId</c>, after which this query no longer sees it.
    /// </summary>
    /// <summary>The connection string - the posting journal opens connections itself, synchronously.</summary>
    public string ConnectionString => connectionString;

    /// <summary>
    /// Fills in the split payment details on cash entries: the link from the VAT leg to the gross
    /// payment (<c>KAZ_SplitPNumer</c>) and the "not subject to settlement" flag
    /// (<c>KAZ_Rozliczony = 2</c>).
    /// </summary>
    /// <remarks>
    /// NOTE - this is the one place where we write directly to a <c>CDN.*</c> table, with the
    /// explicit consent of the system's owner. The split payment link cannot be created through
    /// the XL API: the <c>XLZapisKasowyInfo</c> structure has no <c>SplitPNumer</c> field, and
    /// there is neither a function that modifies an existing entry nor a suitable ERP procedure.
    /// The "not subject" state is set here only when the cash operation's definition
    /// (<c>KAO_NieRozliczaj</c>) did not already set it.
    ///
    /// Pairs are found by register, date and identical payment text - the bank sends the same
    /// split payment message on both legs, and the legs run in opposite directions. Only
    /// unambiguous pairs are linked; at the least ambiguity the entries are left untouched. On the
    /// VAT account no link is set - the second leg simply does not occur there.
    ///
    /// Only entries created by the automat (<c>PostedAt IS NOT NULL</c>) are touched. The proposal
    /// table also points at entries adopted from the CDC import - those are somebody else's work
    /// and stay untouched, even where our classification differs from the one the import gave them.
    /// </remarks>
    /// <returns>The number of pairs linked.</returns>
    public async Task<int> LinkSplitPaymentsAsync(CancellationToken cancellationToken = default)
    {
        const string sql = """
            -- Legs are matched on the split payment header: /VAT/amount/IDC/taxid/INV/number.
            -- The payer's system generates it, so it is identical on both legs - unlike the rest
            -- of the description, which the bank can render with small differences in spacing and
            -- punctuation.
            WITH Nogi AS (
                SELECT PaymentId, ErpEntryId, RegisterSeries, BookingDate, Direction, PostingCategory,
                       Naglowek = CASE
                           WHEN CHARINDEX('/TXT/', Description) > 0
                           THEN LEFT(Description, CHARINDEX('/TXT/', Description) - 1)
                           ELSE Description END
                FROM pay.Payment
                WHERE ErpEntryId IS NOT NULL AND PostedAt IS NOT NULL
                  AND PostingCategory IN ('SplitPayment', 'Standard')
            ),
            Kandydaci AS (
                SELECT v.ErpEntryId AS VatEntry, b.ErpEntryId AS BruttoEntry,
                       COUNT(*) OVER (PARTITION BY v.PaymentId) AS IleDlaVat,
                       COUNT(*) OVER (PARTITION BY b.PaymentId) AS IleDlaBrutto
                FROM Nogi AS v
                INNER JOIN Nogi AS b
                    ON  b.RegisterSeries = v.RegisterSeries
                    AND b.BookingDate    = v.BookingDate
                    AND b.Naglowek       = v.Naglowek
                    AND b.Direction     <> v.Direction
                WHERE v.PostingCategory = 'SplitPayment' AND b.PostingCategory = 'Standard'
            )
            SELECT VatEntry, BruttoEntry INTO #Pary
            FROM Kandydaci WHERE IleDlaVat = 1 AND IleDlaBrutto = 1;

            UPDATE z SET z.KAZ_SplitPNumer = p.BruttoEntry
            FROM CDN.Zapisy AS z INNER JOIN #Pary AS p ON z.KAZ_GIDNumer = p.VatEntry;

            UPDATE z SET z.KAZ_SplitPNumer = p.VatEntry
            FROM CDN.Zapisy AS z INNER JOIN #Pary AS p ON z.KAZ_GIDNumer = p.BruttoEntry;

            -- Every VAT leg, including the one on the VAT account, is not subject to settlement.
            UPDATE z SET z.KAZ_Rozliczony = 2
            FROM CDN.Zapisy AS z
            INNER JOIN pay.Payment AS p ON p.ErpEntryId = z.KAZ_GIDNumer
            WHERE p.PostingCategory = 'SplitPayment' AND p.PostedAt IS NOT NULL AND z.KAZ_Rozliczony <> 2;

            SELECT COUNT(*)
            FROM CDN.Zapisy AS z
            INNER JOIN pay.Payment AS p ON p.ErpEntryId = z.KAZ_GIDNumer
            WHERE p.PostingCategory = 'SplitPayment' AND p.PostedAt IS NOT NULL AND z.KAZ_SplitPNumer <> 0;
            """;

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 300 };

        return (int)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    /// <summary>Operations with no cash entry yet, oldest first.</summary>
    public async Task<IReadOnlyList<PendingOperation>> GetOperationsToPostAsync(
        DateTime from,
        IReadOnlyCollection<string> registers,
        int limit,
        string feeOperation = "PRW",
        string codOperation = "",
        bool toBuffer = false,
        CancellationToken cancellationToken = default)
    {
        if (registers.Count == 0) return [];

        var parameters = registers.Select((_, i) => $"@r{i}").ToArray();

        // The cash operation symbol comes from the statement import configuration on the
        // register - the same one ERP's own statement import uses.
        var sql = $"""
            SELECT TOP (@limit)
                   p.PaymentId, p.BankExternalId, p.RegisterSeries, p.BookingDate, p.Amount, p.Currency,
                   p.Direction, p.ContractorId, p.Description, p.PayerName, p.Confidence, p.Status,
                   p.ContractorFromBankAccount,
                   RTRIM(COALESCE(cod.KAO_Kod, fee.KAO_Kod, op.KAO_Kod, '')) AS OperationSymbol,
                   p.PostingCategory,
                   ISNULL(doc.Numbers, '')                                   AS DocumentNumbers
            FROM pay.Payment AS p
            INNER JOIN CDN.Rejestry AS r
                ON r.KAR_Seria = p.RegisterSeries
            OUTER APPLY (
                SELECT TOP 1 o.KAO_Kod
                FROM CDN.Operacje AS o
                WHERE o.KAO_GIDNumer = CASE
                        -- Split payments and commissions have operations of their own on the register.
                        WHEN p.PostingCategory = 'SplitPayment' AND p.Direction = 'P'
                            THEN r.KAR_ImpKAOSplitPPrzychod
                        WHEN p.PostingCategory = 'SplitPayment'
                            THEN r.KAR_ImpKAOSplitPRozchod
                        -- Registers without settlement (cards, social fund, social insurance,
                        -- grants): always the "other" operation, because the entry is booked on
                        -- the anonymous party.
                        WHEN p.PostingCategory IN ('Card', 'PostOnly') AND p.Direction = 'P'
                            THEN r.KAR_ImpKAOInnePrzychod
                        WHEN p.PostingCategory IN ('Card', 'PostOnly')
                            THEN r.KAR_ImpKAOInneRozchod
                        -- The party reaches the entry only when the payer account identified
                        -- it; otherwise the entry is booked on the anonymous party and the
                        -- matching operation has to be chosen.
                        WHEN p.Direction = 'P' AND p.ContractorId <> 0 AND p.ContractorFromBankAccount = 1
                            THEN r.KAR_ImpKAOKontrahentPrzychod
                        WHEN p.Direction = 'P' THEN r.KAR_ImpKAOInnePrzychod
                        WHEN p.ContractorId <> 0 AND p.ContractorFromBankAccount = 1
                            THEN r.KAR_ImpKAOKontrahentRozchod
                        ELSE r.KAR_ImpKAOInneRozchod
                    END
            ) AS op
            OUTER APPLY (
                -- A bank commission has one symbol regardless of the register.
                SELECT TOP 1 f.KAO_Kod
                FROM CDN.Operacje AS f
                WHERE p.PostingCategory = 'BankFee' AND f.KAO_Kod = @feeOperation
            ) AS fee
            OUTER APPLY (
                -- Cash on delivery. Its register has no statement import configured - nothing is
                -- ever imported into it - so the operation is named in configuration instead, and
                -- checked here against the ones the register actually offers.
                SELECT TOP 1 c.KAO_Kod
                FROM CDN.RejOp AS ro
                INNER JOIN CDN.Operacje AS c
                    ON c.KAO_GIDNumer = ro.KRO_KAONumer AND c.KAO_GIDTyp = ro.KRO_KAOTyp
                WHERE p.PostingCategory = 'Cod'
                  AND ro.KRO_KARNumer = r.KAR_GIDNumer
                  AND c.KAO_Kod = @codOperation
            ) AS cod
            OUTER APPLY (
                -- The documents the operation is to close, for the entry's own number field.
                SELECT STRING_AGG(a.DocNumber, ', ') WITHIN GROUP (ORDER BY a.DocNumber) AS Numbers
                FROM pay.Allocation AS a
                WHERE a.PaymentId = p.PaymentId
            ) AS doc
            WHERE p.ErpEntryId IS NULL
              AND p.BookingDate >= @from
              AND p.RegisterSeries IN ({string.Join(", ", parameters)})
              AND ({(toBuffer ? "1 = 1" : "NOT " + PastDay)})
            ORDER BY p.BookingDate, p.PaymentId
            """;

        var operations = new List<PendingOperation>();

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 120 };
        command.Parameters.AddWithValue("@limit", limit);
        command.Parameters.AddWithValue("@from", from.Date);
        command.Parameters.AddWithValue("@feeOperation", feeOperation);
        command.Parameters.AddWithValue("@codOperation", codOperation);

        var index = 0;
        foreach (var register in registers) command.Parameters.AddWithValue(parameters[index++], register);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            operations.Add(new PendingOperation(
                reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetDateTime(3),
                reader.GetDecimal(4), reader.GetString(5), reader.GetString(6) == "P",
                reader.GetInt32(7), reader.GetString(8), reader.GetString(9),
                reader.GetString(10), reader.GetString(11), reader.GetString(13), reader.GetString(14),
                reader.GetBoolean(12), reader.GetString(15)));
        }

        return operations;
    }

    /// <summary>
    /// Certain operations whose cash entry is already in ERP but has not been settled.
    /// </summary>
    /// <remarks>
    /// An ordinary pass settles a payment right after creating its entry, so what lands here is
    /// whatever matched only later - the engine can change its mind when new rules arrive, or when
    /// a document is issued after the transfer was posted.
    /// </remarks>
    public async Task<IReadOnlyList<PendingSettlement>> GetSettlementBacklogAsync(
        DateTime from,
        IReadOnlyCollection<string> registers,
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (registers.Count == 0) return [];

        var parameters = registers.Select((_, i) => $"@r{i}").ToArray();

        var sql = $"""
            SELECT TOP (@limit) p.PaymentId, p.ErpEntryId, p.Amount, p.RegisterSeries, p.ContractorId
            FROM pay.Payment AS p
            INNER JOIN CDN.Zapisy AS z ON z.KAZ_GIDNumer = p.ErpEntryId
            WHERE p.ErpEntryId IS NOT NULL
              AND p.SettledAt IS NULL
              AND p.Status = 'Proposed'
              AND p.Confidence = 'High'
              AND p.PostingCategory IN ('Standard', 'Cod')
              AND p.BookingDate >= @from
              AND p.RegisterSeries IN ({string.Join(", ", parameters)})
              AND z.KAZ_Rozliczony = 0
              AND EXISTS (SELECT 1 FROM pay.Allocation AS a WHERE a.PaymentId = p.PaymentId)
            ORDER BY p.BookingDate, p.PaymentId
            """;

        var rows = new List<PendingSettlement>();

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 120 };
        command.Parameters.AddWithValue("@limit", limit);
        command.Parameters.AddWithValue("@from", from.Date);

        var index = 0;
        foreach (var register in registers) command.Parameters.AddWithValue(parameters[index++], register);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new PendingSettlement(
                reader.GetInt64(0), reader.GetInt32(1), reader.GetDecimal(2), reader.GetString(3),
                reader.GetInt32(4)));
        }

        return rows;
    }

    /// <summary>Whether a cash report already exists for a register and a day.</summary>
    public async Task<bool> ReportExistsAsync(
        string registerSeries, DateTime day, CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT COUNT(*) FROM CDN.Raporty
            WHERE KRP_Seria = @series AND KRP_DataOtwarcia = @day
            """;

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@series", registerSeries);
        command.Parameters.AddWithValue("@day", XlDate.FromDateTime(day));

        return (int)(await command.ExecuteScalarAsync(cancellationToken))! > 0;
    }

    /// <summary>The settlement lines for a payment that is to be settled automatically.</summary>
    public async Task<IReadOnlyList<PendingAllocation>> GetAllocationsAsync(
        long paymentId, CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT AllocationId, DocType, DocId, DocLp, DocNumber, Amount
            FROM pay.Allocation
            WHERE PaymentId = @paymentId AND SettlementId IS NULL
            ORDER BY AllocationId
            """;

        var allocations = new List<PendingAllocation>();

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@paymentId", paymentId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            allocations.Add(new PendingAllocation(
                reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3),
                reader.GetString(4), reader.GetDecimal(5)));
        }

        return allocations;
    }
}
