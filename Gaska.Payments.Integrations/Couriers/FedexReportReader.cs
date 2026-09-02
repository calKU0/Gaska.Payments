using System.Net;
using System.Text.RegularExpressions;
using System.Text;
using MimeKit;

using Gaska.Payments.Domain.Couriers;

namespace Gaska.Payments.Integrations.Couriers;

/// <summary>
/// The FedEx report: an HTML table wrapped in a MIME envelope, named <c>.xls</c> and opened by
/// Excel as a web page.
/// </summary>
/// <remarks>
/// It is what Oracle BI Publisher calls a web archive, so it is neither a workbook nor plain HTML:
/// a MIME envelope with one quoted-printable HTML part inside.
///
/// FedEx sends one file per payout and puts several of them in the mailbox on the same morning,
/// each with its own total under "Razem" and its own bank account. The date at the head of every
/// row - "Data przekazania do banku" - is the day the money reaches us.
/// </remarks>
public sealed partial class FedexReportReader : ICodReportReader
{
    public const string FormatName = "FedexReport";

    private const int PayoutDay = 0;
    private const int Waybill = 1;
    private const int Content = 3;
    private const int Notes = 4;
    private const int Recipient = 6;
    private const int Money = 7;

    public string Format => FormatName;

    public bool Recognises(string fileName, byte[] content)
    {
        var text = Text(content);

        return text.Contains("Nr listu", StringComparison.OrdinalIgnoreCase)
            && text.Contains("Data przekazania do banku", StringComparison.OrdinalIgnoreCase);
    }

    public CodReport Read(byte[] content)
    {
        var rows = Rows(Text(content));

        var parcels = new List<CodParcel>();
        var payoutDate = default(DateTime?);
        var total = 0m;
        var reference = string.Empty;
        var account = string.Empty;

        foreach (var row in rows)
        {
            // The head of the file is a little table of its own - account number, payment
            // reference, company - laid out with a leading empty cell, so the label is looked for
            // rather than expected in a particular column. Only the reference is of use to us.
            if (Array.FindIndex(row, c => c.StartsWith("Ref.", StringComparison.OrdinalIgnoreCase)) is var label
                && label >= 0)
            {
                reference = After(row, label);
                continue;
            }

            // The account the payout goes to. Under the dropshipping service it is the customer's
            // own, not ours, and the pipeline drops such a report - the money never reaches us.
            if (Array.FindIndex(row, c => c.StartsWith("Nr konta", StringComparison.OrdinalIgnoreCase))
                is var accountLabel && accountLabel >= 0)
            {
                account = After(row, accountLabel);
                continue;
            }

            // "Razem" closes the file and "Suma dnia" closes a day inside it. Both sit in the
            // second-to-last cell with the amount beside them, and neither is a parcel.
            if (row.Length >= 2 && row[^2].Equals("Razem", StringComparison.OrdinalIgnoreCase))
            {
                total = CodText.Amount(row[^1]);
                continue;
            }

            if (row.Length <= Money) continue;

            var waybill = row[Waybill];
            var amount = CodText.Amount(row[Money]);

            if (waybill.Length == 0 || amount == 0m || !waybill.All(char.IsAsciiDigit)) continue;

            payoutDate ??= CodText.Date(row[PayoutDay]);

            // The document is in "Opis zawartości"; "Uwagi" repeats it next to the parcel id and
            // serves as the fallback when the description column has been left empty.
            var documents = CodText.Documents(row[Content]);
            if (documents.Count == 0) documents = CodText.Documents(row[Notes]);

            parcels.Add(new CodParcel(waybill, amount, documents, row[Recipient]));
        }

        return new CodReport(
            FormatName, payoutDate ?? DateTime.Today, total, reference, account, parcels);
    }

    /// <summary>
    /// The first non-empty cell after a label. The head of the file is laid out with empty cells
    /// between the label and its value, and not always the same number of them.
    /// </summary>
    private static string After(string[] row, int label) =>
        row.Skip(label + 1).FirstOrDefault(c => c.Length > 0) ?? string.Empty;

    /// <summary>The table rows, each as its cells' text with the markup taken out.</summary>
    private static List<string[]> Rows(string html)
    {
        var rows = new List<string[]>();

        foreach (Match row in RowPattern().Matches(html))
        {
            var cells = CellPattern().Matches(row.Groups[1].Value)
                .Select(c => CodText.Clean(WebUtility.HtmlDecode(TagPattern().Replace(c.Groups[1].Value, " "))))
                .ToArray();

            if (cells.Any(c => c.Length > 0)) rows.Add(cells);
        }

        return rows;
    }

    /// <summary>
    /// The markup, taken out of the MIME envelope and with the stylesheet dropped.
    /// </summary>
    /// <remarks>
    /// The envelope is opened by MimeKit rather than by hand: the body is quoted-printable UTF-8,
    /// so an equals sign arrives as "=3D" and a soft line break lands every 76 characters. Cutting
    /// that apart with string operations gets Polish names wrong in ways nobody notices until an
    /// entry carries them.
    ///
    /// The style block is dropped afterwards because its selectors are full of braces and colons
    /// that would otherwise turn up as table text.
    /// </remarks>
    private static string Text(byte[] content) => StylePattern().Replace(Html(content), string.Empty);

    /// <summary>
    /// The HTML part of the archive - or the whole file read as text when it is not an envelope at
    /// all, so that a plain HTML export would still be read.
    /// </summary>
    private static string Html(byte[] content)
    {
        try
        {
            using var stream = new MemoryStream(content, writable: false);
            var message = MimeMessage.Load(stream);

            if (message.BodyParts.OfType<TextPart>().FirstOrDefault(p => p.IsHtml) is { } html)
            {
                return html.Text;
            }
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException)
        {
            // Not a MIME envelope. Fall through and read it as it stands.
        }

        return Encoding.UTF8.GetString(content);
    }

    [GeneratedRegex(@"<tr[^>]*>(.*?)</tr>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex RowPattern();

    [GeneratedRegex(@"<t[dh][^>]*>(.*?)</t[dh]>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex CellPattern();

    [GeneratedRegex(@"<[^>]+>", RegexOptions.Singleline)]
    private static partial Regex TagPattern();

    [GeneratedRegex(@"<style.*?</style>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex StylePattern();
}
