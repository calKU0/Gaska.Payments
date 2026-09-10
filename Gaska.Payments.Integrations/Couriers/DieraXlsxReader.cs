using ExcelDataReader;

using Gaska.Payments.Domain.Couriers;

namespace Gaska.Payments.Integrations.Couriers;

/// <summary>
/// The Diera report: one sheet, one header row, one line per parcel collected.
/// </summary>
/// <remarks>
/// Like Hellmann's, this is a running list rather than a payout breakdown - it is filled in as
/// parcels are collected, states no total, no payment reference and no account, and the same rows
/// come back in the next file. Unlike Hellmann's, Diera does not pay parcel by parcel: one transfer
/// covers several of them and names them in its title, as "ZWROT POBRAN 2602153784,2602156427".
///
/// The columns are:
///
/// <code>
/// Nr paczki/listu/zlecenia | Data pobrania | Kwota COD | Nr referencyjny |
/// Data przelewu | Nazwa odbiorcy | Adres odbiorcy
/// </code>
///
/// "Nr referencyjny" is our own invoice number, which is what the parcel is settled against. The
/// parcel number is also a real waybill - it is <c>CDN.Wysylki.WYS_NumerObcy</c> with a "p01"
/// suffix - but the file states the document outright, so the document is followed directly and
/// shipping is left out of it, exactly as for Hellmann.
///
/// "Data przelewu" is meant to be filled in once Diera knows when it pays. Nothing here reads it:
/// which parcels a transfer covers is decided by the transfer itself, and a column that is empty
/// today would be one more thing to be wrong tomorrow.
/// </remarks>
public sealed class DieraXlsxReader : ICodReportReader
{
    public const string FormatName = "DieraXlsx";

    private const int Parcel = 0;
    private const int Money = 2;
    private const int Document = 3;
    private const int Recipient = 5;

    /// <summary>The first bytes of a zip archive, which is what an .xlsx workbook is.</summary>
    private static readonly byte[] ZipArchive = [0x50, 0x4B, 0x03, 0x04];

    public string Format => FormatName;

    public bool Recognises(string fileName, byte[] content)
    {
        if (content.Length < ZipArchive.Length) return false;
        if (!content.Take(ZipArchive.Length).SequenceEqual(ZipArchive)) return false;

        try
        {
            var header = Rows(content).FirstOrDefault() ?? [];

            return header.Any(c => c.Contains("Nr paczki", StringComparison.OrdinalIgnoreCase))
                && header.Any(c => c.Contains("Kwota COD", StringComparison.OrdinalIgnoreCase))
                && header.Any(c => c.Contains("Nr referencyjny", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception)
        {
            // A workbook we cannot open is not ours to read. Whoever it belongs to will say so.
            return false;
        }
    }

    public CodReport Read(byte[] content)
    {
        var parcels = new List<CodParcel>();

        foreach (var row in Rows(content).Skip(1))
        {
            var parcel = CodText.Clean(Cell(row, Parcel));
            var amount = CodText.Amount(Cell(row, Money));

            // The sheet comes with its empty rows already formatted, so most of what follows the
            // last parcel is blank cells rather than the end of the file.
            if (parcel.Length == 0 || amount == 0m) continue;

            // Typed by hand and so of any capitalisation, like Hellmann's.
            var document = CodText.Clean(Cell(row, Document)).ToUpperInvariant();

            parcels.Add(new CodParcel(
                parcel,
                amount,
                document.Length == 0 ? [] : [document],
                CodText.Clean(Cell(row, Recipient))));
        }

        return new CodReport(
            FormatName,
            // Today, because the file has no payout day to give. Which parcels are old enough to
            // leave alone is decided per transfer instead, where the money actually is.
            DateTime.Today,
            parcels.Sum(p => p.Amount),
            // No payment reference and no account: Diera states neither.
            string.Empty,
            string.Empty,
            parcels)
        {
            Shape = CodPayoutShape.NamedGroups,
        };
    }

    private static string Cell(string[] row, int index) => index < row.Length ? row[index] : string.Empty;

    /// <summary>The sheet as text, cell by cell, dates in the ISO form the readers speak.</summary>
    private static IEnumerable<string[]> Rows(byte[] content)
    {
        using var stream = new MemoryStream(content, writable: false);
        using var reader = ExcelReaderFactory.CreateReader(stream);

        while (reader.Read())
        {
            var row = new string[reader.FieldCount];

            for (var i = 0; i < reader.FieldCount; i++)
            {
                row[i] = reader.GetValue(i) switch
                {
                    null => string.Empty,
                    DateTime date => date.ToString("yyyy-MM-dd"),
                    double number => number.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture),
                    var value => value.ToString() ?? string.Empty,
                };
            }

            yield return row;
        }
    }
}
