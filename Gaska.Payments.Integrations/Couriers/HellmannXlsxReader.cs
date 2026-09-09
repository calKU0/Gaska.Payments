using ExcelDataReader;

using Gaska.Payments.Domain.Couriers;

namespace Gaska.Payments.Integrations.Couriers;

/// <summary>
/// The Hellmann report: one sheet, one header row, one line per parcel collected.
/// </summary>
/// <remarks>
/// Unlike the others this is not a payout breakdown but a running list of parcels, and Hellmann
/// pays each of them with a transfer of its own - the title of which is the order number from the
/// first column. So the file states no payout total, no payment reference and no account, and the
/// same rows come back in the next file: what has already been booked is what keeps them from
/// being booked twice.
///
/// The columns are:
///
/// <code>
/// Order NR | Order Customer NR | Data Utworzenia Zlecenia | Data Dostawy |
/// Wartość Pobrania | Odbiorca Przesyłki | Miasto | Ulica
/// </code>
///
/// There is no waybill number anywhere in it. The order number takes that place - it identifies
/// the parcel, it is what the transfer's title carries, and it is what stops a row being booked a
/// second time. The document is read from "Order Customer NR", which is our own invoice number
/// written by hand and so of any capitalisation: FS-21214/26/SPR, fs-21764/26/spr, fs-38371/26/s.
/// </remarks>
public sealed class HellmannXlsxReader : ICodReportReader
{
    public const string FormatName = "HellmannXlsx";

    private const int Order = 0;
    private const int Document = 1;
    private const int Money = 4;
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

            return header.Any(c => c.Contains("Order NR", StringComparison.OrdinalIgnoreCase))
                && header.Any(c => c.Contains("Order Customer NR", StringComparison.OrdinalIgnoreCase))
                && header.Any(c => c.Contains("Pobrania", StringComparison.OrdinalIgnoreCase));
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
            var order = CodText.Clean(Cell(row, Order));
            var amount = CodText.Amount(Cell(row, Money));

            if (order.Length == 0 || amount == 0m) continue;

            // The number is written by hand and comes in any capitalisation, so it is squared up
            // here rather than at every place that compares it with ERP.
            var document = CodText.Clean(Cell(row, Document)).ToUpperInvariant();

            parcels.Add(new CodParcel(
                order,
                amount,
                document.Length == 0 ? [] : [document],
                CodText.Clean(Cell(row, Recipient))));
        }

        return new CodReport(
            FormatName,
            // Today, because the file has no payout day to give: it is a running list of parcels,
            // and each of them is paid whenever Hellmann gets round to it. Dating the report by
            // the last delivery in it - the obvious thing - made the whole file look older than
            // the takeover date and it was written off unread. Which parcels are old enough to
            // leave alone is decided per transfer instead, where the money actually is.
            DateTime.Today,
            parcels.Sum(p => p.Amount),
            // No payment reference and no account: Hellmann states neither.
            string.Empty,
            string.Empty,
            parcels)
        {
            PaysPerParcel = true,
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
