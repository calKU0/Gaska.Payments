using Gaska.Payments.Domain.Matching;
using Gaska.Payments.Domain.Model;
using Microsoft.Data.SqlClient;

namespace Gaska.Payments.Erp;

/// <summary>
/// Reading data from the Comarch ERP XL database. The class is deliberately read-only -
/// settlements are written through our own mechanisms, never straight into the CDN tables.
/// </summary>
public sealed class ErpReadRepository(string connectionString)
{
    /// <summary>Document types that give rise to receivables from customers.</summary>
    private const string ReceivableDocTypes = "2033,2034,2035,2037,1824,2041,2042,2043,2045";

    /// <summary>
    /// Open payments of sales documents together with the numbers of related documents (goods
    /// issues and orders), because customers quote those in a payment title just as readily.
    /// </summary>
    public async Task<IReadOnlyList<OpenReceivable>> GetOpenReceivablesAsync(
        DateTime issuedSince, CancellationToken cancellationToken = default)
    {
        var sql = $"""
            WITH Platnosci AS (
                SELECT
                    p.TrP_GIDTyp, p.TrP_GIDNumer, p.TrP_GIDLp, p.TrP_Typ,
                    p.TrP_Kwota, p.TrP_Pozostaje, p.TrP_Waluta, p.TrP_Termin,
                    p.TrP_KntTyp, p.TrP_KntNumer
                FROM CDN.TraPlat p
                WHERE p.TrP_GIDTyp IN ({ReceivableDocTypes})
                  AND p.TrP_Rozliczona <> 1 AND p.TrP_Pozostaje > 0
            )
            SELECT
                p.TrP_GIDTyp                                                            AS DocType,
                p.TrP_GIDNumer                                                          AS DocId,
                p.TrP_GIDLp                                                             AS Lp,
                o.OB_Skrot                                                              AS Symbol,
                CDN.NumerDokumentu(n.TrN_GIDTyp, n.TrN_SpiTyp, n.TrN_TrNTyp,
                                   n.TrN_TrNNumer, n.TrN_TrNRok, n.TrN_TrNSeria, 0)     AS DocNumber,
                n.TrN_TrNNumer                                                          AS Number,
                n.TrN_TrNRok                                                            AS Year,
                RTRIM(n.TrN_TrNSeria)                                                   AS Series,
                p.TrP_Typ                                                               AS PaymentType,
                p.TrP_Kwota                                                             AS Amount,
                p.TrP_Pozostaje                                                         AS Remaining,
                p.TrP_Waluta                                                            AS Currency,
                p.TrP_Termin                                                            AS DueDate,
                n.TrN_Data2                                                             AS IssueDate,
                p.TrP_KntNumer                                                          AS ContractorId,
                ISNULL(k.Knt_Akronim, '')                                               AS Acronym,
                ISNULL(k.Knt_Nazwa1, '')                                                AS ContractorName,
                ISNULL(k.Knt_Nip, '')                                                   AS Nip,
                zam.ZaN_ZamNumer                                                        AS OrderNumber,
                zam.ZaN_ZamRok                                                          AS OrderYear,
                RTRIM(ISNULL(zam.ZaN_ZamSeria, ''))                                     AS OrderSeries,
                ISNULL(ksef.KSF_Numer, '')                                              AS KsefNumber
            FROM Platnosci p
            INNER JOIN CDN.TraNag n
                ON n.TrN_GIDTyp = p.TrP_GIDTyp AND n.TrN_GIDNumer = p.TrP_GIDNumer
            INNER JOIN CDN.Obiekty o
                ON o.OB_GIDTyp = p.TrP_GIDTyp
            LEFT JOIN CDN.KntKarty k
                ON k.Knt_GIDTyp = p.TrP_KntTyp AND k.Knt_GIDNumer = p.TrP_KntNumer
            LEFT JOIN CDN.ZamNag zam
                ON n.TrN_ZaNTyp = 960 AND zam.ZaN_GIDNumer = n.TrN_ZaNNumer
            OUTER APPLY (
                SELECT TOP 1 k.KSF_Numer
                FROM CDN.KSeFDokumenty k
                WHERE k.KSF_DokTyp = p.TrP_GIDTyp AND k.KSF_DokNumer = p.TrP_GIDNumer AND k.KSF_Numer <> ''
                ORDER BY k.KSF_ID DESC
            ) ksef
            WHERE n.TrN_Data2 >= @issuedSince
            """;

        var receivables = new List<OpenReceivable>();
        var byDocId = new Dictionary<int, List<DocumentReference>>();

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using (var command = new SqlCommand(sql, connection) { CommandTimeout = 300 })
        {
            command.Parameters.AddWithValue("@issuedSince", XlDate.FromDateTime(issuedSince));

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var docId = reader.GetInt32(reader.GetOrdinal("DocId"));
                var paymentType = reader.GetInt16(reader.GetOrdinal("PaymentType"));
                var symbol = reader.GetString(reader.GetOrdinal("Symbol")).Trim();
                var kind = DocumentKindExtensions.FromErpSymbol(symbol);

                // TrP_Typ: 2 is a receivable, 1 a liability. On a sales document a liability
                // means a reducing correction, which is subtracted when a payment comes in.
                var isCorrection = paymentType == 1 || kind.IsCorrection();

                // A document may have several instalments (GIDLp) - they all share the same
                // list of related numbers, so one instance is kept per document.
                if (!byDocId.TryGetValue(docId, out var related))
                {
                    related = [];
                    byDocId[docId] = related;

                    // The order from the invoice header. Collective invoices have none - for
                    // those the order is added by AppendDeliveryNoteNumbersAsync, going through
                    // the goods issues they collect.
                    var orderOrdinal = reader.GetOrdinal("OrderNumber");
                    if (!reader.IsDBNull(orderOrdinal))
                    {
                        related.Add(new DocumentReference(
                            DocumentKind.Zs,
                            reader.GetInt32(orderOrdinal),
                            reader.GetInt16(reader.GetOrdinal("OrderYear")) % 100,
                            reader.GetString(reader.GetOrdinal("OrderSeries")),
                            ReferenceStrength.FullNumber,
                            "zamówienie źródłowe"));
                    }
                }

                receivables.Add(new OpenReceivable
                {
                    PaymentDocType = reader.GetInt16(reader.GetOrdinal("DocType")),
                    PaymentDocId = docId,
                    PaymentLp = reader.GetInt16(reader.GetOrdinal("Lp")),
                    Kind = kind,
                    DocumentNumber = reader.GetString(reader.GetOrdinal("DocNumber")).Trim(),
                    Number = reader.GetInt32(reader.GetOrdinal("Number")),
                    Year = reader.GetInt16(reader.GetOrdinal("Year")),
                    Series = reader.GetString(reader.GetOrdinal("Series")),
                    ContractorId = reader.GetInt32(reader.GetOrdinal("ContractorId")),
                    ContractorAcronym = reader.GetString(reader.GetOrdinal("Acronym")),
                    ContractorName = reader.GetString(reader.GetOrdinal("ContractorName")),
                    ContractorNip = reader.GetString(reader.GetOrdinal("Nip")),
                    Amount = reader.GetDecimal(reader.GetOrdinal("Amount")),
                    Remaining = reader.GetDecimal(reader.GetOrdinal("Remaining")),
                    Currency = reader.GetString(reader.GetOrdinal("Currency")).Trim(),
                    DueDate = XlDate.ToDateTime(reader.GetInt32(reader.GetOrdinal("DueDate"))),
                    IssueDate = XlDate.ToDateTime(reader.GetInt32(reader.GetOrdinal("IssueDate"))),
                    IsCorrection = isCorrection,
                    RelatedNumbers = related,
                    KsefNumber = reader.GetString(reader.GetOrdinal("KsefNumber")),
                });
            }
        }

        await AppendDeliveryNoteNumbersAsync(connection, byDocId, issuedSince, cancellationToken);
        await AppendOrderNumbersAsync(connection, byDocId, issuedSince, cancellationToken);
        return receivables;
    }

    /// <summary>
    /// Adds the numbers of the orders behind the goods issues an invoice collects.
    /// </summary>
    /// <remarks>
    /// The link to the order is read from the goods issue's line items
    /// (<c>CDN.TraSElem.TrS_Rez*</c>) rather than from its header. The header holds a single
    /// order, whereas one goods issue can fulfil several - in 2026 there are over 1,400 such
    /// issues, and the record holder covers 56 orders.
    /// </remarks>
    private static async Task AppendOrderNumbersAsync(
        SqlConnection connection,
        Dictionary<int, List<DocumentReference>> byDocId,
        DateTime issuedSince,
        CancellationToken cancellationToken)
    {
        if (byDocId.Count == 0) return;

        var sql = $"""
            SELECT DISTINCT
                w.TrN_SpiNumer                        AS InvoiceId,
                zam.ZaN_ZamNumer                      AS OrderNumber,
                zam.ZaN_ZamRok                        AS OrderYear,
                RTRIM(ISNULL(zam.ZaN_ZamSeria, ''))   AS OrderSeries
            FROM CDN.TraNag AS w
            INNER JOIN CDN.TraSElem AS s
                ON s.TrS_GIDTyp = w.TrN_GIDTyp AND s.TrS_GIDNumer = w.TrN_GIDNumer AND s.TrS_RezTyp = 960
            INNER JOIN CDN.ZamNag AS zam
                ON zam.ZaN_GIDNumer = s.TrS_RezNumer
            WHERE w.TrN_GIDTyp IN (2001, 2005)
              AND w.TrN_SpiTyp IN ({ReceivableDocTypes})
              AND w.TrN_Data2 >= @issuedSince
            """;

        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 300 };
        command.Parameters.AddWithValue("@issuedSince", XlDate.FromDateTime(issuedSince));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var invoiceId = reader.GetInt32(0);
            if (!byDocId.TryGetValue(invoiceId, out var related)) continue;

            AddRelated(related, new DocumentReference(
                DocumentKind.Zs,
                reader.GetInt32(1),
                reader.GetInt16(2) % 100,
                reader.GetString(3),
                ReferenceStrength.FullNumber,
                "zamówienie zrealizowane wydaniem"));
        }
    }

    /// <summary>Adds the numbers of the goods issues an invoice collects - customers often pay "for the delivery note".</summary>
    private static async Task AppendDeliveryNoteNumbersAsync(
        SqlConnection connection,
        Dictionary<int, List<DocumentReference>> byDocId,
        DateTime issuedSince,
        CancellationToken cancellationToken)
    {
        if (byDocId.Count == 0) return;

        var sql = $"""
            SELECT w.TrN_SpiNumer          AS InvoiceId,
                   w.TrN_TrNNumer          AS WzNumber,
                   w.TrN_TrNRok            AS WzYear,
                   RTRIM(w.TrN_TrNSeria)   AS WzSeries
            FROM CDN.TraNag AS w
            WHERE w.TrN_GIDTyp IN (2001, 2005)          -- goods issues, domestic and export
              AND w.TrN_SpiTyp IN ({ReceivableDocTypes})
              AND w.TrN_Data2 >= @issuedSince
            """;

        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 300 };
        command.Parameters.AddWithValue("@issuedSince", XlDate.FromDateTime(issuedSince));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var invoiceId = reader.GetInt32(0);
            if (!byDocId.TryGetValue(invoiceId, out var related)) continue;

            AddRelated(related, new DocumentReference(
                DocumentKind.Wz,
                reader.GetInt32(1),
                reader.GetInt16(2) % 100,
                reader.GetString(3),
                ReferenceStrength.FullNumber,
                "WZ spięta fakturą"));
        }
    }

    /// <summary>Several goods issues can come from one order - the number is kept once.</summary>
    private static void AddRelated(List<DocumentReference> related, DocumentReference reference)
    {
        if (related.Any(r => r.Kind == reference.Kind && r.Number == reference.Number && r.Year == reference.Year))
        {
            return;
        }

        related.Add(reference);
    }

    /// <summary>
    /// The bank registers named in configuration.
    /// </summary>
    public async Task<IReadOnlyList<BankRegister>> GetBankRegistersAsync(
        IReadOnlyCollection<string> series, CancellationToken cancellationToken = default)
    {
        if (series.Count == 0) return [];

        var parameters = series.Select((_, i) => $"@s{i}").ToArray();

        var sql = $"""
            SELECT RTRIM(r.KAR_Seria)      AS Series,
                   RTRIM(r.KAR_Nazwa)      AS Name,
                   RTRIM(r.KAR_Waluta)     AS Currency,
                   RTRIM(r.KAR_NrRachunku) AS AccountNumber,
                   RTRIM(ISNULL(r.KAR_Kraj, '')) AS Country
            FROM CDN.Rejestry AS r
            WHERE r.KAR_Seria IN ({string.Join(", ", parameters)})
            """;

        var registers = new List<BankRegister>();

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 60 };

        var index = 0;
        foreach (var value in series) command.Parameters.AddWithValue(parameters[index++], value);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            registers.Add(new BankRegister(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4)));
        }

        return registers;
    }

    /// <summary>A contractor's number from their acronym - needed for our own company on VAT legs.</summary>
    public async Task<int> GetContractorIdByAcronymAsync(
        string acronym, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(acronym)) return 0;

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(
            "SELECT TOP 1 Knt_GIDNumer FROM CDN.KntKarty WHERE Knt_Akronim = @acronym", connection);
        command.Parameters.AddWithValue("@acronym", acronym);

        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is int id ? id : 0;
    }

    /// <summary>A cash or bank entry from ERP - used to correlate a downloaded operation with what ERP sees.</summary>
    /// <param name="Remaining">The amount of the entry still open in ERP (KAZ_Pozostaje).</param>
    public sealed record ErpBankEntry(
        long Id, DateTime Date, decimal Amount, string Currency, string Description, int Rozliczony, decimal Remaining);

    /// <summary>
    /// Incoming bank entries from ERP over a given period. They serve to tie an operation
    /// downloaded from GOconnect to an entry in ERP, so that the engine's proposal can be
    /// compared with the settlement the accounting team actually made.
    /// </summary>
    public async Task<IReadOnlyList<ErpBankEntry>> GetErpBankEntriesAsync(
        DateTime bookedSince, CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT z.KAZ_GIDNumer, z.KAZ_DataZapisu, z.KAZ_Kwota, RTRIM(z.KAZ_Waluta),
                   ISNULL(z.KAZ_TrescCDC, ''), z.KAZ_Rozliczony, z.KAZ_Pozostaje
            FROM CDN.Zapisy z
            WHERE z.KAZ_RP = 2 AND z.KAZ_Anulowany = 0 AND z.KAZ_DataZapisu >= @bookedSince
            """;

        var entries = new List<ErpBankEntry>();

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 300 };
        command.Parameters.AddWithValue("@bookedSince", XlDate.FromDateTime(bookedSince));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            entries.Add(new ErpBankEntry(
                reader.GetInt32(0),
                XlDate.ToDateTime(reader.GetInt32(1)),
                reader.GetDecimal(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetByte(5),
                reader.GetDecimal(6)));
        }

        return entries;
    }

    /// <summary>
    /// Open liabilities together with the document number at the contractor's end.
    /// </summary>
    /// <remarks>
    /// The counterpart of the receivables index, for outgoing transfers. The key is
    /// <c>TrN_DokumentObcy</c> - the supplier's invoice number, which is what our transfer quotes.
    /// 93% of open liabilities carry one, which makes it the strongest evidence after the order
    /// reference.
    /// </remarks>
    public async Task<IReadOnlyList<OpenReceivable>> GetOpenLiabilitiesAsync(
        CancellationToken cancellationToken = default)
    {
        var sql = $"""
            SELECT p.TrP_GIDTyp, p.TrP_GIDNumer, p.TrP_GIDLp,
                   COALESCE(
                       CDN.NumerDokumentu(n.TrN_GIDTyp, n.TrN_SpiTyp, n.TrN_TrNTyp,
                                          n.TrN_TrNNumer, n.TrN_TrNRok, n.TrN_TrNSeria, 0),
                       CDN.NumerDokumentu(imp.ImN_GIDTyp, 0, imp.ImN_ImNTyp,
                                          imp.ImN_ImNNumer, imp.ImN_ImNRok, imp.ImN_ImNSeria, 0),
                       CDN.NumerDokumentu(upo.UPN_GIDTyp, 0, upo.UPN_Typ,
                                          upo.UPN_Numer, upo.UPN_Rok, upo.UPN_Seria, 0),
                       NULLIF(RTRIM(mem.MEN_NumerDokumentu), ''),
                       RTRIM(ISNULL(o.OB_Skrot, '?')) + ' ' + CAST(p.TrP_GIDNumer AS VARCHAR(12))) AS DocNumber,
                   -- Import invoices keep the contractor's number in a header of their own;
                   -- they are absent from CDN.TraNag, so the column there is empty.
                   COALESCE(NULLIF(RTRIM(n.TrN_DokumentObcy), ''),
                            NULLIF(RTRIM(imp.ImN_DokumentObcy), ''), '') AS DokumentObcy,
                   ISNULL(n.TrN_TrNNumer, 0) AS Numer,
                   ISNULL(n.TrN_TrNRok, 0)   AS Rok,
                   RTRIM(ISNULL(n.TrN_TrNSeria, '')) AS Seria,
                   p.TrP_Kwota, p.TrP_Pozostaje, p.TrP_Waluta, p.TrP_Termin,
                   p.TrP_KntNumer,
                   ISNULL(RTRIM(k.Knt_Akronim), '') AS Akronim,
                   ISNULL(RTRIM(k.Knt_Nazwa1), '')  AS Nazwa,
                   ISNULL(RTRIM(k.Knt_Nip), '')     AS Nip
            FROM CDN.TraPlat AS p
            LEFT JOIN CDN.TraNag AS n
                ON n.TrN_GIDTyp = p.TrP_GIDTyp AND n.TrN_GIDNumer = p.TrP_GIDNumer
            LEFT JOIN CDN.KntKarty AS k ON k.Knt_GIDNumer = p.TrP_KntNumer
            LEFT JOIN CDN.Obiekty AS o ON o.OB_GIDTyp = p.TrP_GIDTyp
            -- Import invoices keep their header in CDN.ImpNag, not in CDN.TraNag.
            LEFT JOIN CDN.ImpNag AS imp
                ON imp.ImN_GIDTyp = p.TrP_GIDTyp AND imp.ImN_GIDNumer = p.TrP_GIDNumer
            -- Accrual notes keep a readable number in a header of their own.
            LEFT JOIN CDN.MemNag AS mem
                ON mem.MEN_GIDTyp = p.TrP_GIDTyp AND mem.MEN_GIDNumer = p.TrP_GIDNumer
            LEFT JOIN CDN.UpoNag AS upo
                ON upo.UPN_GIDTyp = p.TrP_GIDTyp AND upo.UPN_GIDNumer = p.TrP_GIDNumer
            WHERE p.TrP_Typ = 1
              AND p.TrP_Rozliczona <> 1
              AND p.TrP_Pozostaje > 0
              AND p.TrP_KntNumer <> 0
              AND p.TrP_GIDTyp NOT IN ({SettlementDocumentTypes.NotSettleableSql})
            """;

        var rows = new List<OpenReceivable>();

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 180 };

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new OpenReceivable
            {
                PaymentDocType = Convert.ToInt32(reader.GetValue(0)),
                PaymentDocId = reader.GetInt32(1),
                PaymentLp = Convert.ToInt32(reader.GetValue(2)),
                DocumentNumber = reader.IsDBNull(3) ? string.Empty : reader.GetString(3).Trim(),
                ForeignNumber = reader.GetString(4),
                Number = Convert.ToInt32(reader.GetValue(5)),
                Year = Convert.ToInt32(reader.GetValue(6)),
                Series = reader.GetString(7),
                Kind = DocumentKind.Unknown,
                Amount = reader.GetDecimal(8),
                Remaining = reader.GetDecimal(9),
                Currency = reader.GetString(10).Trim(),
                DueDate = XlDate.ToDateTime(reader.GetInt32(11)),
                ContractorId = reader.GetInt32(12),
                ContractorAcronym = reader.GetString(13),
                ContractorName = reader.GetString(14),
                ContractorNip = reader.GetString(15),
            });
        }

        return rows;
    }

    /// <summary>
    /// Open document payments XL sent to the bank, together with their order reference.
    /// </summary>
    /// <remarks>
    /// Neither document type nor direction is narrowed down: this is mostly about liabilities
    /// (purchase invoices), which the receivables index does not hold at all. The set is small -
    /// as small as the number of unpaid orders sent to the bank.
    /// </remarks>
    public async Task<IReadOnlyList<OpenReceivable>> GetPaymentsByBankReferenceAsync(
        CancellationToken cancellationToken = default)
    {
        var sql = $"""
            SELECT p.TrP_GIDTyp, p.TrP_GIDNumer, p.TrP_GIDLp,
                   ISNULL(CDN.NumerDokumentu(n.TrN_GIDTyp, n.TrN_SpiTyp, n.TrN_TrNTyp,
                                             n.TrN_TrNNumer, n.TrN_TrNRok, n.TrN_TrNSeria, 0),
                          RTRIM(ISNULL(o.OB_Skrot, '?')) + ' ' + CAST(p.TrP_GIDNumer AS VARCHAR(12))) AS DocNumber,
                   ISNULL(n.TrN_TrNNumer, 0)  AS Number,
                   ISNULL(n.TrN_TrNRok, 0)    AS Rok,
                   RTRIM(ISNULL(n.TrN_TrNSeria, '')) AS Seria,
                   p.TrP_Kwota, p.TrP_Pozostaje, p.TrP_Waluta, p.TrP_Termin,
                   p.TrP_KntNumer,
                   ISNULL(RTRIM(k.Knt_Akronim), '') AS Akronim,
                   ISNULL(RTRIM(k.Knt_Nazwa1), '')  AS Nazwa,
                   ISNULL(RTRIM(k.Knt_Nip), '')     AS Nip,
                   LTRIM(RTRIM(p.TrP_EndToEndId))   AS Referencja
            FROM CDN.TraPlat AS p
            LEFT JOIN CDN.TraNag AS n
                ON n.TrN_GIDTyp = p.TrP_GIDTyp AND n.TrN_GIDNumer = p.TrP_GIDNumer
            LEFT JOIN CDN.KntKarty AS k ON k.Knt_GIDNumer = p.TrP_KntNumer
            LEFT JOIN CDN.Obiekty AS o ON o.OB_GIDTyp = p.TrP_GIDTyp
            WHERE LEN(LTRIM(RTRIM(ISNULL(p.TrP_EndToEndId, '')))) > 0
              AND p.TrP_Rozliczona <> 1
              AND p.TrP_Pozostaje > 0
              AND p.TrP_GIDTyp NOT IN ({SettlementDocumentTypes.NotSettleableSql})
            """;

        var rows = new List<OpenReceivable>();

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 180 };

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new OpenReceivable
            {
                PaymentDocType = Convert.ToInt32(reader.GetValue(0)),
                PaymentDocId = reader.GetInt32(1),
                PaymentLp = Convert.ToInt32(reader.GetValue(2)),
                DocumentNumber = reader.IsDBNull(3) ? string.Empty : reader.GetString(3).Trim(),
                Number = Convert.ToInt32(reader.GetValue(4)),
                Year = Convert.ToInt32(reader.GetValue(5)),
                Series = reader.GetString(6),
                Kind = DocumentKind.Unknown,
                Amount = reader.GetDecimal(7),
                Remaining = reader.GetDecimal(8),
                Currency = reader.GetString(9).Trim(),
                DueDate = XlDate.ToDateTime(reader.GetInt32(10)),
                ContractorId = reader.GetInt32(11),
                ContractorAcronym = reader.GetString(12),
                ContractorName = reader.GetString(13),
                ContractorNip = reader.GetString(14),
                BankReference = reader.GetString(15),
            });
        }

        return rows;
    }

    /// <summary>Fills the index with account-to-contractor and tax-id-to-contractor links.</summary>
    public async Task LoadContractorLookupsAsync(DocumentIndex index, CancellationToken cancellationToken = default)
    {
        const string accountsSql = """
            SELECT RkB_ObiNumer, RkB_NrRachunku FROM CDN.RachunkiBankowe
            WHERE RkB_ObiTyp = 32 AND RkB_NrRachunku <> ''
            UNION
            SELECT NRB_ObNumer, NRB_NrRachunkuZnorm FROM CDN.NumeryRachunkow
            WHERE NRB_ObTyp = 32 AND NRB_NrRachunkuZnorm <> ''
            """;

        const string nipSql = """
            SELECT Knt_GIDNumer, Knt_Nip FROM CDN.KntKarty WHERE Knt_Nip <> ''
            """;

        const string namesSql = """
            SELECT Knt_GIDNumer,
                   ISNULL(Knt_Akronim, ''),
                   ISNULL(Knt_Nazwa1, ''),
                   ISNULL(Knt_Nazwa2, '')
            FROM CDN.KntKarty
            WHERE Knt_Nazwa1 <> '' OR Knt_Akronim <> ''
            """;

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using (var command = new SqlCommand(accountsSql, connection) { CommandTimeout = 300 })
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                index.RegisterContractorAccount(reader.GetString(1), reader.GetInt32(0));
            }
        }

        await using (var command = new SqlCommand(nipSql, connection) { CommandTimeout = 300 })
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                index.RegisterContractorNip(reader.GetString(1), reader.GetInt32(0));
            }
        }

        await using (var command = new SqlCommand(namesSql, connection) { CommandTimeout = 300 })
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var contractorId = reader.GetInt32(0);
                var acronym = reader.GetString(1);
                var name1 = reader.GetString(2);
                var name2 = reader.GetString(3);

                // A name is sometimes split across two fields ("Gospodarstwo Rolne" plus
                // "JADWIGA FICHNA"), so both the parts and their concatenation are recorded.
                index.RegisterContractorAcronym(contractorId, acronym);
                index.RegisterContractorName(name1, contractorId);
                index.RegisterContractorName(acronym, contractorId);
                if (name2.Length > 0)
                {
                    index.RegisterContractorName(name2, contractorId);
                    index.RegisterContractorName($"{name1} {name2}", contractorId);
                }
            }
        }
    }
}
