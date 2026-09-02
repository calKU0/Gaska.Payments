using System.Text;

using Gaska.Payments.Domain.Couriers;

namespace Gaska.Payments.Integrations.Couriers;

/// <summary>
/// The GLS report: a semicolon-separated UTF-8 text file, whatever its extension says.
/// </summary>
/// <remarks>
/// Its shape is a payout statement seen from the courier's side: the second line is the transfer
/// GLS makes to us, and every line below it is a parcel stated as a negative - money leaving GLS.
/// The parcels add up to the transfer exactly.
///
/// Alone among the three it names the document outright, in a column called "Ref.", so nothing has
/// to be dug out of a free-text field.
/// </remarks>
public sealed class GlsCsvReader : ICodReportReader
{
    public const string FormatName = "GlsCsv";

    private const int Waybill = 0;
    private const int PayoutDay = 2;
    private const int Money = 3;
    private const int Reference = 5;
    private const int Recipient = 7;

    public string Format => FormatName;

    public bool Recognises(string fileName, byte[] content) =>
        FirstLine(content).StartsWith("Nr paczki;", StringComparison.OrdinalIgnoreCase);

    public CodReport Read(byte[] content)
    {
        var lines = Text(content)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.TrimEnd('\r').Split(';'))
            .ToList();

        // Line 0 is the header and line 1 the payout: no parcel, just the date and the total.
        var summary = lines.Count > 1 ? lines[1] : [];
        var payoutDate = CodText.Date(Cell(summary, PayoutDay)) ?? DateTime.Today;
        var payoutTotal = CodText.Amount(Cell(summary, Money));
        var reference = CodText.Clean(Cell(summary, 1));

        var parcels = new List<CodParcel>();

        foreach (var row in lines.Skip(2))
        {
            // The parcel number carries a leading asterisk that is GLS's own marking and is not
            // part of the number ERP holds.
            var waybill = CodText.Clean(Cell(row, Waybill)).TrimStart('*');
            var amount = CodText.Amount(Cell(row, Money));

            if (waybill.Length == 0 || amount == 0m) continue;

            parcels.Add(new CodParcel(
                waybill, amount, CodText.Documents(Cell(row, Reference)), CodText.Clean(Cell(row, Recipient))));
        }

        // GLS names no account in the file - there is nothing to tell apart.
        return new CodReport(FormatName, payoutDate, payoutTotal, reference, string.Empty, parcels);
    }

    private static string Cell(string[] row, int index) => index < row.Length ? row[index] : string.Empty;

    private static string FirstLine(byte[] content)
    {
        var line = Text(content);
        var end = line.IndexOf('\n');
        return end < 0 ? line : line[..end];
    }

    /// <summary>
    /// UTF-8, with the byte order mark thrown away if there is one.
    /// </summary>
    /// <remarks>
    /// A misdecoded file would not fail here - it would quietly produce recipient names with
    /// question marks in them - so the encoding is stated rather than guessed. The sample GLS sent
    /// is UTF-8; should that ever change, it will show up as a report nothing recognises, which is
    /// reported, rather than as an entry with a mangled name.
    /// </remarks>
    private static string Text(byte[] content) => new UTF8Encoding(false).GetString(content).TrimStart('\uFEFF');
}
