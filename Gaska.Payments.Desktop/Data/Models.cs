namespace Gaska.Payments.Desktop.Data;

/// <summary>A transfer awaiting settlement - the ERP bank entry plus what the service knows about it.</summary>
public sealed record PaymentRow(
    long PaymentId,
    int ErpEntryId,
    string BankExternalId,
    DateTime BookingDate,
    decimal Amount,
    decimal Remaining,
    string Currency,
    string RegisterSeries,
    string PayerName,
    string PayerAccount,
    string Description,
    string Confidence,
    int ContractorId,
    string ContractorAcronym,
    string ContractorName,
    string ContractorSource,
    bool ContractorFromBankAccount,
    string Notes,
    string Strategy,
    string Status,
    string LastError,
    string BankBic,
    string BankName,
    bool IsIncoming,
    /// <summary>
    /// The courier payout report this entry came out of. Empty for bank operations - their
    /// statement is found in the archive by register and date.
    /// </summary>
    string SourceFile,
    /// <summary>A credit card account - it gets an icon of its own in the application.</summary>
    bool IsCard,
    /// <summary>
    /// A register the service does not settle: cards, the social fund, auxiliary accounts.
    /// Such registers enter the filter unchecked.
    /// </summary>
    bool WithoutSettlement,
    SettlementState SettlementState);

/// <summary>An open document payment in ERP.</summary>
/// <param name="PaymentType">1 is a liability, 2 a receivable.</param>
/// <param name="Symbol">The document type's abbreviation from <c>CDN.Obiekty</c> - FS, FZ, UNM and so on.</param>
public sealed record DocumentRow(
    int DocType,
    int DocId,
    int DocLp,
    string DocNumber,
    int PaymentType,
    decimal Amount,
    decimal Remaining,
    string Currency,
    DateTime DueDate,
    int ContractorId,
    string ContractorAcronym,
    string Symbol,
    string ForeignNumber)
{
    /// <summary>
    /// The signed amount, in the engine's convention: a receivable is positive, the opposite side
    /// negative. The engine nets opposite sides off against receivables, so the sign has to be
    /// unambiguous.
    /// </summary>
    public decimal SignedRemaining => PaymentType == 1 ? -Remaining : Remaining;

    /// <summary>
    /// Whether the item sits on the opposite side of the ledger from an incoming payment - a
    /// sales correction, a purchase invoice, a note. Those have to be netted off against a
    /// receivable first.
    /// </summary>
    public bool IsLiability => PaymentType == 1;
}

/// <summary>The service's hint: a document the matching engine assigned to a transfer.</summary>
public sealed record SuggestionRow(
    long PaymentId,
    int DocType,
    int DocId,
    int DocLp,
    string DocNumber,
    decimal Amount,
    double Score,
    string Reason);

/// <summary>A contractor from the register - for swapping by hand when the service named the wrong one.</summary>
public sealed record ContractorRow(int Id, string Acronym, string Name, string Nip)
{
    public string Display => string.IsNullOrEmpty(Name) ? Acronym : $"{Acronym} — {Name}";
}

/// <summary>
/// A settlement of a bank entry read from <c>CDN.Rozliczenia</c>.
/// </summary>
/// <remarks>
/// We keep no GID table of our own - ERP holds the full set: <c>R2_ID</c> is the settlement's GID
/// number, <c>R2_GIDFirma</c> the company, and <c>R2_OpeNumerRL</c> records the operator who
/// created it. Type and Lp are constant (verified against what <c>XLRozliczaj</c> returns).
/// </remarks>
public sealed record SettlementRow(
    int SettlementId,
    int GidFirma,
    string DocNumber,
    decimal Amount,
    DateTime SettledAt,
    string SettledBy);
