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
    SettlementState SettlementState,
    /// <summary>
    /// The account in the chart of accounts the entry is posted against (<c>KAZ_KontoPrzec</c>).
    /// Empty when nobody has set one - which is what an entry posted on the anonymous party looks
    /// like until it is settled.
    /// </summary>
    string EntryAccount,
    /// <summary>
    /// The party the entry is booked against when it is not a contractor - a tax office or an
    /// employee. Empty for everything the application can settle; when it is filled in, the
    /// transfer is shown but left alone, because settlement in XL only knows contractors.
    /// </summary>
    string OtherParty,
    /// <summary>
    /// Where the language model's question about this transfer stands - <c>Running</c> or
    /// <c>Done</c> - and empty when it was never asked, or failed.
    /// </summary>
    string AdvisorStatus,
    /// <summary>The model's reasoning for the transfer as a whole. Empty until it has answered.</summary>
    string AdvisorSummary);

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
    string ForeignNumber,
    /// <summary>
    /// <c>TrP_Rozliczona</c> as ERP holds it: 0 open, 1 settled, 2 the "nie rozliczaj" box.
    /// </summary>
    int SettlementFlag,
    /// <summary>The payment form as ERP spells it - "Przelew", "Za pobraniem", "Gotówka".</summary>
    string PaymentForm,
    /// <summary>The series of the register the payment is settled on, empty when it names none.</summary>
    string Register)
{
    /// <summary>
    /// Whether this is money a courier collects from the customer at the door.
    /// </summary>
    /// <remarks>
    /// Decided by the register rather than by the payment form. The two nearly always agree -
    /// 178 120 of the 178 161 payments on K_GLS say "Za pobraniem" - but the form on its own is
    /// not the same question: a few hundred payments carry that form while sitting on FORPL or
    /// KASPL, and those are settled by an ordinary transfer or over the counter, not by a courier.
    /// </remarks>
    public bool IsCashOnDelivery =>
        Register.Equals(CashOnDeliveryRegister, StringComparison.OrdinalIgnoreCase);

    /// <summary>The register the couriers' money is paid onto.</summary>
    public const string CashOnDeliveryRegister = "K_GLS";

    /// <summary>
    /// Whether the payment carries ERP's "nie rozliczaj" flag - the accountants' own decision
    /// that this open item is never going to be pursued.
    /// </summary>
    public bool DoNotSettle => SettlementFlag == 2;

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

/// <summary>Who proposed a document for a transfer.</summary>
public enum SuggestionSource
{
    /// <summary>The matching engine - its documents arrive ticked.</summary>
    Service,

    /// <summary>The language model asked about what the engine could not settle - never ticked.</summary>
    Advisor,
}

/// <summary>A hint: a document proposed for a transfer, by the engine or by the model.</summary>
public sealed record SuggestionRow(
    long PaymentId,
    int DocType,
    int DocId,
    int DocLp,
    string DocNumber,
    decimal Amount,
    double Score,
    string Reason,
    SuggestionSource Source);

/// <summary>
/// Money from a contractor sitting in ERP against nothing - a payment nobody has allocated.
/// </summary>
/// <param name="Oldest">
/// The day of the oldest of them. Some go back years, and that is worth seeing: an overpayment
/// from 2011 is a different conversation from one from last week.
/// </param>
public sealed record ContractorOverpayment(string Currency, int Count, decimal Amount, DateTime Oldest);

/// <summary>One account of the chart of accounts.</summary>
public sealed record AccountRow(string Account, string Name);

/// <summary>An account as the picker offers it.</summary>
/// <param name="Contractors">
/// Whether it is one this contractor is settled to. Those head the list and are marked, because
/// one of them is nearly always the answer.
/// </param>
public sealed record AccountOption(string Account, string Name, bool Contractors)
{
    public string Display => Name.Length > 0 ? $"{Account} · {Name}" : Account;
}

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
