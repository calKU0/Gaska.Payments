using Gaska.Payments.Domain.Parsing;

namespace Gaska.Payments.Desktop.Data;

/// <summary>The layout of an account number in one country: where the bank code sits and how long the whole IBAN is.</summary>
/// <param name="Offset">Offset of the bank code within the BBAN - non-zero only for Italy and San Marino.</param>
/// <param name="Length">Length of the bank code.</param>
/// <param name="IbanLength">Length of the whole IBAN, country code and check digits included.</param>
public sealed record IbanLayout(int Offset, int Length, int IbanLength);

/// <summary>
/// Taking an account number apart into the pieces Comarch ERP XL recognises a bank by.
/// </summary>
/// <remarks>
/// XL's rule is the same for every country and was established experimentally on the test
/// database: when a number passes IBAN validation, XL <b>ignores the bank code it was given</b>
/// (it only checks that such a card exists at all) and looks for a card that has
/// <c>Bnk_IBAN = 1</c> and whose <c>Bnk_Numer</c> opens the account number counted from after the
/// two check digits. Find none and it leaves the account with no bank - even when the bank was
/// chosen by hand in the ERP window. Hence the bank that vanishes the moment "IBAN" is ticked.
///
/// Domestic accounts are no exception - the register simply holds a complete set of correct cards
/// for Polish banks, where <c>Bnk_Numer</c> is the eight-digit clearing code at positions 3 to 10
/// of the national account number.
/// </remarks>
public static class IbanParts
{
    /// <summary>
    /// The account number without its country code and without the two check digits - the BBAN.
    /// For a domestic number, which arrives with no prefix, only the check digits come off.
    /// </summary>
    public static string Bban(string account)
    {
        var number = Normalize(account);

        if (number.Length > 2 && char.IsLetter(number[0]) && char.IsLetter(number[1]))
        {
            number = number[2..];
        }

        return number.Length > 2 ? number[2..] : string.Empty;
    }

    /// <summary>The country code from the account number; a number with no prefix is taken to be Polish.</summary>
    public static string Country(string account)
    {
        var number = Normalize(account);

        return number.Length > 2 && char.IsLetter(number[0]) && char.IsLetter(number[1])
            ? number[..2]
            : "PL";
    }

    /// <summary>
    /// The bank code taken out of the account number, or empty when the layout of numbers in that
    /// country is unknown to us or the number does not look like that country's IBAN.
    /// </summary>
    /// <remarks>
    /// Within one country the bank code always sits in the same place - so says the IBAN registry
    /// (ISO 13616). It differs only between countries: Germany has eight digits, France five,
    /// Romania four letters, and Italy is the only one listed here whose BBAN opens with the CIN
    /// check character, so its bank code (ABI) starts one position later.
    /// </remarks>
    public static string BankCode(string account)
    {
        if (Layout(account) is not { } layout || !LooksLikeIban(account)) return string.Empty;

        var bban = Bban(account);
        return bban.Length >= layout.Offset + layout.Length
            ? bban.Substring(layout.Offset, layout.Length)
            : string.Empty;
    }

    /// <summary>
    /// The number with no spaces or separators, in upper case - the way the bank sends it, that
    /// is with its country code.
    /// </summary>
    public static string Compact(string account) => Normalize(account);

    /// <summary>
    /// The number in the form Comarch ERP XL keeps it in - without the country code.
    /// </summary>
    /// <remarks>
    /// ERP strips the country code from everything it considers an IBAN, Polish accounts
    /// included. The rule is shared with the matching engine so that both sides of a comparison
    /// see the number the same way - it is described at
    /// <see cref="TextNormalizer.NormalizeAccount"/>.
    /// </remarks>
    public static string WithoutCountryCode(string account) => TextNormalizer.NormalizeAccount(account);

    /// <summary>The account number layout for that account's country, or null when unknown.</summary>
    public static IbanLayout? Layout(string account) =>
        Layouts.TryGetValue(Country(account), out var layout) ? layout : null;

    /// <summary>
    /// Whether the number is an IBAN of its country at all: it has that country's length and
    /// starts with two check digits.
    /// </summary>
    /// <remarks>
    /// Without this check, numbers in national formats - thirteen-digit Belarusian ones, Chinese
    /// ones, pre-IBAN Turkish ones - yield a bank code cut from an arbitrary position. The
    /// register holds over two hundred such accounts, so this is no theoretical case.
    /// </remarks>
    public static bool LooksLikeIban(string account)
    {
        if (Layout(account) is not { } layout) return false;

        var number = Normalize(account);

        if (number.Length > 2 && char.IsLetter(number[0]) && char.IsLetter(number[1]))
        {
            number = number[2..];
        }

        // The number is sometimes stored without a country code - that is how ERP keeps it -
        // so we compare what is left.
        return number.Length == layout.IbanLength - 2
            && char.IsDigit(number[0])
            && char.IsDigit(number[1]);
    }

    /// <summary>
    /// The position of the bank code and the IBAN length according to the ISO 13616 registry.
    /// </summary>
    /// <remarks>
    /// Every entry was verified against accounts in the ERP register - the code extracted agrees
    /// with the bank the account belongs to (Romanian <c>INGB</c> and <c>BTRL</c>, British
    /// <c>HBUK</c> and <c>LOYD</c>, French <c>28233</c>, German <c>50080000</c>), and the lengths
    /// agree character for character with the numbers stored in the database.
    ///
    /// Countries absent from the register are not guessed at: either there is an entry here taken
    /// from the IBAN registry, or the application asks the operator and shows them which part of
    /// the number it takes for the bank code. Finland, China and India are left out deliberately:
    /// the latter two do not use IBAN at all, and for a Finnish number the registry and practice
    /// diverge enough that we would rather ask.
    /// </remarks>
    private static readonly Dictionary<string, IbanLayout> Layouts = new()
    {
        ["AT"] = new(0, 5, 20),
        ["BE"] = new(0, 3, 16),
        ["BG"] = new(0, 4, 22),
        ["BR"] = new(0, 8, 29),
        ["BY"] = new(0, 4, 28),
        ["CH"] = new(0, 5, 21),
        ["CY"] = new(0, 3, 28),
        ["CZ"] = new(0, 4, 24),
        ["DE"] = new(0, 8, 22),
        ["DK"] = new(0, 4, 18),
        ["EE"] = new(0, 2, 20),
        ["ES"] = new(0, 4, 24),
        ["FR"] = new(0, 5, 27),
        ["GB"] = new(0, 4, 22),
        ["GR"] = new(0, 3, 27),
        ["HR"] = new(0, 7, 21),
        ["HU"] = new(0, 3, 28),
        ["IE"] = new(0, 4, 22),
        ["IL"] = new(0, 3, 23),
        ["IS"] = new(0, 4, 26),
        ["IT"] = new(1, 5, 27),   // the BBAN opens with the CIN check character, then the ABI
        ["KZ"] = new(0, 3, 20),
        ["LI"] = new(0, 5, 21),
        ["LT"] = new(0, 5, 20),
        ["LU"] = new(0, 3, 20),
        ["LV"] = new(0, 4, 21),
        ["MC"] = new(0, 5, 27),
        ["MD"] = new(0, 2, 24),
        ["MT"] = new(0, 4, 31),
        ["NL"] = new(0, 4, 18),
        ["NO"] = new(0, 4, 15),
        ["PL"] = new(0, 8, 28),
        ["PT"] = new(0, 4, 25),
        ["RO"] = new(0, 4, 24),
        ["RS"] = new(0, 3, 22),
        ["SE"] = new(0, 3, 24),
        ["SI"] = new(0, 5, 19),
        ["SK"] = new(0, 4, 24),
        ["SM"] = new(1, 5, 27),   // same layout as Italy
        ["TR"] = new(0, 5, 26),
        ["UA"] = new(0, 6, 29),
    };

    private static string Normalize(string account) =>
        new([.. account.Where(char.IsLetterOrDigit)]);
}
