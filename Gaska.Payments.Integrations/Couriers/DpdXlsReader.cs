using System.Text;
using ExcelDataReader;

using Gaska.Payments.Domain.Couriers;

namespace Gaska.Payments.Integrations.Couriers;

/// <summary>
/// The DPD report: a real Excel workbook in the old BIFF format, one sheet, one header row.
/// </summary>
/// <remarks>
/// Every row repeats the same collective transfer and its date, because the file is one payout
/// - so those are read from the first row and the rest of the column is ignored.
///
/// The document number hides in the column DPD calls "Pole 'zawartość' w programie Unisoft-K",
/// alongside the parcel id and, when two invoices travelled in one parcel, both of their numbers.
/// </remarks>
public sealed class DpdXlsReader : ICodReportReader
{
    public const string FormatName = "DpdXls";

    private const int Waybill = 2;
    private const int Money = 4;
    private const int Recipient = 6;
    private const int Content = 10;
    private const int PayoutTotal = 11;
    private const int PayoutDay = 12;
    private const int Reference = 13;

    /// <summary>The magic of an OLE compound file, which is what a BIFF workbook is wrapped in.</summary>
    private static readonly byte[] CompoundFile = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];

    static DpdXlsReader() =>
        // BIFF stores text in a code page, and .NET Core ships only the Unicode ones. Without this
        // every Polish name in the file comes out as question marks.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public string Format => FormatName;

    public bool Recognises(string fileName, byte[] content)
    {
        if (content.Length < CompoundFile.Length) return false;
        if (!content.Take(CompoundFile.Length).SequenceEqual(CompoundFile)) return false;

        // Being a workbook is not enough - the header has to be DPD's.
        try
        {
            var header = Rows(content).FirstOrDefault() ?? [];
            return header.Any(c => c.Contains("Kwota pobrania", StringComparison.OrdinalIgnoreCase))
                && header.Any(c => c.Contains("Nr listu przewozowego", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception)
        {
            // A workbook we cannot open is not ours to read. Whoever it belongs to will say so.
            return false;
        }
    }

    public CodReport Read(byte[] content)
    {
        var rows = Rows(content).Skip(1).ToList();
        var first = rows.FirstOrDefault() ?? [];

        var parcels = new List<CodParcel>();

        foreach (var row in rows)
        {
            var waybill = CodText.Clean(Cell(row, Waybill));
            var amount = CodText.Amount(Cell(row, Money));

            if (waybill.Length == 0 || amount == 0m) continue;

            parcels.Add(new CodParcel(
                waybill, amount, CodText.Documents(Cell(row, Content)), CodText.Clean(Cell(row, Recipient))));
        }

        return new CodReport(
            FormatName,
            CodText.Date(Cell(first, PayoutDay)) ?? DateTime.Today,
            CodText.Amount(Cell(first, PayoutTotal)),
            CodText.Clean(Cell(first, Reference)),
            // DPD names no account in the file - there is nothing to tell apart.
            string.Empty,
            parcels);
    }

    private static string Cell(string[] row, int index) => index < row.Length ? row[index] : string.Empty;

    /// <summary>
    /// The sheet as text, cell by cell.
    /// </summary>
    /// <remarks>
    /// Everything is turned into strings on the way out, dates included: Excel keeps a date as a
    /// serial number and the column holding the payout date is no different, so it is converted
    /// here into the ISO form the rest of the readers speak.
    /// </remarks>
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
                    double number => Number(reader, i, number),
                    var value => value.ToString() ?? string.Empty,
                };
            }

            yield return row;
        }
    }

    /// <summary>
    /// A numeric cell as text - as a date when Excel's formatting says it is one.
    /// </summary>
    /// <remarks>
    /// DPD leaves its date columns as plain serial numbers with a date format applied, so nothing
    /// but the format tells 46268 from a quantity.
    /// </remarks>
    private static string Number(IExcelDataReader reader, int index, double value)
    {
        try
        {
            if (reader.GetNumberFormatString(index)?.Contains('y', StringComparison.OrdinalIgnoreCase) == true)
            {
                return DateTime.FromOADate(value).ToString("yyyy-MM-dd");
            }
        }
        catch (ArgumentException)
        {
            // Out of the range of a date - then it was a number after all.
        }

        return value.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}
