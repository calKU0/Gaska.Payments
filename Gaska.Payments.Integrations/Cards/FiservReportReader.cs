using System.Globalization;
using System.Text;
using System.Xml.Linq;

using Gaska.Payments.Domain.Cards;

namespace Gaska.Payments.Integrations.Cards;

/// <summary>
/// Fiserv's (Polcard's) transaction report: an Excel 2003 workbook saved as XML.
/// </summary>
/// <remarks>
/// Two sheets. "Objaśnienia" names the merchant and the period; "Transakcja" lists the
/// transactions, one per row, under a header row. The columns are found by their headings rather
/// than their positions - there are twenty-eight of them, most of them empty, and a report that
/// grew a column would otherwise be read a column askew.
///
/// The file declares ISO-8859-2, which .NET reads only once the code page provider is registered.
/// </remarks>
public sealed class FiservReportReader
{
    public const string FormatName = "FiservXml";

    private static readonly XNamespace Ss = "urn:schemas-microsoft-com:office:spreadsheet";

    static FiservReportReader() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public bool Recognises(string fileName, byte[] content)
    {
        try
        {
            var sheet = Transactions(Load(content));
            if (sheet is null) return false;

            var header = Header(Rows(sheet).FirstOrDefault() ?? []);
            return header.ContainsKey("numer transakcji") && header.ContainsKey("numer zbiorówki");
        }
        catch (Exception)
        {
            // Not XML, or XML of some other kind. Whoever it belongs to will say so.
            return false;
        }
    }

    public CardReport Read(byte[] content)
    {
        var document = Load(content);
        var sheet = Transactions(document)
                    ?? throw new InvalidDataException("The workbook has no \"Transakcja\" sheet.");

        var rows = Rows(sheet).ToList();
        var header = Header(rows.FirstOrDefault() ?? []);

        string Cell(string[] row, string column) =>
            header.TryGetValue(column, out var index) && index < row.Length ? row[index].Trim() : string.Empty;

        var transactions = new List<CardTransaction>();

        foreach (var row in rows.Skip(1))
        {
            // Only transaction records; the sheet describes "TR" as the one kind it holds, but a
            // summary line would be something else and must not be read as money.
            if (!Cell(row, "typ rekordu").Equals("TR", StringComparison.OrdinalIgnoreCase)) continue;

            var number = Cell(row, "numer transakcji");
            if (number.Length == 0) continue;

            transactions.Add(new CardTransaction(
                Cell(row, "numer punktu"),
                Cell(row, "numer terminala"),
                number,
                Cell(row, "numer zbiorówki"),
                Date(Cell(row, "data transakcji")),
                Time(Cell(row, "czas transakcji")),
                Cell(row, "typ transakcji"),
                Amount(Cell(row, "kwota transakcji")),
                Cell(row, "numer karty"),
                Cell(row, "system karty")));
        }

        var about = About(document);

        return new CardReport(
            about.GetValueOrDefault("numer kontrahenta:", string.Empty),
            ParseDateOr(about.GetValueOrDefault("początek okresu:"), transactions.Select(t => t.Date).DefaultIfEmpty().Min()),
            ParseDateOr(about.GetValueOrDefault("koniec okresu:"), transactions.Select(t => t.Date).DefaultIfEmpty().Max()),
            transactions);
    }

    private static XDocument Load(byte[] content)
    {
        using var stream = new MemoryStream(content, writable: false);
        return XDocument.Load(stream);
    }

    private static XElement? Transactions(XDocument document) =>
        document.Root?.Elements(Ss + "Worksheet")
            .FirstOrDefault(w => string.Equals((string?)w.Attribute(Ss + "Name"), "Transakcja", StringComparison.OrdinalIgnoreCase));

    /// <summary>The sheet as rows of text, with the gaps a cell's <c>ss:Index</c> leaves filled in.</summary>
    private static IEnumerable<string[]> Rows(XElement sheet)
    {
        foreach (var row in sheet.Descendants(Ss + "Row"))
        {
            var cells = new List<string>();

            foreach (var cell in row.Elements(Ss + "Cell"))
            {
                // ss:Index is 1-based and skips the empty cells before it.
                if (int.TryParse((string?)cell.Attribute(Ss + "Index"), out var index))
                {
                    while (cells.Count < index - 1) cells.Add(string.Empty);
                }

                cells.Add(cell.Element(Ss + "Data")?.Value ?? string.Empty);
            }

            yield return [.. cells];
        }
    }

    private static Dictionary<string, int> Header(string[] row)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < row.Length; i++)
        {
            var name = row[i].Trim().ToLowerInvariant();
            if (name.Length > 0) map.TryAdd(name, i);
        }

        return map;
    }

    /// <summary>The label-and-value pairs of the "Objaśnienia" sheet.</summary>
    private static Dictionary<string, string> About(XDocument document)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var sheet = document.Root?.Elements(Ss + "Worksheet")
            .FirstOrDefault(w => ((string?)w.Attribute(Ss + "Name"))?.StartsWith("Obja", StringComparison.OrdinalIgnoreCase) == true);

        if (sheet is null) return values;

        foreach (var row in Rows(sheet))
        {
            var filled = row.Select(c => c.Trim()).Where(c => c.Length > 0).ToList();
            if (filled.Count >= 2 && filled[0].EndsWith(':')) values.TryAdd(filled[0].ToLowerInvariant(), filled[1]);
        }

        return values;
    }

    private static DateTime Date(string value) =>
        DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date.Date
            : throw new InvalidDataException($"Unreadable transaction date \"{value}\".");

    /// <summary>A time of day, which the workbook stores as a moment on 1899-12-31.</summary>
    private static TimeSpan Time(string value) =>
        DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var moment)
            ? moment.TimeOfDay
            : TimeSpan.Zero;

    private static decimal Amount(string value) =>
        decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var amount)
            ? amount
            : throw new InvalidDataException($"Unreadable transaction amount \"{value}\".");

    private static DateTime ParseDateOr(string? value, DateTime fallback) =>
        DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date.Date : fallback;
}
