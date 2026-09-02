using System.Text;

namespace Gaska.Payments.Domain.Parsing;

/// <summary>
/// Reduces a payment title to a shape regular expressions can work on.
/// </summary>
/// <remarks>
/// The practical problem that matters most: the bank assembles the description from 35-character
/// lines (MT940 :86: / camt.053 Ustrd) and puts a space at every line boundary. The database then
/// shows "(S)FS-23 069/26/SPR" or "26/S PR" - the number cut at a random place. That is why the
/// working form is the text WITHOUT spaces: gluing it back repairs the cuts and does not damage
/// comma-separated lists, because the commas stay.
/// </remarks>
public static class TextNormalizer
{
    private static readonly (char From, string To)[] PolishMap =
    [
        ('Ą', "A"), ('Ć', "C"), ('Ę', "E"), ('Ł', "L"), ('Ń', "N"),
        ('Ó', "O"), ('Ś', "S"), ('Ź', "Z"), ('Ż', "Z"),
        ('ą', "A"), ('ć', "C"), ('ę', "E"), ('ł', "L"), ('ń', "N"),
        ('ó', "O"), ('ś', "S"), ('ź', "Z"), ('ż', "Z"),
    ];

    /// <summary>Upper case, Polish diacritics folded away, runs of spaces collapsed to one.</summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        var sb = new StringBuilder(text.Length);
        var lastWasSpace = false;

        foreach (var ch in text)
        {
            var mapped = MapChar(ch);

            if (mapped == " ")
            {
                if (!lastWasSpace && sb.Length > 0) sb.Append(' ');
                lastWasSpace = true;
                continue;
            }

            sb.Append(mapped);
            lastWasSpace = false;
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// The working form used to hunt for numbers: like <see cref="Normalize"/> but with no
    /// spaces at all. It glues back numbers cut apart by the 35-character line wrapping.
    /// </summary>
    public static string NormalizeCompact(string? text)
    {
        var normalized = Normalize(text);
        return normalized.Replace(" ", string.Empty);
    }

    /// <summary>Keeps digits only - for comparing tax ids and account numbers.</summary>
    public static string DigitsOnly(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (char.IsAsciiDigit(ch)) sb.Append(ch);
        }
        return sb.ToString();
    }

    /// <summary>
    /// An account number stripped of decoration: alphanumeric characters only, upper case.
    /// The country code stays - this is the form the bank understands.
    /// </summary>
    public static string CompactAccount(string? account)
    {
        if (string.IsNullOrWhiteSpace(account)) return string.Empty;

        var sb = new StringBuilder(account.Length);
        foreach (var ch in account)
        {
            if (char.IsAsciiLetterOrDigit(ch)) sb.Append(char.ToUpperInvariant(ch));
        }

        return sb.ToString();
    }

    /// <summary>
    /// An account number reduced to the form ERP keeps it in: alphanumeric characters only,
    /// upper case, without the country code.
    /// </summary>
    public static string NormalizeAccount(string? account)
    {
        var value = CompactAccount(account);

        // Comarch ERP XL stores the number without a country code whenever it considers it an
        // IBAN - and it does so for domestic and foreign accounts alike (close to 24 thousand out
        // of 24 thousand in the register). The bank sends them with the country code, so without
        // this trim the two sides would never meet.
        //
        // The prefix comes off only when the number looks like an IBAN: two letters followed
        // immediately by two check digits. Numbers in national formats that also start with
        // letters (Chinese ones, "OSA...") do not fit that pattern and are left alone.
        return LooksLikeIban(value) ? value[2..] : value;
    }

    /// <summary>
    /// Legal forms, trade abbreviations and filler words that do not tell one company from
    /// another. "FIRMA HANDLOWO-USLUGOWA JAN KOWALSKI" carries information in the surname only.
    /// </summary>
    private static readonly HashSet<string> CompanyNoiseTokens = new(StringComparer.Ordinal)
    {
        // Polish legal forms
        "SP", "ZOO", "SPZOO", "SPOLKA", "SPOLKI", "AKCYJNA", "JAWNA", "CYWILNA", "KOMANDYTOWA",
        "OGRANICZONA", "OGRANICZONO", "ODPOWIEDZIALNOSCIA", "SA", "SC", "SJ", "SK", "SKA",
        // foreign legal forms
        "LTD", "LIMITED", "GMBH", "AG", "KFT", "BT", "ZRT", "NYRT", "SIA", "UAB", "AS", "OU",
        "SRL", "SRO", "SPA", "SNC", "SAS", "SARL", "BV", "NV", "OY", "AB", "APS", "DOO", "OOO",
        "INC", "LLC", "PLC", "EOOD", "OOD", "AD", "ZAO",
        // lines of business
        "PPHU", "PHU", "FHU", "PPH", "PUH", "ZPHU", "FH", "PH", "PW", "FIRMA", "HANDLOWA",
        "HANDLOWO", "USLUGOWA", "USLUGOWO", "PRODUKCYJNA", "PRODUKCYJNO", "USLUGI", "HANDEL",
        "PRZEDSIEBIORSTWO", "ZAKLAD", "ZAKLADY", "GOSPODARSTWO", "ROLNE", "ROLNY", "GR",
        "CENTRUM", "GRUPA", "COMPANY", "CO",
    };

    /// <summary>
    /// Splits a company name into the parts it can be recognised by: no diacritics, no
    /// punctuation, no legal forms and no filler words.
    /// </summary>
    public static string[] CompanyTokens(string? name)
    {
        var normalized = Normalize(name);
        if (normalized.Length == 0) return [];

        var buffer = new StringBuilder(normalized.Length);
        foreach (var ch in normalized)
        {
            buffer.Append(char.IsAsciiLetterOrDigit(ch) ? ch : ' ');
        }

        return buffer.ToString()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(token => token.Length >= 2)
            .Where(token => !CompanyNoiseTokens.Contains(token))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static string MapChar(char ch)
    {
        foreach (var (from, to) in PolishMap)
        {
            if (ch == from) return to;
        }

        if (char.IsWhiteSpace(ch)) return " ";
        if (char.IsControl(ch)) return " ";
        return char.ToUpperInvariant(ch).ToString();
    }

    /// <summary>Whether the number starts with a country code and two check digits.</summary>
    private static bool LooksLikeIban(string value) =>
        value.Length > 4
        && char.IsAsciiLetter(value[0]) && char.IsAsciiLetter(value[1])
        && char.IsAsciiDigit(value[2]) && char.IsAsciiDigit(value[3]);
}
