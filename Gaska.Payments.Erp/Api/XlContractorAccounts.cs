using Microsoft.Extensions.Logging;
using cdn_api;

namespace Gaska.Payments.Erp;

/// <summary>
/// Adding bank accounts to a contractor's card through the XL API.
/// </summary>
/// <remarks>
/// The payer account is the only evidence that permits automatic settlement, so every manual
/// choice of contractor is worth recording - the next transfer from the same account will settle
/// on its own. This goes through <c>XLNowyRachunek</c>, not through a write to <c>CDN.*</c>.
/// </remarks>
public sealed class XlContractorAccounts(ILogger logger)
{
    private const int BatchMode = 2;

    /// <summary>The contractor GIDTyp in Comarch ERP XL.</summary>
    private const int ContractorGidType = 32;

    /// <summary>Adds an account to the card. Returns an error description, or null on success.</summary>
    /// <param name="bankCode">
    /// The bank's code from the ERP register (<c>CDN.Banki.Bnk_Kod</c>). Mandatory - without it
    /// the API returns error 4 and the account is created with no bank attached.
    /// </param>
    public string? Assign(
        XlSession session, int contractorId, string account, string currency, string? bankCode, string swift = "")
    {
        if (contractorId == 0) return "Nie wskazano kontrahenta.";
        if (string.IsNullOrWhiteSpace(account)) return "Przelew nie ma numeru rachunku drugiej strony.";
        if (string.IsNullOrWhiteSpace(bankCode))
        {
            return "Nie znaleziono w ERP banku prowadzącego ten rachunek. " +
                   "Dodaj bank ręcznie w ERP XL i spróbuj ponownie.";
        }

        var number = account.Trim();

        // A foreign account starts with a country code; a domestic one arrives from the bank
        // as bare digits, but it is an IBAN too - simply without the prefix.
        var foreign = number.Length > 2 && char.IsLetter(number[0]) && char.IsLetter(number[1]);

        // The flag is not set blindly: the register holds accounts that are not IBANs at all
        // (non-standard numbers, internal accounts) and they carry RkB_IBAN = 0.
        var isIban = foreign
            ? number.Length is >= 15 and <= 34 && number.Skip(2).Take(2).All(char.IsDigit)
            : number.Length == 26 && number.All(char.IsDigit);

        var info = new XLNowyRachunekInfo_20251
        {
            Wersja = session.Version,
            Tryb = BatchMode,
            ObiTyp = ContractorGidType,
            ObiNumer = contractorId,
            ObiLp = 0,
            NrRachunku = number,
            BankKod = bankCode.Trim(),
            Waluta = string.IsNullOrWhiteSpace(currency) ? "PLN" : currency.Trim(),
            // The field is a flag, not a place for the number - RkB_IBAN is its counterpart.
            IBAN = isIban ? "1" : "0",
            Kraj = foreign ? number[..2].ToUpperInvariant() : "PL",
            Swift = swift.Trim(),
            // An account taken off a statement is not made the default - a contractor may have
            // several, and all we know is that this one time they paid from this one.
            Domyslny = 0,
            Uwagi = "Dopisany z aplikacji rozliczania przelewów.",
        };

        var id = 0;
        var result = cdn_api.cdn_api.XLNowyRachunek(session.Id, ref id, info);

        if (result == 0)
        {
            logger.LogInformation("Added account {Account} to contractor {Contractor}.", number, contractorId);
            return null;
        }

        var message = XlErrors.Describe(session, XlErrors.NowyRachunek, result, "XLNowyRachunek");
        logger.LogError("XLNowyRachunek for contractor {Contractor}: {Message}", contractorId, message);

        return message;
    }
}
