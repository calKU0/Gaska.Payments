using System.Data;
using Gaska.Payments.Domain.Model;
using Gaska.Payments.Erp;
using Microsoft.Data.SqlClient;

namespace Gaska.Payments.Desktop.Data;

/// <summary>
/// Data for the accounting application. It only ever reads from the <c>CDN.*</c> tables -
/// settlements are created by the XL API, and our own trail goes to <c>pay.Allocation</c>.
/// </summary>
public sealed class QueueRepository(string connectionString, RegisterSettings registers)
{
    /// <summary>
    /// The shared part of the open-item queries. <c>CDN.TraNag</c> is joined on the left - not
    /// every open item has a trade header, and one without still has to be shown.
    /// </summary>
    private static readonly string DocumentSelect = $"""
            SELECT pl.TrP_GIDTyp, pl.TrP_GIDNumer, pl.TrP_GIDLp,
                   COALESCE(
                       CDN.NumerDokumentu(n.TrN_GIDTyp, n.TrN_SpiTyp, n.TrN_TrNTyp,
                                          n.TrN_TrNNumer, n.TrN_TrNRok, n.TrN_TrNSeria, 0),
                       CDN.NumerDokumentu(imp.ImN_GIDTyp, 0, imp.ImN_ImNTyp,
                                          imp.ImN_ImNNumer, imp.ImN_ImNRok, imp.ImN_ImNSeria, 0),
                       CDN.NumerDokumentu(upo.UPN_GIDTyp, 0, upo.UPN_Typ,
                                          upo.UPN_Numer, upo.UPN_Rok, upo.UPN_Seria, 0),
                       CDN.NumerDokumentu(sad.SaN_GIDTyp, 0, sad.SaN_SaNTyp,
                                          sad.SaN_SaNNumer, sad.SaN_SaNRok, sad.SaN_SaNSeria, 0),
                       NULLIF(RTRIM(mem.MEN_NumerDokumentu), ''),
                       -- A note with no document number filled in: we assemble one from
                       -- series, number and year so it can be found in ERP. Old records only.
                       CASE WHEN mem.MEN_GIDNumer IS NOT NULL THEN
                            RTRIM(ISNULL(o.OB_Skrot, 'UNM')) + ' '
                            + ISNULL(NULLIF(RTRIM(mem.MEN_Seria), '') + '/', '')
                            + CAST(mem.MEN_Numer AS VARCHAR(12)) + '/'
                            + CAST(mem.MEN_RokMiesiac / 100 AS VARCHAR(4))
                       END,
                       RTRIM(ISNULL(o.OB_Skrot, '?')) + ' ' + CAST(pl.TrP_GIDNumer AS VARCHAR(12))) AS DocNumber,
                   pl.TrP_Typ, pl.TrP_Kwota, pl.TrP_Pozostaje, pl.TrP_Waluta, pl.TrP_Termin,
                   pl.TrP_KntNumer, ISNULL(RTRIM(k.Knt_Akronim), '') AS Acronym,
                   RTRIM(ISNULL(o.OB_Skrot, '')) AS Symbol,
                   COALESCE(NULLIF(RTRIM(n.TrN_DokumentObcy), ''),
                            NULLIF(RTRIM(imp.ImN_DokumentObcy), ''), '') AS DokumentObcy
            FROM CDN.TraPlat AS pl
            LEFT JOIN CDN.TraNag AS n
                ON n.TrN_GIDTyp = pl.TrP_GIDTyp AND n.TrN_GIDNumer = pl.TrP_GIDNumer
            LEFT JOIN CDN.KntKarty AS k ON k.Knt_GIDNumer = pl.TrP_KntNumer
            LEFT JOIN CDN.Obiekty AS o ON o.OB_GIDTyp = pl.TrP_GIDTyp
            -- Import invoices keep their headers in a separate table, not in CDN.TraNag -
            -- without this join a placeholder number came out instead of "FAI-886/26/ZT".
            LEFT JOIN CDN.ImpNag AS imp
                ON imp.ImN_GIDTyp = pl.TrP_GIDTyp AND imp.ImN_GIDNumer = pl.TrP_GIDNumer
            -- Accrual notes keep a readable number in a header of their own.
            LEFT JOIN CDN.MemNag AS mem
                ON mem.MEN_GIDTyp = pl.TrP_GIDTyp AND mem.MEN_GIDNumer = pl.TrP_GIDNumer
            -- Interest notes and dunning letters share a header in CDN.UpoNag.
            LEFT JOIN CDN.UpoNag AS upo
                ON upo.UPN_GIDTyp = pl.TrP_GIDTyp AND upo.UPN_GIDNumer = pl.TrP_GIDNumer
            -- Customs documents have a header of their own - without it "SAD 21024" came out.
            LEFT JOIN CDN.SadNag AS sad
                ON sad.SaN_GIDTyp = pl.TrP_GIDTyp AND sad.SaN_GIDNumer = pl.TrP_GIDNumer
            WHERE pl.TrP_GIDTyp NOT IN ({SettlementDocumentTypes.NotSettleableSql})
              -- Only contractor open items are settled. Tax returns hang on offices (4304)
              -- and payrolls on employees (944) - their party number can coincide with somebody's
              -- Knt_GIDNumer, and they were turning up on the list as another party's debts.
              AND pl.TrP_KntTyp = 32
            """;

    /// <summary>
    /// Who is signed in to ERP on the given XL session, and which registers they may work on.
    /// </summary>
    /// <remarks>
    /// The session id is the one <c>XLLogin</c> returned. ERP writes it to <c>CDN.Sesje</c> along
    /// with the operator's login and the centre they signed in to, and that is the only route out
    /// of the API to either - no XL function reports who signed in through its window.
    ///
    /// The rights themselves are ERP's own: registers hang on the centre, which owns a list of them
    /// in <c>CDN.FrmObiekty</c> or inherits its parent's, and <c>CDN.DostepnyRejestr</c> is the
    /// function ERP uses to walk that. Calling it rather than reimplementing the walk means the
    /// application cannot drift from what the Comarch client shows the same operator.
    ///
    /// A session with no centre (<c>SES_FrsID = 0</c>) yields <see cref="OperatorAccess.Unknown"/>:
    /// the function answers "no register" for a centre it cannot find, and a window with an empty
    /// register filter would be a worse answer than an unnarrowed one.
    /// </remarks>
    public async Task<OperatorAccess> GetOperatorAccessAsync(int sessionId, CancellationToken token = default)
    {
        const string operatorSql = """
            SELECT TOP 1
                   RTRIM(s.SES_OpeIdent)             AS Ident,
                   RTRIM(ISNULL(o.Ope_Nazwisko, '')) AS Name,
                   ISNULL(s.SES_FrsID, 0)            AS CentreId,
                   RTRIM(ISNULL(f.FRS_Nazwa, ''))    AS CentreName
            FROM CDN.Sesje AS s
            LEFT JOIN CDN.OpeKarty AS o ON o.Ope_Ident = s.SES_OpeIdent
            LEFT JOIN CDN.FrmStruktura AS f ON f.FRS_ID = s.SES_FrsID
            WHERE s.SES_SesjaID = @session
            """;

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(token);

        string ident, name, centreName;
        int centreId;

        await using (var command = new SqlCommand(operatorSql, connection) { CommandTimeout = 30 })
        {
            command.Parameters.AddWithValue("@session", sessionId);

            await using var reader = await command.ExecuteReaderAsync(token);
            if (!await reader.ReadAsync(token)) return OperatorAccess.Unknown;

            ident = reader.GetString(0);
            name = reader.GetString(1);
            centreId = reader.GetInt32(2);
            centreName = reader.GetString(3);
        }

        if (centreId == 0) return OperatorAccess.Unknown;

        var series = registers.All;
        var seriesParameters = series.Select((_, i) => $"@r{i}").ToArray();

        var accessSql = $"""
            SELECT r.Series
            FROM (VALUES {string.Join(", ", seriesParameters.Select(p => $"({p})"))}) AS r(Series)
            WHERE CDN.DostepnyRejestr(r.Series, @centre) = 1
            """;

        var allowed = new List<string>();

        await using (var command = new SqlCommand(accessSql, connection) { CommandTimeout = 30 })
        {
            command.Parameters.AddWithValue("@centre", centreId);

            // The series go over as varchar, the type CDN.DostepnyRejestr declares. Sent as
            // nvarchar the whole call would be converted, and a register like ZFŚS would stop
            // matching on a case-sensitive comparison of the converted value.
            for (var i = 0; i < series.Count; i++)
                command.Parameters.Add(seriesParameters[i], SqlDbType.VarChar, 5).Value = series[i];

            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token)) allowed.Add(reader.GetString(0));
        }

        return new OperatorAccess(ident, name, centreId, centreName, allowed);
    }

    /// <summary>
    /// Transfers that have a cash entry in ERP, together with how far they are settled.
    /// </summary>
    /// <remarks>
    /// The queue deliberately filters neither by confidence nor by settlement state - the filter
    /// in the window does that. The automat settles the certain payments itself, so settled ones
    /// are the majority here; the accountant wants to see them all the same, if only to check
    /// what the automat did.
    ///
    /// Entries flagged "not subject to settlement" (<c>KAZ_Rozliczony = 2</c>) are left out: those
    /// are VAT legs of split payments and bank commissions - they are not open items and never
    /// will be.
    ///
    /// The query is driven by <c>CDN.Zapisy</c>, with our own proposals joined on the outside.
    /// It used to be the other way round, and that hid every entry the service had not downloaded
    /// itself - which is all of them on the card registers, because the bank does not serve those
    /// over the API and they reach ERP through its own statement import. Driving it from ERP also
    /// means one row per entry however many proposals point at it.
    /// </remarks>
    public async Task<IReadOnlyList<PaymentRow>> GetQueueAsync(DateTime from, CancellationToken token = default)
    {
        if (registers.All.Count == 0) return [];

        var seriesParameters = registers.All.Select((_, i) => $"@s{i}").ToArray();

        var sql = $"""
            SELECT ISNULL(p.PaymentId, 0)                 AS PaymentId,
                   z.KAZ_GIDNumer                         AS ErpEntryId,
                   ISNULL(p.BankExternalId, RTRIM(ISNULL(z.KAZ_NumerDokumentu, ''))) AS BankExternalId,
                   CONVERT(DATE, DATEADD(DAY, rap.KRP_DataOtwarcia, '1800-12-28'))   AS BookingDate,
                   z.KAZ_Kwota                            AS Amount,
                   z.KAZ_Pozostaje                        AS Remaining,
                   RTRIM(z.KAZ_Waluta)                    AS Currency,
                   RTRIM(rap.KRP_Seria)                   AS RegisterSeries,
                   -- Without a proposal the entry still carries what the ERP import wrote on it:
                   -- the counterparty in Opis and the payment title in Tresc.
                   ISNULL(p.PayerName, RTRIM(ISNULL(z.KAZ_Opis, '')))     AS PayerName,
                   ISNULL(p.PayerAccount, '')                             AS PayerAccount,
                   ISNULL(p.Description, RTRIM(ISNULL(z.KAZ_Tresc, '')))  AS Description,
                   ISNULL(p.Confidence, 'None')                           AS Confidence,
                   ISNULL(p.ContractorId, z.KAZ_KntNumer)                 AS ContractorId,
                   ISNULL(RTRIM(k.Knt_Akronim), '')       AS ContractorAcronym,
                   ISNULL(RTRIM(k.Knt_Nazwa1), '')        AS ContractorName,
                   ISNULL(p.ContractorSource, '')         AS ContractorSource,
                   ISNULL(p.ContractorFromBankAccount, CONVERT(BIT, 0)) AS ContractorFromBankAccount,
                   ISNULL(p.Notes, '')                    AS Notes,
                   ISNULL(p.Strategy, '')                 AS Strategy,
                   ISNULL(p.Status, 'Proposed')           AS Status,
                   ISNULL(p.PostingError, '')             AS PostingError,
                   ISNULL(p.BankBic, '')                  AS BankBic,
                   ISNULL(p.BankName, '')                 AS BankName,
                   ISNULL(p.SourceFile, '')               AS SourceFile,
                   -- KAZ_RP is the entry's side: 1 an outgoing payment, 2 an incoming one. That
                   -- is how an entry with no proposal of ours reveals its direction (it agrees
                   -- with what the service recorded on 2593 of the 2597 entries that have both).
                   ISNULL(p.Direction, CASE WHEN z.KAZ_RP = 2 THEN 'P' ELSE 'R' END) AS Direction,
                   -- The settlement state is computed here, next to the data: 'R' settled in
                   -- full, 'C' in part, 'N' untouched. A penny of difference is rounding, not an
                   -- open item.
                   CASE
                       WHEN z.KAZ_Rozliczony = 1 OR z.KAZ_Pozostaje <= 0.004 THEN 'R'
                       WHEN z.KAZ_Pozostaje < z.KAZ_Kwota - 0.004            THEN 'C'
                       ELSE 'N'
                   END AS SettlementState
            FROM CDN.Zapisy AS z
            INNER JOIN CDN.Raporty AS rap
                ON rap.KRP_GIDNumer = z.KAZ_KRPNumer AND rap.KRP_GIDTyp = z.KAZ_KRPTyp
            -- One proposal per entry, chosen deterministically: the service can in principle hold
            -- more than one row pointing at the same entry, and the queue must not double up.
            OUTER APPLY (
                SELECT TOP 1 q.*
                FROM pay.Payment AS q
                WHERE q.ErpEntryId = z.KAZ_GIDNumer
                ORDER BY q.PaymentId
            ) AS p
            LEFT JOIN CDN.KntKarty AS k
                ON k.Knt_GIDNumer = ISNULL(p.ContractorId, z.KAZ_KntNumer) AND k.Knt_GIDTyp = 32
            WHERE RTRIM(rap.KRP_Seria) IN ({string.Join(", ", seriesParameters)})
              AND rap.KRP_DataOtwarcia >= DATEDIFF(DAY, '1800-12-28', @from)
              AND z.KAZ_Rozliczony <> 2
            ORDER BY rap.KRP_DataOtwarcia DESC, z.KAZ_Kwota DESC
            """;

        var rows = new List<PaymentRow>();

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(token);
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 120 };
        command.Parameters.AddWithValue("@from", from.Date);

        var index = 0;
        foreach (var series in registers.All) command.Parameters.AddWithValue(seriesParameters[index++], series);

        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            rows.Add(new PaymentRow(
                reader.GetInt64(0), reader.GetInt32(1), reader.GetString(2), reader.GetDateTime(3),
                reader.GetDecimal(4), reader.GetDecimal(5), reader.GetString(6), reader.GetString(7),
                reader.GetString(8), reader.GetString(9), reader.GetString(10), reader.GetString(11),
                reader.GetInt32(12), reader.GetString(13), reader.GetString(14), reader.GetString(15),
                reader.GetBoolean(16), reader.GetString(17), reader.GetString(18),
                reader.GetString(19), reader.GetString(20),
                reader.GetString(21), reader.GetString(22),
                reader.GetString(24) == "P", reader.GetString(23),
                registers.IsCard(reader.GetString(7)), registers.WithoutSettlement(reader.GetString(7)),
                SettlementStates.Parse(reader.GetString(25))));
        }

        return rows;
    }

    /// <summary>The engine's hints for the whole queue - in one query, not row by row.</summary>
    public async Task<IReadOnlyList<SuggestionRow>> GetSuggestionsAsync(
        DateTime from, CancellationToken token = default)
    {
        const string sql = """
            SELECT a.PaymentId, a.DocType, a.DocId, a.DocLp, a.DocNumber, a.Amount,
                   a.Score, ISNULL(a.Reason, '') AS Reason
            FROM pay.Allocation AS a
            INNER JOIN pay.Payment AS p ON p.PaymentId = a.PaymentId
            INNER JOIN CDN.Zapisy AS z ON z.KAZ_GIDNumer = p.ErpEntryId
            WHERE p.PostingCategory = 'Standard'
              AND p.BookingDate >= @from
              AND z.KAZ_Rozliczony <> 2
            """;

        var rows = new List<SuggestionRow>();

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(token);
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 120 };
        command.Parameters.AddWithValue("@from", from.Date);

        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            rows.Add(new SuggestionRow(
                reader.GetInt64(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3),
                reader.GetString(4), reader.GetDecimal(5), (double)reader.GetDecimal(6), reader.GetString(7)));
        }

        return rows;
    }

    /// <summary>
    /// Every open payment of a contractor - receivables and liabilities together.
    /// </summary>
    /// <remarks>
    /// This is not narrowed to sales documents. The automat looks only at those because it matches
    /// customer payments, but the accountant also settles cases outside that pattern - a transfer
    /// from a courier, say, whose open items sit on simplified accrual notes.
    ///
    /// What is filtered out are the types ERP will not settle against a cash entry at all - see
    /// <see cref="SettlementDocumentTypes"/>.
    /// </remarks>
    public Task<IReadOnlyList<DocumentRow>> GetOpenDocumentsAsync(
        int contractorId, CancellationToken token = default)
    {
        var sql = $$"""
            {{DocumentSelect}}
              AND pl.TrP_KntNumer = @knt
              AND pl.TrP_Rozliczona <> 1
              AND pl.TrP_Pozostaje > 0
            ORDER BY pl.TrP_Termin, pl.TrP_GIDNumer
            """;

        return ReadDocumentsAsync(sql, token, ("@knt", contractorId));
    }

    /// <summary>
    /// Looking a document up by number - the way out when the service named the wrong contractor
    /// and the accountant has the invoice number from the payment title in front of them.
    /// </summary>
    /// <remarks>
    /// The number is searched for in every header, not only in <c>CDN.TraNag</c> - otherwise
    /// import invoices, notes, dunning letters and customs documents cannot be found, even though
    /// we can already show their numbers on the list.
    /// </remarks>
    public Task<IReadOnlyList<DocumentRow>> FindDocumentsByNumberAsync(
        int number, CancellationToken token = default)
    {
        var sql = $$"""
            {{DocumentSelect}}
              AND @number IN (n.TrN_TrNNumer, imp.ImN_ImNNumer, upo.UPN_Numer,
                              mem.MEN_Numer, sad.SaN_SaNNumer)
              AND pl.TrP_Rozliczona <> 1
              AND pl.TrP_Pozostaje > 0
            ORDER BY COALESCE(n.TrN_TrNRok, imp.ImN_ImNRok, upo.UPN_Rok,
                              sad.SaN_SaNRok, mem.MEN_RokMiesiac / 100) DESC
            OFFSET 0 ROWS FETCH NEXT 100 ROWS ONLY
            """;

        return ReadDocumentsAsync(sql, token, ("@number", number));
    }

    private async Task<IReadOnlyList<DocumentRow>> ReadDocumentsAsync(
        string sql, CancellationToken token, params (string Name, object Value)[] parameters)
    {
        var rows = new List<DocumentRow>();

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(token);
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 120 };
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);

        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            // GIDTyp, GIDLp and TrP_Typ are smallint in ERP - read without assuming a width.
            rows.Add(new DocumentRow(
                Convert.ToInt32(reader.GetValue(0)), reader.GetInt32(1), Convert.ToInt32(reader.GetValue(2)),
                reader.IsDBNull(3) ? string.Empty : reader.GetString(3).Trim(),
                Convert.ToInt32(reader.GetValue(4)), reader.GetDecimal(5), reader.GetDecimal(6),
                reader.GetString(7).Trim(), XlDate.ToDateTime(reader.GetInt32(8)),
                reader.GetInt32(9), reader.GetString(10), reader.GetString(11),
                reader.GetString(12)));
        }

        return rows;
    }

    /// <summary>
    /// The settlements created against a given bank entry, so that they can be revoked. The entry
    /// may sit on either side of a settlement, so both are checked.
    /// </summary>
    public async Task<IReadOnlyList<SettlementRow>> GetEntrySettlementsAsync(
        int erpEntryId, CancellationToken token = default)
    {
        const string sql = """
            SELECT r.R2_ID, r.R2_GIDFirma, r.R2_KwotaWal1,
                   r.R2_DataRozliczenia, ISNULL(RTRIM(o.Ope_Ident), '') AS Operator,
                   CASE WHEN r.R2_Dok1Typ = @entryType AND r.R2_Dok1Numer = @entry
                        THEN r.R2_Dok2Numer ELSE r.R2_Dok1Numer END AS DocId
            FROM CDN.Rozliczenia AS r
            LEFT JOIN CDN.OpeKarty AS o ON o.Ope_GIDNumer = r.R2_OpeNumerRL
            WHERE (r.R2_Dok1Typ = @entryType AND r.R2_Dok1Numer = @entry)
               OR (r.R2_Dok2Typ = @entryType AND r.R2_Dok2Numer = @entry)
            ORDER BY r.R2_ID
            """;

        var rows = new List<SettlementRow>();

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(token);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@entry", erpEntryId);
        command.Parameters.AddWithValue("@entryType", XlSettlementEngine.CashEntryGidType);

        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            rows.Add(new SettlementRow(
                reader.GetInt32(0), reader.GetInt32(1), $"dokument {reader.GetInt32(5)}",
                reader.GetDecimal(2), XlDate.ToDateTime(reader.GetInt32(3)), reader.GetString(4)));
        }

        return rows;
    }

    /// <summary>
    /// The contractor a given account is attached to. It answers only when the answer is
    /// unambiguous.
    /// </summary>
    /// <remarks>
    /// The engine establishes a contractor for incoming payments only, because those are all it
    /// matches against receivables. On outgoing ones the counterparty account belongs to the
    /// recipient and is looked up the same way - but only on demand, for a single transfer. The
    /// same query run in bulk over the whole queue stretched loading it to a dozen seconds and more.
    /// </remarks>
    public async Task<ContractorRow?> FindContractorByAccountAsync(
        string account, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(account)) return null;

        // The number is searched for in both forms, with and without the country code. ERP
        // stores everything it considers an IBAN without one - Polish accounts included - while
        // the bank sends them with it. Comparing a single form lost the contractor on 133
        // accounts in the register.
        const string sql = """
            SELECT k.Knt_GIDNumer, RTRIM(k.Knt_Akronim), ISNULL(RTRIM(k.Knt_Nazwa1), ''),
                   ISNULL(RTRIM(k.Knt_Nip), '')
            FROM CDN.KntKarty AS k
            WHERE k.Knt_GIDNumer IN (
                SELECT RkB_ObiNumer FROM CDN.RachunkiBankowe
                WHERE RkB_ObiTyp = 32
                  AND REPLACE(RkB_NrRachunku, ' ', '') IN (@pelny, @bezKraju)
                UNION
                SELECT NRB_ObNumer FROM CDN.NumeryRachunkow
                WHERE NRB_ObTyp = 32
                  AND REPLACE(NRB_NrRachunkuZnorm, ' ', '') IN (@pelny, @bezKraju))
            """;

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(token);
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 60 };
        command.Parameters.AddWithValue("@pelny", IbanParts.Compact(account));
        command.Parameters.AddWithValue("@bezKraju", IbanParts.WithoutCountryCode(account));

        var hits = new List<ContractorRow>();

        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            hits.Add(new ContractorRow(
                reader.GetInt32(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        }

        // An account attached to several cards decides nothing - better to suggest nothing.
        return hits.Count == 1 ? hits[0] : null;
    }

    /// <summary>
    /// The card of the bank holding a given account.
    /// </summary>
    /// <remarks>
    /// We look for a card whose clearing code is <b>exactly</b> the bank code cut out of the
    /// account number according to the IBAN registry, and in that account's country at that. It
    /// used to be enough for the card's number to be any prefix of the account number - and that
    /// missed wildly: a Dutch account starting with "INGB" was given the card of Romanian ING, and
    /// a Polish national number could hit Bank of China under the number "36". Short foreign bank
    /// codes won all the more easily because the ordering put the IBAN flag ahead of match length.
    ///
    /// A card with the IBAN flag takes precedence, because that is the only kind XL will match by
    /// itself; a card without the flag is returned so that it can be corrected rather than
    /// duplicated. Where the layout of numbers in a country is unknown to us we do not guess -
    /// better to ask the operator than to substitute somebody else's bank.
    /// </remarks>
    public async Task<BankRef?> FindBankAsync(
        string account, string bic, CancellationToken token = default)
    {
        // 781 cards in the register have an empty Bnk_KodKraju, Polish ones among them - the
        // filter has to let them through, or domestic lookups stop working.
        const string byIban = """
            SELECT TOP 1 ISNULL(RTRIM(Bnk_Kod), ''), Bnk_GIDNumer, ISNULL(Bnk_IBAN, 0)
            FROM CDN.Banki
            WHERE RTRIM(Bnk_Numer) = @kod
              AND (ISNULL(RTRIM(Bnk_KodKraju), '') = '' OR RTRIM(Bnk_KodKraju) = @kraj)
            ORDER BY Bnk_IBAN DESC, Bnk_GIDNumer
            """;

        // A card found by the bank's SWIFT code will not bind to the account by itself, but it
        // gives us the name and address, and the link is attached afterwards. A SWIFT code alone
        // hits many cards - the register holds one per branch - so the ordering has to be
        // repeatable.
        const string bySwift = """
            SELECT TOP 1 ISNULL(RTRIM(Bnk_Kod), ''), Bnk_GIDNumer
            FROM CDN.Banki
            WHERE @swift <> '' AND LEFT(RTRIM(Bnk_Swift), 8) = LEFT(@swift, 8)
            ORDER BY CASE WHEN RTRIM(Bnk_Swift) = @swift THEN 0 ELSE 1 END, Bnk_GIDNumer
            """;

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(token);

        var bankCode = IbanParts.BankCode(account);

        if (bankCode.Length > 0)
        {
            await using var command = new SqlCommand(byIban, connection);
            command.Parameters.AddWithValue("@kod", bankCode);
            command.Parameters.AddWithValue("@kraj", IbanParts.Country(account));

            await using var reader = await command.ExecuteReaderAsync(token);

            if (await reader.ReadAsync(token))
            {
                // A card with the right number but the IBAN flag off is the same bank with one
                // thing to fix - no reason to create a second card for it.
                var binds = reader.GetInt16(2) == 1;
                return new BankRef(reader.GetString(0), reader.GetInt32(1), binds);
            }
        }

        var swift = bic.Trim();
        if (swift.Length == 0) return null;

        await using var fallback = new SqlCommand(bySwift, connection);
        fallback.Parameters.AddWithValue("@swift", swift);

        await using var second = await fallback.ExecuteReaderAsync(token);
        return await second.ReadAsync(token)
            ? new BankRef(second.GetString(0), second.GetInt32(1), BindsInXl: false)
            : null;
    }

    /// <summary>Name, city, postal code and SWIFT of a bank card - to prefill the window.</summary>
    public async Task<BankDetails?> GetBankAsync(int bankId, CancellationToken token = default)
    {
        const string sql = """
            SELECT ISNULL(RTRIM(Bnk_Swift), ''), ISNULL(RTRIM(Bnk_Nazwa), ''),
                   ISNULL(RTRIM(Bnk_Miasto), ''), ISNULL(RTRIM(Bnk_KodP), ''),
                   ISNULL(RTRIM(Bnk_KodKraju), ''), ISNULL(RTRIM(Bnk_Numer), '')
            FROM CDN.Banki WHERE Bnk_GIDNumer = @bank
            """;

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(token);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@bank", bankId);

        await using var reader = await command.ExecuteReaderAsync(token);

        return await reader.ReadAsync(token)
            ? new BankDetails(
                reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetString(4), reader.GetString(5))
            : null;
    }

    /// <summary>
    /// Which of the given contractors already hold this account in the register.
    /// </summary>
    /// <remarks>
    /// Our own table only remembers whether the account is what identified the contractor. That is
    /// not enough: the account may have reached the card later, by hand in ERP for instance, and
    /// the "add account" button would then tempt the operator while <c>XLNowyRachunek</c> failed
    /// with an error about an existing number. An account stored as an IBAN loses its country
    /// prefix, so it is searched for in both forms.
    ///
    /// Pairs are returned rather than bare contractor ids: one contractor may hold several
    /// accounts, and one of them already being on the card says nothing about the others.
    /// </remarks>
    public async Task<IReadOnlySet<(int ContractorId, string Account)>> ContractorsWithAccountAsync(
        IReadOnlyList<(int ContractorId, string Account)> pairs, CancellationToken token = default)
    {
        const string sql = """
            SELECT COUNT(*)
            FROM CDN.RachunkiBankowe
            WHERE RkB_ObiTyp = 32
              AND RkB_ObiNumer = @knt
              AND REPLACE(RTRIM(RkB_NrRachunku), ' ', '') IN (@pelny, @bezKraju)
            """;

        var found = new HashSet<(int ContractorId, string Account)>();
        if (pairs.Count == 0) return found;

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(token);

        foreach (var (contractorId, account) in pairs.Distinct())
        {
            if (contractorId == 0 || account.Trim().Length == 0) continue;

            await using var command = new SqlCommand(sql, connection);
            command.Parameters.AddWithValue("@knt", contractorId);
            command.Parameters.AddWithValue("@pelny", IbanParts.Compact(account));
            command.Parameters.AddWithValue("@bezKraju", IbanParts.WithoutCountryCode(account));

            if (await command.ExecuteScalarAsync(token) is int count and > 0)
            {
                found.Add((contractorId, account));
            }
        }

        return found;
    }

    /// <summary>
    /// Out of the given contractor-account pairs, returns the accounts that still have no bank in
    /// the register.
    /// </summary>
    /// <remarks>
    /// Used to check whether attaching the bank worked. An account stored as an IBAN loses its
    /// country prefix, so it is searched for in both forms.
    /// </remarks>
    public async Task<IReadOnlyList<string>> AccountsWithoutBankAsync(
        IReadOnlyList<(int ContractorId, string Account)> pairs, CancellationToken token = default)
    {
        const string sql = """
            SELECT COUNT(*)
            FROM CDN.RachunkiBankowe
            WHERE RkB_ObiTyp = 32
              AND RkB_ObiNumer = @knt
              AND ISNULL(RkB_BnkNumer, 0) = 0
              AND REPLACE(RTRIM(RkB_NrRachunku), ' ', '') IN (@pelny, @bezKraju)
            """;

        var missing = new List<string>();

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(token);

        foreach (var (contractorId, account) in pairs)
        {
            await using var command = new SqlCommand(sql, connection);
            command.Parameters.AddWithValue("@knt", contractorId);
            command.Parameters.AddWithValue("@pelny", IbanParts.Compact(account));
            command.Parameters.AddWithValue("@bezKraju", IbanParts.WithoutCountryCode(account));

            if (await command.ExecuteScalarAsync(token) is int count and > 0) missing.Add(account.Trim());
        }

        return missing;
    }

    /// <summary>Looking a contractor up by acronym, name or tax id.</summary>
    public async Task<IReadOnlyList<ContractorRow>> FindContractorsAsync(
        string query, CancellationToken token = default)
    {
        const string sql = """
            SELECT TOP 60 Knt_GIDNumer, RTRIM(Knt_Akronim), ISNULL(RTRIM(Knt_Nazwa1), ''),
                   ISNULL(RTRIM(Knt_Nip), '')
            FROM CDN.KntKarty
            WHERE Knt_Archiwalny = 0
              AND (Knt_Akronim LIKE @q OR Knt_Nazwa1 LIKE @q OR Knt_Nip LIKE @q)
            ORDER BY Knt_Akronim
            """;

        var rows = new List<ContractorRow>();

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(token);
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 60 };
        command.Parameters.AddWithValue("@q", $"%{query}%");

        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            rows.Add(new ContractorRow(
                reader.GetInt32(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        }

        return rows;
    }
}
