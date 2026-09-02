using System.Text.RegularExpressions;
using Gaska.Payments.Domain.Couriers;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace Gaska.Payments.Erp;

/// <summary>An open item of a document a parcel was sent against.</summary>
/// <param name="PaymentType">1 is a liability, 2 a receivable - a sale collected on delivery.</param>
public sealed record CodDocument(
    int DocType,
    int DocId,
    int DocLp,
    string DocNumber,
    int ContractorId,
    decimal Amount,
    decimal Remaining,
    int PaymentType);

/// <summary>
/// Finds in ERP what a parcel was carrying: the documents and the customer.
/// </summary>
/// <remarks>
/// The chain is the one shipping keeps: the courier's waybill number is
/// <c>CDN.Wysylki.WYS_NumerObcy</c>, a shipment holds parcels in <c>CDN.WysPaczki</c>, and a parcel
/// points at its documents through <c>CDN.WysRelacje</c>. Verified against live data - DPD's
/// 1049493400040U comes out as FS-40665/26/SPR for 4 287,23, the amount the file states.
///
/// The document numbers printed in the report are used as a second route rather than as the first
/// one. The chain is what shipping actually recorded; the printed number is what somebody typed
/// into a parcel description, and the two disagree often enough to matter - a parcel whose shipment
/// was cancelled and resent keeps the old number in its description.
/// </remarks>
public sealed partial class ShipmentReader(IOptions<ErpOptions> options)
{
    private const int ContractorGidType = 32;

    /// <summary>How many identifiers go into one query. Long IN lists stop the plan from folding.</summary>
    private const int BatchSize = 200;

    private readonly string _connectionString = options.Value.ConnectionString;

    /// <summary>
    /// The couriers' collective transfers sitting on the bank registers, over a window of days.
    /// </summary>
    /// <remarks>
    /// Read straight from <c>CDN.Zapisy</c> rather than from our own proposals, because a transfer
    /// may have reached ERP by its own statement import and because setting it aside afterwards
    /// needs the entry's identifier anyway.
    ///
    /// Everything credited in the window is returned and the matching is done in memory. A
    /// <c>LIKE '%...%'</c> per waybill would be a scan per parcel - thousands of them in a pass -
    /// and it would still miss the numbers the bank has broken across a line.
    /// </remarks>
    public async Task<IReadOnlyList<CodPayout>> GetPayoutsAsync(
        IReadOnlyCollection<string> registers, DateTime from, DateTime to,
        CancellationToken cancellationToken = default)
    {
        if (registers.Count == 0) return [];

        var parameters = registers.Select((_, i) => $"@r{i}").ToArray();

        var sql = $"""
            SELECT z.KAZ_GIDNumer,
                   CONVERT(DATE, DATEADD(DAY, rap.KRP_DataOtwarcia, '1800-12-28')) AS BookedOn,
                   z.KAZ_Kwota,
                   RTRIM(ISNULL(z.KAZ_Tresc, '')) + ' ' + RTRIM(ISNULL(z.KAZ_TrescCDC, '')) AS Title,
                   RTRIM(rap.KRP_Seria) AS Register,
                   CASE WHEN z.KAZ_Rozliczony = 2 THEN CONVERT(BIT, 1) ELSE CONVERT(BIT, 0) END AS Closed
            FROM CDN.Zapisy AS z
            INNER JOIN CDN.Raporty AS rap
                ON rap.KRP_GIDNumer = z.KAZ_KRPNumer AND rap.KRP_GIDTyp = z.KAZ_KRPTyp
            WHERE RTRIM(rap.KRP_Seria) IN ({string.Join(", ", parameters)})
              AND z.KAZ_RP = 2
              AND rap.KRP_DataOtwarcia BETWEEN DATEDIFF(DAY, '1800-12-28', @from)
                                           AND DATEDIFF(DAY, '1800-12-28', @to)
            """;

        var payouts = new List<CodPayout>();

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 120 };
        command.Parameters.AddWithValue("@from", from.Date);
        command.Parameters.AddWithValue("@to", to.Date);

        var index = 0;
        foreach (var register in registers) command.Parameters.AddWithValue(parameters[index++], register);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            payouts.Add(new CodPayout(
                reader.GetInt32(0), reader.GetDateTime(1), reader.GetDecimal(2),
                reader.GetString(3), reader.GetString(4), reader.GetBoolean(5)));
        }

        return payouts;
    }

    /// <summary>
    /// The open items of the documents each waybill was sent against.
    /// </summary>
    /// <remarks>
    /// Only receivables that are still open. A parcel whose invoice somebody has already settled
    /// by other means gives an empty list, which is the right answer: the entry is still to be
    /// created, there is simply nothing left for it to close.
    /// </remarks>
    public async Task<IReadOnlyDictionary<string, List<CodDocument>>> ByWaybillAsync(
        IReadOnlyCollection<string> waybills, CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT DISTINCT
                   RTRIM(w.WYS_NumerObcy) AS Waybill,
                   -- Cast because ERP types these narrowly - GIDTyp, GIDLp and Typ are smallint,
                   -- and reading a smallint as an int throws rather than widening.
                   CAST(pl.TrP_GIDTyp AS INT), pl.TrP_GIDNumer, CAST(pl.TrP_GIDLp AS INT),
                   CDN.NumerDokumentu(n.TrN_GIDTyp, n.TrN_SpiTyp, n.TrN_TrNTyp,
                                      n.TrN_TrNNumer, n.TrN_TrNRok, n.TrN_TrNSeria, 0) AS DocNumber,
                   pl.TrP_KntNumer, pl.TrP_Kwota, pl.TrP_Pozostaje, CAST(pl.TrP_Typ AS INT)
            FROM CDN.Wysylki AS w
            INNER JOIN CDN.WysPaczki AS p
                ON p.WyP_WysNumer = w.WYS_GIDNumer AND p.WyP_WysTyp = w.WYS_GIDTyp
            INNER JOIN CDN.WysRelacje AS r ON r.WYR_IdPaczki = p.WyP_IdPaczki
            INNER JOIN CDN.TraNag AS n
                ON n.TrN_GIDTyp = r.WYR_DokTyp AND n.TrN_GIDNumer = r.WYR_DokNumer
            INNER JOIN CDN.TraPlat AS pl
                ON pl.TrP_GIDTyp = n.TrN_GIDTyp AND pl.TrP_GIDNumer = n.TrN_GIDNumer
            WHERE RTRIM(w.WYS_NumerObcy) IN ({0})
              AND pl.TrP_KntTyp = @contractorType
              AND pl.TrP_Pozostaje > 0
            """;

        return await LookupAsync(sql, waybills, cancellationToken);
    }

    /// <summary>
    /// The open items of documents named by number - the route for a parcel shipping has no
    /// record of.
    /// </summary>
    /// <remarks>
    /// A document's printed number is assembled by a scalar function, so it cannot be looked up on
    /// an index. Left alone, the query builds the number for every open contractor item in the
    /// company before throwing nearly all of them away.
    ///
    /// So the ordinal buried in the number - the 40874 of FS-40874/26/SPR - is pulled out first and
    /// used to narrow the rows the function is called on. It is only a narrowing: the printed
    /// number is still compared in full afterwards, so the answer is exactly what it was. When any
    /// one of the numbers cannot be read that way, the narrowing is dropped altogether rather than
    /// risk excluding a document we should have found.
    /// </remarks>
    public async Task<IReadOnlyDictionary<string, List<CodDocument>>> ByDocumentNumberAsync(
        IReadOnlyCollection<string> numbers, CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT DISTINCT
                   d.DocNumber AS Waybill,
                   d.TrP_GIDTyp, d.TrP_GIDNumer, d.TrP_GIDLp, d.DocNumber,
                   d.TrP_KntNumer, d.TrP_Kwota, d.TrP_Pozostaje, d.TrP_Typ
            FROM (
                SELECT CAST(pl.TrP_GIDTyp AS INT) AS TrP_GIDTyp, pl.TrP_GIDNumer,
                       CAST(pl.TrP_GIDLp AS INT) AS TrP_GIDLp,
                       pl.TrP_KntNumer, pl.TrP_Kwota, pl.TrP_Pozostaje,
                       CAST(pl.TrP_Typ AS INT) AS TrP_Typ,
                       CDN.NumerDokumentu(n.TrN_GIDTyp, n.TrN_SpiTyp, n.TrN_TrNTyp,
                                          n.TrN_TrNNumer, n.TrN_TrNRok, n.TrN_TrNSeria, 0) AS DocNumber
                FROM CDN.TraNag AS n
                INNER JOIN CDN.TraPlat AS pl
                    ON pl.TrP_GIDTyp = n.TrN_GIDTyp AND pl.TrP_GIDNumer = n.TrN_GIDNumer
                WHERE pl.TrP_KntTyp = @contractorType
                  AND pl.TrP_Pozostaje > 0
                  {1}
            ) AS d
            WHERE d.DocNumber IN ({0})
            """;

        return await LookupAsync(sql, numbers, cancellationToken, Prefilter(numbers));
    }

    /// <summary>
    /// The document ordinals hidden in the numbers we are looking for, or null when any of them
    /// does not carry one.
    /// </summary>
    private static IReadOnlyList<int>? Prefilter(IReadOnlyCollection<string> numbers)
    {
        var ordinals = new List<int>(numbers.Count);

        foreach (var number in numbers)
        {
            var match = DocumentOrdinal().Match(number);

            if (!match.Success || !int.TryParse(match.Groups[1].Value, out var ordinal)) return null;

            ordinals.Add(ordinal);
        }

        return ordinals.Count > 0 ? ordinals : null;
    }

    /// <summary>The number between the dash and the first slash: FS-<b>40874</b>/26/SPR.</summary>
    [GeneratedRegex(@"-(\d+)/")]
    private static partial Regex DocumentOrdinal();

    private async Task<IReadOnlyDictionary<string, List<CodDocument>>> LookupAsync(
        string template, IReadOnlyCollection<string> keys, CancellationToken cancellationToken,
        IReadOnlyList<int>? ordinals = null)
    {
        var found = new Dictionary<string, List<CodDocument>>(StringComparer.OrdinalIgnoreCase);
        if (keys.Count == 0) return found;

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        foreach (var batch in keys.Distinct(StringComparer.OrdinalIgnoreCase).Chunk(BatchSize))
        {
            var parameters = batch.Select((_, i) => $"@k{i}").ToArray();

            var narrowing = ordinals is null
                ? string.Empty
                : "AND n.TrN_TrNNumer IN (" + string.Join(", ", ordinals.Distinct()) + ")";

            var sql = template.Contains("{1}")
                ? string.Format(template, string.Join(", ", parameters), narrowing)
                : string.Format(template, string.Join(", ", parameters));

            await using var command = new SqlCommand(sql, connection) { CommandTimeout = 120 };
            command.Parameters.AddWithValue("@contractorType", ContractorGidType);

            for (var i = 0; i < batch.Length; i++) command.Parameters.AddWithValue(parameters[i], batch[i]);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);

            while (await reader.ReadAsync(cancellationToken))
            {
                var key = reader.GetString(0);

                if (!found.TryGetValue(key, out var documents))
                {
                    documents = [];
                    found[key] = documents;
                }

                documents.Add(new CodDocument(
                    reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3),
                    (reader.IsDBNull(4) ? string.Empty : reader.GetString(4)).Trim(),
                    reader.GetInt32(5), reader.GetDecimal(6), reader.GetDecimal(7), reader.GetInt32(8)));
            }
        }

        return found;
    }
}
