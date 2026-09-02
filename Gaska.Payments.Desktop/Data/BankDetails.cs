namespace Gaska.Payments.Desktop.Data;

/// <summary>
/// Bank details typed in by hand when the bank is absent from the ERP register and the BNP
/// statement says nothing about it.
/// </summary>
/// <param name="BankCode">
/// The bank code taken out of the account number - it is by this, and by this alone, that Comarch
/// ERP XL ties a bank to an account. See <see cref="IbanParts"/>.
/// </param>
public sealed record BankDetails(
    string Bic,
    string Name,
    string City,
    string PostalCode,
    string CountryCode,
    string BankCode);

/// <summary>
/// A bank from the ERP register: the code the XL API uses and the GID the database uses.
/// </summary>
/// <param name="BindsInXl">
/// Whether Comarch ERP XL will tie this bank to the account by itself. True only when the bank's
/// card has the IBAN flag set and its clearing code opens the account number.
/// </param>
public sealed record BankRef(string Code, int Id, bool BindsInXl);

/// <summary>
/// What the operator is asked when Comarch ERP XL will not tie the bank to the account itself.
/// </summary>
/// <param name="BankCode">
/// The bank code taken out of the account number according to the IBAN registry. Empty when the
/// layout of numbers in that country is unknown to us - then, and only then, must the operator
/// supply the code themselves.
/// </param>
/// <param name="Known">Bank details to prefill the window with: from a card to be corrected, or from a hint.</param>
/// <param name="RepairsExistingCard">
/// Whether confirming will correct an existing bank card or create a new one. What decides this is
/// whether the bank was found in the register - a name suggested from another card does not make
/// that card this bank's card.
/// </param>
public sealed record BankPrompt(
    string Account,
    string BankCode,
    BankDetails? Known,
    bool RepairsExistingCard);
