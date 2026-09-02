using System.Collections.ObjectModel;
using System.Linq;
using Gaska.Payments.Desktop.Data;
using Gaska.Payments.Desktop.Mvvm;

namespace Gaska.Payments.Desktop.ViewModels;

/// <summary>A document on the selection list - with its tick and whether the service suggested it.</summary>
public sealed class DocumentItem(
    DocumentRow row, bool suggested, bool paymentIsIncoming) : ObservableObject
{
    private bool _isSelected = suggested;

    public DocumentRow Row { get; } = row;

    public bool IsSuggested { get; } = suggested;

    public bool IsSelected
    {
        get => _isSelected;
        set { if (Set(ref _isSelected, value)) SelectionChanged?.Invoke(); }
    }

    public event Action? SelectionChanged;

    public string DocNumber => Row.DocNumber;

    public string Kind => Row.IsLiability ? "zobowiązanie" : "należność";

    public string Symbol => Row.Symbol;

    /// <summary>The document number at the contractor's end - for liabilities, the supplier's invoice number.</summary>
    public string ForeignNumber => Row.ForeignNumber;

    /// <summary>
    /// The remaining amount signed by its side: liabilities and corrections negative, receivables
    /// positive. The sign is a property of the document, not of the transfer - and that is how the
    /// accounting team sees it.
    /// </summary>
    public decimal Remaining => Row.IsLiability ? -Row.Remaining : Row.Remaining;

    /// <summary>
    /// Whether the document sits on the same side as the transfer: money in settles receivables,
    /// money out settles liabilities.
    /// </summary>
    public bool MatchesPaymentSide => paymentIsIncoming ? !Row.IsLiability : Row.IsLiability;

    /// <summary>
    /// The signed amount in the engine's convention: positive for the side the transfer settles,
    /// negative for the opposite side, which has to be netted off first.
    /// </summary>
    public decimal SignedRemaining => MatchesPaymentSide ? Row.Remaining : -Row.Remaining;

    /// <summary>The due date - the column that shows it with a caption sorts by this.</summary>
    public DateTime DueDate => Row.DueDate;

    public string Currency => Row.Currency;

    public string ContractorAcronym => Row.ContractorAcronym;

    /// <summary>Days overdue - negative while the due date is still ahead.</summary>
    public int DaysOverdue => (int)(DateTime.Today - Row.DueDate.Date).TotalDays;

    public string DueLabel => DaysOverdue > 0
        ? $"{Row.DueDate:dd.MM.yyyy} ({DaysOverdue} dni po terminie)"
        : Row.DueDate.ToString("dd.MM.yyyy");
}

/// <summary>A transfer in the queue together with the documents chosen to settle it.</summary>
public sealed class PaymentItem(PaymentRow row) : ObservableObject
{
    private bool _isChecked;
    private string _lastResult = row.LastError;

    public PaymentRow Row { get; } = row;

    public ObservableCollection<DocumentItem> Documents { get; } = [];

    /// <summary>The documents the matching engine assigned - ticked to begin with.</summary>
    public IReadOnlyList<SuggestionRow> Suggestions { get; set; } = [];

    /// <summary>Whether the contractor's open documents have been loaded - done on demand only.</summary>
    public bool DocumentsLoaded { get; set; }

    /// <summary>The tick on the list - for settling several transfers at once.</summary>
    public bool IsChecked
    {
        get => _isChecked;
        set => Set(ref _isChecked, value);
    }

    /// <summary>The message left by the last attempt to settle this transfer.</summary>
    public string LastResult
    {
        get => _lastResult;
        set => Set(ref _lastResult, value);
    }

    public long PaymentId => Row.PaymentId;

    public DateTime BookingDate => Row.BookingDate;

    public decimal Amount => Row.Amount;

    public decimal Remaining => Row.Remaining;

    public string Currency => Row.Currency;

    public string RegisterSeries => Row.RegisterSeries;

    /// <summary>
    /// The register's icon in the column: the flag of the currency's country, and a card for card
    /// accounts. The point is to make a register recognisable at a glance.
    /// </summary>
    public string RegisterIcon => RegisterIcons.For(Row.RegisterSeries, Row.IsCard);

    /// <summary>Whether the transfer sits on a register the service does not settle.</summary>
    public bool WithoutSettlement => Row.WithoutSettlement;

    /// <summary>
    /// The archived document behind the entry, set when the queue is loaded. Null when the archive
    /// does not hold one - a day the bank sent no statement, or a report from before the archive
    /// existed.
    /// </summary>
    public string? SourceDocument { get; set; }

    public bool HasSourceDocument => SourceDocument is { Length: > 0 };

    /// <summary>What the button that opens it says - a statement is not a payout report.</summary>
    public string SourceDocumentLabel =>
        Row.SourceFile.Length > 0 ? "Pokaż zestawienie pobrań" : "Pokaż wyciąg bankowy";

    public string PayerName => Row.PayerName;

    public string Description => Row.Description;

    public string Confidence => Row.Confidence;

    /// <summary>Why the engine classified the payment the way it did.</summary>
    public string Notes => Row.Notes;

    /// <summary>
    /// The side of the ledger this transfer settles, in the genitive, to be dropped into a
    /// sentence.
    /// </summary>
    /// <remarks>
    /// Money in closes receivables, money out closes liabilities. Labels written for incoming
    /// payments misled on outgoing ones, speaking of receivables where liabilities were meant.
    /// </remarks>
    private string SideLabel => Row.IsIncoming ? "należności" : "zobowiązań";

    /// <summary>The strategy by which the engine arrived at this proposal.</summary>
    public string StrategyLabel => Row.Strategy switch
    {
        "NoCandidates" => "brak kandydatów",
        "ExplicitReferencesExactSum" => "numery z tytułu, kwota zgodna",
        "ExplicitReferencesPartial" => "numery z tytułu, kwota inna",
        "SingleDocumentPartialPayment" => $"jeden dokument, {DirectionLabel} częściowa",
        "SubsetSumOnContractor" => $"zestaw {SideLabel} kontrahenta",
        "ReferencesExtendedBySubsetSum" => "numery z tytułu + dobrane pozycje",
        "OldestFirstFallback" => $"od najstarszych {SideLabel}",
        "BankOrderReference" => "referencja zlecenia z banku",
        _ => Row.Strategy,
    };

    /// <summary>The acronym alone - for the narrow column in the grid.</summary>
    public string ContractorAcronym => Row.ContractorId != 0
        ? Row.ContractorAcronym
        : ResolvedAcronym ?? "—";

    /// <summary>Acronym of the contractor found from the counterparty account.</summary>
    public string? ResolvedAcronym { get; private set; }

    private string? _resolvedContractor;

    /// <summary>
    /// A contractor found from the counterparty account when the engine established none. It is
    /// treated as a hint - the choice stays with the accountant.
    /// </summary>
    public string? ResolvedContractor
    {
        get => _resolvedContractor;
        private set { if (Set(ref _resolvedContractor, value)) Raise(nameof(ContractorDisplay)); }
    }

    /// <summary>Id of the contractor actually in use - from the engine or chosen by hand.</summary>
    public int EffectiveContractorId { get; private set; } = row.ContractorId;

    /// <summary>
    /// Whether the counterparty account is already on the contractor's card.
    /// </summary>
    /// <remarks>
    /// A different thing from "we know the contractor": the operator may have chosen one by hand
    /// while the account is still absent from ERP. Confusing the two disabled the "add account"
    /// button in precisely the case where it is needed most.
    /// </remarks>
    public bool AccountKnown { get; private set; } = row.ContractorFromBankAccount;

    public bool CanAssignAccount =>
        Row.PayerAccount.Length > 0 && EffectiveContractorId != 0 && !AccountKnown;

    /// <summary>Records that the account reached the named contractor's card.</summary>
    public void MarkAccountAssigned() => SetAccountKnown(true);

    /// <summary>
    /// Sets the account state as verified against the ERP register.
    /// </summary>
    /// <remarks>
    /// The check against the database is what decides - the flag in our own table only says how
    /// the service recognised the contractor, and it goes stale.
    /// </remarks>
    public void SetAccountKnown(bool known)
    {
        if (AccountKnown == known) return;

        AccountKnown = known;
        Raise(nameof(AccountKnown));
        Raise(nameof(CanAssignAccount));
    }

    /// <summary>
    /// Whether the operator swapped the contractor by hand - if so, the service's own answer can
    /// be restored.
    /// </summary>
    public bool ContractorOverridden { get; private set; }

    /// <summary>Restores the contractor and documents to the shape the service established.</summary>
    public void RestoreServiceContractor()
    {
        ContractorOverridden = false;
        AccountKnown = Row.ContractorFromBankAccount;
        EffectiveContractorId = Row.ContractorId;
        ResolvedAcronym = null;
        ResolvedContractor = null;

        Raise(nameof(ContractorAcronym));
        Raise(nameof(ContractorSourceLabel));
        Raise(nameof(AccountKnown));
        Raise(nameof(CanAssignAccount));
        Raise(nameof(ContractorOverridden));
    }

    /// <summary>Remembers the contractor named by the account or by the operator.</summary>
    /// <param name="byOperator">
    /// True when a human chose the contractor. The account is then absent from the register -
    /// otherwise the lookup by number would have found it.
    /// </param>
    public void UseContractor(int id, string acronym, string display, bool byOperator = false)
    {
        ContractorOverridden = byOperator;
        if (!byOperator) AccountKnown = true;
        Raise(nameof(ContractorOverridden));
        EffectiveContractorId = id;
        ResolvedAcronym = acronym;
        ResolvedContractor = display;
        Raise(nameof(ContractorAcronym));
        Raise(nameof(ContractorSourceLabel));
        Raise(nameof(AccountKnown));
        Raise(nameof(CanAssignAccount));
    }

    public string ContractorDisplay => Row.ContractorId == 0
        ? ResolvedContractor ?? "nierozpoznany"
        : string.IsNullOrEmpty(Row.ContractorName)
            ? Row.ContractorAcronym
            : $"{Row.ContractorAcronym} — {Row.ContractorName}";

    /// <summary>How we know the contractor - the accountant has to see whether the evidence is solid.</summary>
    public string ContractorSourceLabel => Row.ContractorId != 0
        ? Row.ContractorFromBankAccount ? BankAccountSource : Describe(Row.ContractorSource)
        : ResolvedAcronym is null ? "—" : BankAccountSource;

    /// <summary>
    /// Recognition by account number - the only evidence sufficient to settle without a human.
    /// </summary>
    /// <remarks>
    /// The name speaks of the account rather than of a side of the transfer, because it covers
    /// both directions: on money in it is the sender's account, on money out the recipient's.
    /// </remarks>
    private const string BankAccountSource = "rachunek bankowy";

    /// <summary>
    /// Translates the recognition source stored in the database into what the accountant sees.
    /// </summary>
    /// <remarks>
    /// Older runs wrote "rachunek nadawcy" and "rachunek drugiej strony" - both names spoke of a
    /// direction although they mean the same thing. They are mapped so that history does not look
    /// different from what the service writes today.
    /// </remarks>
    private static string Describe(string source) => source switch
    {
        "rachunek nadawcy" or "rachunek drugiej strony" => BankAccountSource,
        _ => source,
    };

    public string DirectionLabel => Row.IsIncoming ? "wpłata" : "wypłata";

    /// <summary>How far the entry has been settled in ERP.</summary>
    public SettlementState SettlementState => Row.SettlementState;

    /// <summary>The state's wording for the list column.</summary>
    public string SettlementLabel => Row.SettlementState.Describe();

    /// <summary>Whether there is anything on the entry to revoke.</summary>
    public bool HasSettlements => Row.SettlementState != SettlementState.Unsettled;

    /// <summary>
    /// Whether the transfer can be marked as not subject to settlement.
    /// </summary>
    /// <remarks>
    /// Only an untouched entry. The flag means "this is not an open item", and once somebody has
    /// matched part of the amount to something that is untrue - the settlement has to be revoked
    /// first.
    /// </remarks>
    public bool CanMarkDoNotSettle => Row.SettlementState == SettlementState.Unsettled;

    /// <summary>On money in the counterparty is the sender, on money out the recipient.</summary>
    public string PartyLabel => Row.IsIncoming ? "Nadawca" : "Odbiorca";

    public string PartyAccountLabel => Row.IsIncoming ? "Rachunek nadawcy" : "Rachunek odbiorcy";

    public string ConfidenceLabel => Row.Confidence switch
    {
        "High" => "pewne",
        "Medium" => "do akceptacji",
        "Low" => "słabe",
        _ => "brak dopasowania",
    };

    /// <summary>How many of the service's hints still stand (the document is still open).</summary>
    public IReadOnlyList<DocumentItem> Selected => [.. Documents.Where(d => d.IsSelected)];

    /// <summary>
    /// Ticked documents whose currency differs from the transfer's.
    /// </summary>
    /// <remarks>
    /// ERP will not settle a euro entry against a zloty receivable, and without this check the
    /// attempt failed silently - the list refreshed and nothing happened.
    /// </remarks>
    public IReadOnlyList<DocumentItem> WrongCurrency =>
        [.. Selected.Where(d => !string.Equals(d.Currency, Currency, StringComparison.OrdinalIgnoreCase))];

    // Summed over Row.Remaining rather than DocumentItem.Remaining: the latter already carries
    // the side's sign and the total would come out at zero where it should show a difference.
    public decimal SelectedInvoices => Documents
        .Where(d => d.IsSelected && !d.Row.IsLiability)
        .Sum(d => d.Row.Remaining);

    public decimal SelectedLiabilities => Documents
        .Where(d => d.IsSelected && d.Row.IsLiability)
        .Sum(d => d.Row.Remaining);

    /// <summary>
    /// How much of the transfer the chosen documents will cover. Money in settles receivables less
    /// corrections, money out settles liabilities less receivables.
    /// </summary>
    public decimal SelectedNet => Row.IsIncoming
        ? SelectedInvoices - SelectedLiabilities
        : SelectedLiabilities - SelectedInvoices;

    /// <summary>Positive means something is left on the transfer; negative means the documents will not close in full.</summary>
    public decimal Difference => Remaining - SelectedNet;

    public bool IsBalanced => Math.Abs(Difference) < 0.02m;

    public void RaiseTotals()
    {
        Raise(nameof(SelectedInvoices));
        Raise(nameof(SelectedLiabilities));
        Raise(nameof(SelectedNet));
        Raise(nameof(Difference));
        Raise(nameof(IsBalanced));
    }
}
