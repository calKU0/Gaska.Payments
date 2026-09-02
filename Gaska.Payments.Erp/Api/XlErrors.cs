using System.Text.RegularExpressions;
using cdn_api;

namespace Gaska.Payments.Erp;

/// <summary>
/// Translates XL API error numbers into the messages ERP shows to a human.
/// </summary>
/// <remarks>
/// <c>XLOpisBledu</c> does the work, given a function number and an error number. The function
/// numbers appear neither in the documentation nor in the library - we read them out of the API
/// itself by asking it about every number in turn: the message returned ends with the function
/// name in brackets, for example <c>(NowyRachunek-9)</c>. The table holds 257 entries and wraps
/// around, so numbers above 256 point at the same functions.
///
/// The cash and settlement functions (<c>XLDodajRaport</c>, <c>XLDodajZapis</c>,
/// <c>XLRozliczaj</c>, <c>XLKasujRozliczenie</c>) are absent from that table - <c>XLRozliczaj</c>
/// has a <c>BladOpis</c> field of its own instead, and that is where its messages come from.
/// </remarks>
public static class XlErrors
{
    /// <summary>Function number of <c>XLNowyRachunek</c> in the message table.</summary>
    public const int NowyRachunek = 169;

    /// <summary>
    /// An error description ready to show an accountant. When the API has nothing sensible to
    /// say, the function name and the number are all that is left - still better than nothing.
    /// </summary>
    public static string Describe(XlSession session, int function, int code, string functionName)
    {
        var explanation = Lookup(session.Version, function, code);

        return explanation is null
            ? $"{functionName} zwrócił {code}."
            : $"{explanation} ({functionName}, błąd {code})";
    }

    private static string? Lookup(int version, int function, int code)
    {
        var info = new XLKomunikatInfo_20251
        {
            Wersja = version,
            Funkcja = function,
            Blad = code,
            Ostrzezenie = 0,
            Tryb = 0,
            // The buffer is fixed length and it is ours to supply - otherwise there is
            // nowhere to write.
            OpisBledu = new string(' ', 2000),
        };

        if (cdn_api.cdn_api.XLOpisBledu(info) != 0) return null;

        return Clean(info.OpisBledu ?? string.Empty);
    }

    /// <summary>
    /// Picks the message out of what the API returns: a "BLEDY:" header, pipes separating the
    /// lines, and the function name with the error number stuck on the end.
    /// </summary>
    private static string? Clean(string raw)
    {
        // The API appends the function name with the error number, sometimes several at once:
        // "(KSeF-9) (KSeFPobierzUPO-9)".
        var text = Regex.Replace(raw.Trim(), @"(\s*\([A-Za-z_][A-Za-z0-9_]*--?\d+\))+$", string.Empty);

        var lines = text
            .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !line.Equals("BŁĘDY:", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var message = string.Join(" ", lines).Trim();

        // This is how the API says "I do not know". Repeating it helps nobody; the bare number
        // is more use.
        return message.Length == 0 || message.Contains("niezidentyfikowany błąd", StringComparison.OrdinalIgnoreCase)
            ? null
            : message;
    }
}
