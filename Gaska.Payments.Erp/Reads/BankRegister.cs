using Gaska.Payments.Domain.Parsing;

namespace Gaska.Payments.Erp;

/// <summary>
/// A bank register from ERP (<c>CDN.Rejestry</c>): series, currency, account number and country.
/// </summary>
/// <remarks>
/// The account number here is the single source of truth about which accounts we download
/// statements for - configuration names register series and nothing else.
///
/// This type carries no cash operation symbols: the posting query picks those itself, straight
/// from the statement import configuration on the register (<c>KAR_ImpKAO*</c>). Holding them
/// here as well would have meant two places describing the same thing, and only one of them was
/// ever used.
/// </remarks>
public sealed record BankRegister(
    string Series,
    string Name,
    string Currency,
    string AccountNumber,
    string Country)
{
    /// <summary>The account number reduced to a form comparable with what the bank sends.</summary>
    public string NormalizedAccount { get; } = TextNormalizer.NormalizeAccount(AccountNumber);

    /// <summary>
    /// The full IBAN, country code included - the form the bank has to be asked for a statement
    /// in. Empty when it cannot be assembled.
    /// </summary>
    /// <remarks>
    /// ERP keeps a number it considers an IBAN <b>without</b> the country code
    /// (<c>KAR_IBAN = 1</c>, the country held separately in <c>KAR_Kraj</c>) - the same convention
    /// as on contractor accounts. GOconnect, by contrast, demands the full number: given bare
    /// digits it answers with error <c>E201, malformed request message</c>, naming the IBAN
    /// element outright. That is why the country code is attached here, when reading the register,
    /// rather than being left to configuration.
    /// </remarks>
    public string Iban { get; } = BuildIban(AccountNumber, Country);

    private static string BuildIban(string accountNumber, string country)
    {
        var number = TextNormalizer.CompactAccount(accountNumber);
        if (number.Length == 0) return string.Empty;

        // The number is sometimes stored with the country code already - nothing to attach then.
        if (IsFullIban(number)) return number;

        var code = country.Trim().ToUpperInvariant();
        return code.Length == 2 ? code + number : string.Empty;
    }

    /// <summary>Two letters of country code and two check digits - the start of a full IBAN.</summary>
    private static bool IsFullIban(string number) =>
        number.Length > 4
        && char.IsAsciiLetter(number[0]) && char.IsAsciiLetter(number[1])
        && char.IsAsciiDigit(number[2]) && char.IsAsciiDigit(number[3]);
}
