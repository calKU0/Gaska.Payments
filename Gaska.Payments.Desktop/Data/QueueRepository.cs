using System.Data;
using Gaska.Payments.Domain.Model;
using Gaska.Payments.Erp;
using Gaska.Payments.Erp.Reads;
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
                            NULLIF(RTRIM(imp.ImN_DokumentObcy), ''), '') AS DokumentObcy,
                   pl.TrP_Rozliczona
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
    /// Settled entries and those flagged "not subject to settlement" are fetched only when the
    /// filter asks for them. They are eighteen thousand of the eighteen and a half over the default
    /// window and take 2,3 s to fetch against 0,1 s for the rest, so paying for them on every load
    /// meant every refresh - and every settlement - waited on history nobody had asked to see.
    ///
    /// Entries flagged "not subject to settlement" (<c>KAZ_Rozliczony = 2</c>) carry a state of
    /// their own on the filter, unticked by default. They are VAT legs of
    /// split payments, commissions and courier payouts - not open items and never will be - but
    /// they have to be reachable, because taking the flag off one is the only way back when it was
    /// set by mistake.
    ///
    /// The query is driven by <c>CDN.Zapisy</c>, with our own proposals joined on the outside.
    /// It used to be the other way round, and that hid every entry the service had not downloaded
    /// itself - which is all of them on the card registers, because the bank does not serve those
    /// over the API and they reach ERP through its own statement import. Driving it from ERP also
    /// means one row per entry however many proposals point at it.
    /// </remarks>
    /// <summary>
    /// The entries that still have work in them: nothing settled against them yet, or only part.
    /// </summary>
    /// <remarks>
    /// The dividing line between what the queue needs at once and what it fetches only if asked.
    /// Over the sixty days the window opens on, this is 503 entries out of 18 591 - the rest is
    /// history, already settled or flagged, and both of those start unticked on the filter.
    /// </remarks>
    public const string HasWorkSql = "KAZ_Rozliczony = 0 AND KAZ_Pozostaje > 0.004";

    public async Task<IReadOnlyList<PaymentRow>> GetQueueAsync(
        DateTime from, bool withHistory, CancellationToken token = default)
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
                   -- The party on the entry counts only when it really is a contractor. A tax
                   -- return is booked against an office (KAZ_KNTTyp 4304) and a payroll against
                   -- an employee (944), and those numbers come from sequences of their own - the
                   -- II Urząd Skarbowy is number 4, which is also the contractor with acronym
                   -- "0002". Joined on the number alone, 2183 entries on this register showed
                   -- somebody else's card.
                   ISNULL(p.ContractorId,
                          CASE WHEN z.KAZ_KNTTyp = 32 THEN z.KAZ_KntNumer ELSE 0 END) AS ContractorId,
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
                       WHEN z.KAZ_Rozliczony = 2                             THEN 'X'
                       WHEN z.KAZ_Rozliczony = 1 OR z.KAZ_Pozostaje <= 0.004 THEN 'R'
                       WHEN z.KAZ_Pozostaje < z.KAZ_Kwota - 0.004            THEN 'C'
                       ELSE 'N'
                   END AS SettlementState,
                   -- The account in the chart of accounts the entry is posted against. The service
                   -- fills it in from the contractor's other entries, which is a guess; accounting
                   -- sees it here and corrects it where the guess was wrong.
                   RTRIM(ISNULL(z.KAZ_KontoPrzec, '')) AS EntryAccount,
                   -- Who the entry is booked against when that is not a contractor, so the
                   -- accountant reads "II URZĄD SKARBOWY" rather than an unrelated acronym.
                   CASE
                       WHEN p.ContractorId IS NOT NULL OR z.KAZ_KNTTyp IN (0, 32) THEN ''
                       WHEN z.KAZ_KNTTyp = 4304 THEN
                           RTRIM(ISNULL(urz.URZ_Akronim, 'urząd ' + CAST(z.KAZ_KntNumer AS VARCHAR(12))))
                       WHEN z.KAZ_KNTTyp = 944 THEN
                           RTRIM(ISNULL(prc.Prc_Akronim, 'pracownik ' + CAST(z.KAZ_KntNumer AS VARCHAR(12))))
                       ELSE 'podmiot typu ' + CAST(z.KAZ_KNTTyp AS VARCHAR(8))
                   END AS OtherParty
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
                ON k.Knt_GIDNumer = ISNULL(p.ContractorId,
                       CASE WHEN z.KAZ_KNTTyp = 32 THEN z.KAZ_KntNumer ELSE 0 END)
               AND k.Knt_GIDTyp = 32
            LEFT JOIN CDN.Urzedy AS urz
                ON z.KAZ_KNTTyp = 4304 AND urz.URZ_GIDNumer = z.KAZ_KntNumer
            LEFT JOIN CDN.PrcKarty AS prc
                ON z.KAZ_KNTTyp = 944 AND prc.Prc_GIDNumer = z.KAZ_KntNumer
            WHERE RTRIM(rap.KRP_Seria) IN ({string.Join(", ", seriesParameters)})
              AND rap.KRP_DataOtwarcia >= DATEDIFF(DAY, '1800-12-28', @from)
              {(withHistory ? string.Empty : $"AND z.{HasWorkSql}")}
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
                SettlementStates.Parse(reader.GetString(25)), reader.GetString(26),
                reader.GetString(27)));
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
              AND pl.TrP_Rozliczona IN (0, 2)
              AND pl.TrP_Pozostaje > 0
            ORDER BY pl.TrP_Termin, pl.TrP_GIDNumer
            """;

        return ReadDocumentsAsync(sql, token, ("@knt", contractorId));
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
                reader.GetString(12), Convert.ToInt32(reader.GetValue(13))));
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
        // Archived accounts are left out, on both sides of the union: they are no longer the
        // contractor's, and reading them makes the payer ambiguous - see BankAccountSql.
        //
        // A retired contractor card is a different matter and is kept: an account is sometimes on
        // one and on no other. It only loses to a live card holding the same number, which is what
        // the ordering below does - the caller takes the answer only when it is unambiguous, and
        // ranking the live cards first is what makes it so.
        var sql = $"""
            WITH {BankAccountSql.RetiredAccounts}
            SELECT k.Knt_GIDNumer, RTRIM(k.Knt_Akronim), ISNULL(RTRIM(k.Knt_Nazwa1), ''),
                   ISNULL(RTRIM(k.Knt_Nip), ''), ISNULL(k.Knt_Archiwalny, 0) AS Archiwalny
            FROM CDN.KntKarty AS k
            WHERE k.Knt_GIDNumer IN (
                SELECT RkB_ObiNumer FROM CDN.RachunkiBankowe
                WHERE RkB_ObiTyp = 32
                  AND {BankAccountSql.InUse}
                  AND REPLACE(RkB_NrRachunku, ' ', '') IN (@pelny, @bezKraju)
                UNION
                SELECT n.NRB_ObNumer FROM CDN.NumeryRachunkow AS n
                WHERE n.NRB_ObTyp = 32
                  AND REPLACE(n.NRB_NrRachunkuZnorm, ' ', '') IN (@pelny, @bezKraju)
                  AND NOT EXISTS (
                      SELECT 1 FROM wycofane AS w
                      WHERE w.Knt = n.NRB_ObNumer
                        AND w.Numer = REPLACE(RTRIM(n.NRB_NrRachunkuZnorm), ' ', '')))
            """;

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(token);
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 60 };
        command.Parameters.AddWithValue("@pelny", IbanParts.Compact(account));
        command.Parameters.AddWithValue("@bezKraju", IbanParts.WithoutCountryCode(account));

        var hits = new List<ContractorRow>();
        var live = new List<ContractorRow>();

        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            var contractor = new ContractorRow(
                reader.GetInt32(0), reader.GetString(1), reader.GetString(2), reader.GetString(3));

            hits.Add(contractor);
            if (reader.GetInt16(4) == 0) live.Add(contractor);
        }

        // A live card wins over a retired one holding the same number; retired ones still answer
        // when nothing live does.
        var candidates = live.Count > 0 ? live : hits;

        // An account attached to several live cards decides nothing - better to suggest nothing.
        return candidates.Count == 1 ? candidates[0] : null;
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

    /// <summary>
    /// The accounts in the chart of accounts that belong to a contractor, commonest first.
    /// </summary>
    /// <remarks>
    /// Two sources, because neither is enough on its own. <c>CDN.KntKonta</c> is what ERP declares
    /// for the contractor - 124 380 rows over 34 044 contractors here - but it is kept per
    /// accounting period and is often left empty in the current one, so a contractor genuinely in
    /// use can have nothing there. The other source is what their cash entries actually carry,
    /// which is never empty for anybody who has been paid before, and which is also what the
    /// service guesses from when it settles.
    ///
    /// Ordered by how often an account is used, so the one the automat would have chosen is at the
    /// top of the list. It matters for the 2 587 contractors here who have more than one - for
    /// everybody else the list has a single entry and the operator need not think about it.
    /// </remarks>
    public async Task<IReadOnlyDictionary<int, IReadOnlyList<string>>> GetContractorAccountsAsync(
        IReadOnlyCollection<int> contractorIds, CancellationToken token = default)
    {
        var wanted = contractorIds.Where(id => id != 0).Distinct().ToList();
        if (wanted.Count == 0) return new Dictionary<int, IReadOnlyList<string>>();

        // The ids go in as one delimited string rather than as parameters: the queue holds a few
        // thousand contractors and a command takes at most 2 100 parameters.
        const string sql = """
            WITH knt AS (
                SELECT CAST(value AS INT) AS Knt FROM STRING_SPLIT(@ids, ',')
            ),
            uzywane AS (
                SELECT z.KAZ_KNTNumer AS Knt, RTRIM(z.KAZ_KontoPrzec) AS Konto, COUNT(*) AS Ile
                FROM CDN.Zapisy AS z
                INNER JOIN knt ON knt.Knt = z.KAZ_KNTNumer
                WHERE ISNULL(RTRIM(z.KAZ_KontoPrzec), '') <> ''
                GROUP BY z.KAZ_KNTNumer, RTRIM(z.KAZ_KontoPrzec)
            ),
            zkarty AS (
                SELECT k.KKT_KntNumer AS Knt, RTRIM(k.KKT_Konto) AS Konto
                FROM CDN.KntKonta AS k
                INNER JOIN knt ON knt.Knt = k.KKT_KntNumer
                WHERE k.KKT_KntTyp = 32 AND ISNULL(RTRIM(k.KKT_Konto), '') <> ''
                GROUP BY k.KKT_KntNumer, RTRIM(k.KKT_Konto)
            )
            SELECT w.Knt, w.Konto
            FROM (SELECT Knt, Konto FROM uzywane UNION SELECT Knt, Konto FROM zkarty) AS w
            LEFT JOIN uzywane AS u ON u.Knt = w.Knt AND u.Konto = w.Konto
            ORDER BY w.Knt, ISNULL(u.Ile, 0) DESC, w.Konto
            """;

        var accounts = new Dictionary<int, IReadOnlyList<string>>();

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(token);
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 180 };
        command.Parameters.AddWithValue("@ids", string.Join(',', wanted));

        await using var reader = await command.ExecuteReaderAsync(token);

        while (await reader.ReadAsync(token))
        {
            var contractorId = reader.GetInt32(0);

            if (!accounts.TryGetValue(contractorId, out var list))
            {
                list = new List<string>();
                accounts[contractorId] = list;
            }

            ((List<string>)list).Add(reader.GetString(1));
        }

        return accounts;
    }

    /// <summary>
    /// What the contractor has paid that nobody has allocated - their overpayment, per currency.
    /// </summary>
    /// <remarks>
    /// Money in, still open, on any register: an incoming cash entry with something left on it and
    /// no settlement behind it. Entries marked "nie rozliczaj" (<c>KAZ_Rozliczony = 2</c>) are left
    /// out - somebody has already decided those are not to be matched against anything.
    ///
    /// The party has to be a contractor, not merely carry the contractor's number. Employees (944)
    /// and offices (4304) are numbered from sequences of their own, so an employee's cash advance
    /// counted as somebody's overpayment: AGROMA-O, contractor 290, was shown 9 541,09 zł of which
    /// 2 526,12 was six payroll entries on employee 290 from 2021. On this register 63 contractors
    /// were inflated that way, by 29,2 million in total.
    ///
    /// Only what was already sitting there when this transfer arrived counts: an entry from an
    /// earlier day, or from the same day with a lower <c>KAZ_GIDNumer</c>. The question the figure
    /// answers is "was this contractor's money waiting before this came in", and a payment that
    /// landed afterwards is not an answer to it - nor is the transfer itself, which would otherwise
    /// make every unsettled transfer look like an overpayment of exactly its own amount.
    ///
    /// So two transfers of one contractor on one day read differently on purpose: the first shows
    /// nothing, the second shows the first.
    /// </remarks>
    public async Task<IReadOnlyList<ContractorOverpayment>> GetOverpaymentsAsync(
        int contractorId, int exceptEntryId, CancellationToken token = default)
    {
        if (contractorId == 0) return [];

        const string sql = """
            -- The day of the transfer being worked on. Its report's opening day is the one the
            -- queue shows, so the cut-off is the same date the accountant is reading on screen.
            DECLARE @dzien INT = (
                SELECT TOP 1 rap.KRP_DataOtwarcia
                FROM CDN.Zapisy AS z
                INNER JOIN CDN.Raporty AS rap
                    ON rap.KRP_GIDNumer = z.KAZ_KRPNumer AND rap.KRP_GIDTyp = z.KAZ_KRPTyp
                WHERE z.KAZ_GIDNumer = @except);

            SELECT RTRIM(z.KAZ_Waluta) AS Waluta, COUNT(*) AS Wplat,
                   SUM(z.KAZ_Pozostaje) AS Kwota, MIN(rap.KRP_DataOtwarcia) AS Najstarszy
            FROM CDN.Zapisy AS z
            INNER JOIN CDN.Raporty AS rap
                ON rap.KRP_GIDNumer = z.KAZ_KRPNumer AND rap.KRP_GIDTyp = z.KAZ_KRPTyp
            WHERE z.KAZ_KNTNumer = @knt
              AND z.KAZ_KNTTyp = 32
              AND z.KAZ_RP = 2
              AND z.KAZ_Rozliczony = 0
              AND z.KAZ_Pozostaje > 0.004
              -- Only money that was already there: an earlier day, or the same day but booked
              -- before this one. The entry itself falls out of this by itself - its own number is
              -- not smaller than its own.
              AND (@dzien IS NULL
                   OR rap.KRP_DataOtwarcia < @dzien
                   OR (rap.KRP_DataOtwarcia = @dzien AND z.KAZ_GIDNumer < @except))
            GROUP BY z.KAZ_Waluta
            ORDER BY SUM(z.KAZ_Pozostaje) DESC
            """;

        var found = new List<ContractorOverpayment>();

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(token);
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 60 };
        command.Parameters.AddWithValue("@knt", contractorId);
        command.Parameters.AddWithValue("@except", exceptEntryId);

        await using var reader = await command.ExecuteReaderAsync(token);

        while (await reader.ReadAsync(token))
        {
            found.Add(new ContractorOverpayment(
                reader.GetString(0), reader.GetInt32(1), reader.GetDecimal(2),
                XlDate.ToDateTime(reader.GetInt32(3))));
        }

        return found;
    }

    /// <summary>
    /// The whole chart of accounts for the current year - account number and name.
    /// </summary>
    /// <remarks>
    /// Read once when the queue is loaded and kept: 41 537 accounts, about a megabyte and a half
    /// of text, a second to fetch. Per row it would be unthinkable; once a session it is nothing,
    /// and it is what lets the account picker offer every account rather than only the ones this
    /// contractor happens to have used before.
    ///
    /// The plan is kept per year and month, so the rows are collapsed to one per account number.
    /// </remarks>
    public async Task<IReadOnlyList<AccountRow>> GetChartOfAccountsAsync(CancellationToken token = default)
    {
        const string sql = """
            SELECT RTRIM(k.KKS_Konto) AS Konto, MAX(RTRIM(ISNULL(k.KKS_Nazwa, ''))) AS Nazwa
            FROM CDN.Konta AS k
            WHERE k.KKS_Rok = (SELECT MAX(KKS_Rok) FROM CDN.Konta)
              AND RTRIM(k.KKS_Konto) <> ''
            GROUP BY RTRIM(k.KKS_Konto)
            ORDER BY RTRIM(k.KKS_Konto)
            """;

        var accounts = new List<AccountRow>(45000);

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(token);
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 180 };

        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token)) accounts.Add(new AccountRow(reader.GetString(0), reader.GetString(1)));

        return accounts;
    }

    /// <summary>The accounts of one contractor - after the operator swapped who the transfer is with.</summary>
    public async Task<IReadOnlyList<string>> GetContractorAccountsAsync(
        int contractorId, CancellationToken token = default)
    {
        var found = await GetContractorAccountsAsync([contractorId], token);
        return found.TryGetValue(contractorId, out var accounts) ? accounts : [];
    }

    /// <summary>Name, address and SWIFT of a bank card - to prefill the window.</summary>
    public async Task<BankDetails?> GetBankAsync(int bankId, CancellationToken token = default)
    {
        const string sql = """
            SELECT ISNULL(RTRIM(Bnk_Swift), ''), ISNULL(RTRIM(Bnk_Nazwa), ''),
                   ISNULL(RTRIM(Bnk_Ulica), ''), ISNULL(RTRIM(Bnk_Miasto), ''),
                   ISNULL(RTRIM(Bnk_KodP), ''), ISNULL(RTRIM(Bnk_KodKraju), ''),
                   ISNULL(RTRIM(Bnk_Numer), '')
            FROM CDN.Banki WHERE Bnk_GIDNumer = @bank
            """;

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(token);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@bank", bankId);

        await using var reader = await command.ExecuteReaderAsync(token);

        return await reader.ReadAsync(token)
            ? new BankDetails(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.GetString(5), reader.GetString(6))
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
        // Archived accounts count here, and only here. The question this answers is not "whose
        // account is this" but "is this number already on the card" - and XLNowyRachunek refuses a
        // number that is there, archived or not. Filtering them out would show the operator an
        // "add account" button that fails every time they press it.
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
        var sql = $"""
            SELECT COUNT(*)
            FROM CDN.RachunkiBankowe
            WHERE RkB_ObiTyp = 32
              AND RkB_ObiNumer = @knt
              AND ISNULL(RkB_BnkNumer, 0) = 0
              AND {BankAccountSql.InUse}
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
