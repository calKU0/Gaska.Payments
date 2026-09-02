using System.Globalization;
using System.Text.RegularExpressions;
using System.Text;

using Gaska.Payments.Domain.Couriers;

namespace Gaska.Payments.Integrations.Couriers;

/// <summary>Reads one courier's payout report out of the bytes of an e-mail attachment.</summary>
/// <remarks>
/// Each courier has a format of its own and none of them is negotiable, so there is a reader per
/// courier rather than one configurable parser. What they share - recognising themselves, cleaning
/// up the text, reading amounts - is in <see cref="CodText"/>.
/// </remarks>
public interface ICodReportReader
{
    /// <summary>The format's name, matched against <see cref="CourierOptions.Format"/>.</summary>
    string Format { get; }

    /// <summary>Whether this reader recognises the attachment as its own.</summary>
    bool Recognises(string fileName, byte[] content);

    /// <summary>Reads the report. Only called after <see cref="Recognises"/> said yes.</summary>
    CodReport Read(byte[] content);
}

/// <summary>Text and number handling shared by the readers.</summary>
public static class CodText
{
    /// <summary>
    /// A document number as ERP writes it: FS-40893/26/SPR, WZ-31925/26/S, (S)FS-27132/26/SPR.
    /// </summary>
    /// <remarks>
    /// The couriers put the number in a free-text field alongside a parcel id and sometimes a
    /// second document, so it is picked out by shape rather than by position. The leading
    /// bracketed part is what ERP prints for documents of a subordinate company and it belongs to
    /// the number.
    /// </remarks>
    private static readonly Regex DocumentNumber = new(
        @"(\([A-Z]{1,3}\))?[A-Z]{2,4}-\d+/\d+/[A-Z]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Strips the bidirectional marks the reports are littered with.
    /// </summary>
    /// <remarks>
    /// FedEx wraps every number in U+202D/U+202C so that Arabic renders it left to right. Left in,
    /// they end up inside a waybill number and nothing matches it in ERP.
    /// </remarks>
    public static string Clean(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;

        var text = new StringBuilder(value.Length);

        foreach (var ch in value)
        {
            // U+200E/U+200F and the U+202A..U+202E embedding marks. Written as escapes on purpose:
            // as literals they are invisible in the editor and the next person would delete them.
            if (ch is '\u200E' or '\u200F' or (>= '\u202A' and <= '\u202E')) continue;

            text.Append(ch == '\u00A0' ? ' ' : ch);
        }

        return text.ToString().Trim();
    }

    /// <summary>
    /// An amount, whatever separator the courier used and whatever sign.
    /// </summary>
    /// <remarks>
    /// The sign is dropped deliberately. GLS states the parcels as negatives against a positive
    /// payout - a payout breakdown, in its own bookkeeping's direction - while DPD and FedEx state
    /// the same parcels as positives. To us every one of them is money received.
    /// </remarks>
    public static decimal Amount(string? value)
    {
        var text = Clean(value).Replace(" ", string.Empty).Replace(',', '.');
        if (text.Length == 0) return 0m;

        return decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var amount)
            ? Math.Abs(amount)
            : 0m;
    }

    /// <summary>Every distinct document number in the text, in the order it appears.</summary>
    public static IReadOnlyList<string> Documents(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return [];

        var found = new List<string>();

        foreach (Match match in DocumentNumber.Matches(Clean(value)))
        {
            if (!found.Contains(match.Value, StringComparer.OrdinalIgnoreCase)) found.Add(match.Value);
        }

        return found;
    }

    /// <summary>A date in the ISO form the reports use, or null when the field is empty or odd.</summary>
    public static DateTime? Date(string? value)
    {
        var text = Clean(value);

        return DateTime.TryParseExact(text, ["yyyy-MM-dd", "dd.MM.yyyy", "dd-MM-yyyy"],
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;
    }
}
