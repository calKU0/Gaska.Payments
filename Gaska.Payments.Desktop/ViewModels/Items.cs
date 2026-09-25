using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using Gaska.Payments.Desktop.Data;
using Gaska.Payments.Desktop.Mvvm;

namespace Gaska.Payments.Desktop.ViewModels;

/// <summary>
/// A document on the selection list - with its tick, and who proposed it: the service, the model,
/// or nobody.
/// </summary>
/// <param name="advice">The model's proposal for this document, or null when it named none.</param>
/// <param name="selected">
/// Whether it arrives ticked. Decided outside, because which proposal to tick - the engine's or
/// the model's - is a question about the whole transfer, not about one document.
/// </param>
/// <param name="settledWithPayment">
/// The transfer in hand already settled this document in ERP. Such a document arrives ticked and
/// stays that way: the tick is a record of what happened, not an instruction.
/// </param>
public sealed class DocumentItem(
    DocumentRow row,
    bool suggested,
    SuggestionRow? advice,
    bool paymentIsIncoming,
    bool selected,
    bool settledWithPayment = false) : ObservableObject
{
    // A hint on a payment somebody has since flagged "nie rozliczaj" is stale: the badge still
    // says the service matched it, but it does not arrive ticked - settling it would undo a
    // decision already made in ERP.
    private bool _isSelected = settledWithPayment || (selected && !row.DoNotSettle);

    public DocumentRow Row { get; } = row;

    /// <summary>The matching engine assigned this document to the transfer.</summary>
    public bool IsSuggested { get; } = suggested;

    /// <summary>This transfer's settlement of the document, as it stands in ERP.</summary>
    public bool IsSettledWithPayment { get; } = settledWithPayment;

    /// <summary>
    /// Nothing is left open on the document - it has been settled, here or elsewhere.
    /// </summary>
    /// <remarks>
    /// Both halves are asked, because ERP writes them at different moments: the flag says the
    /// payment is closed, and the remaining amount says how much of it is. A payment left at
    /// nought with the flag still off would otherwise count as an open item and offer a tick that
    /// XL refuses.
    /// </remarks>
    public bool IsSettled => Row.SettlementFlag == 1 || Row.Remaining <= 0.004m;

    /// <summary>
    /// Whether this transfer can still be settled against the document.
    /// </summary>
    /// <remarks>
    /// It cannot once the document is closed or flagged "nie rozliczaj" - XL refuses both - so the
    /// tick is disabled rather than left to fail at the far end. The totals underneath and the
    /// settlement itself count only what this allows, which is why a document settled by this very
    /// transfer can sit on the list ticked and change nothing.
    /// </remarks>
    public bool CanSettle => !IsSettled && !DoNotSettle;

    /// <summary>Whether ERP's "nie rozliczaj" box may still be set - a closed payment has nothing to flag.</summary>
    public bool CanFlag => !IsSettled;

    /// <summary>The language model proposed this document.</summary>
    public bool IsAdvised => advice is not null;

    /// <summary>What the model would settle against the document, and why, for the badge's tooltip.</summary>
    public string AdviceText => advice is null
        ? string.Empty
        : advice.Reason.Length > 0
            ? $"AI proponuje rozliczyć {advice.Amount:N2}. {advice.Reason}"
            : $"AI proponuje rozliczyć {advice.Amount:N2}.";

    public bool IsSelected
    {
        get => _isSelected;
        set { if (Set(ref _isSelected, value)) SelectionChanged?.Invoke(); }
    }

    public event Action? SelectionChanged;

    private bool _doNotSettle = row.DoNotSettle;

    /// <summary>
    /// ERP's "nie rozliczaj" box on this payment. Ticking it says the open item is never going to
    /// be pursued; the transfer in hand cannot then be settled against it, so the tick clears the
    /// selection along with it.
    /// </summary>
    public bool DoNotSettle
    {
        get => _doNotSettle;
        set
        {
            if (!Set(ref _doNotSettle, value)) return;

            if (value) IsSelected = false;

            Raise(nameof(SettlementState));
            Raise(nameof(CanSettle));
            DoNotSettleChanged?.Invoke(this);
        }
    }

    /// <summary>Raised when the box is ticked or cleared, so the change reaches ERP.</summary>
    public event Action<DocumentItem>? DoNotSettleChanged;

    /// <summary>Puts the box back without writing anything - for when the write failed.</summary>
    public void ResetDoNotSettle(bool value)
    {
        _doNotSettle = value;
        Raise(nameof(DoNotSettle));
        Raise(nameof(SettlementState));
        Raise(nameof(CanSettle));
    }

    public string DocNumber => Row.DocNumber;

    public string Kind => Row.IsLiability ? "zobowiązanie" : "należność";

    /// <summary>
    /// How far this document is settled, in the same words the queue uses. It is what the state
    /// filter over the list reads.
    /// </summary>
    public SettlementState SettlementState =>
        DoNotSettle ? Data.SettlementState.DoNotSettle
        : IsSettled ? Data.SettlementState.Settled
        : Row.Remaining < Row.Amount - 0.004m ? Data.SettlementState.Partial
        : Data.SettlementState.Unsettled;

    public string Symbol => Row.Symbol;

    /// <summary>
    /// How the document is to be paid, in the words the accounting team uses. Documents settled on
    /// the courier register say so plainly - those are the ones a courier's transfer closes, and
    /// nothing else on the list is a cash on delivery however its payment form is spelt.
    /// </summary>
    public string PaymentForm => Row.IsCashOnDelivery ? "za pobraniem" : Row.PaymentForm.ToLowerInvariant();

    public bool IsCashOnDelivery => Row.IsCashOnDelivery;

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
    private string? _sourceDocument;

    public string? SourceDocument
    {
        get => _sourceDocument;
        set
        {
            if (!Set(ref _sourceDocument, value)) return;

            Raise(nameof(HasSourceDocument));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool HasSourceDocument => SourceDocument is { Length: > 0 };

    /// <summary>
    /// Looks in the archive again for the file behind this entry.
    /// </summary>
    /// <remarks>
    /// Done on entering the row, not only when the queue is loaded. Today's statement is written
    /// during the day - the service replaces it every pass as more transfers arrive - so a
    /// transfer booked this morning has no file when the queue is opened and has one an hour
    /// later. Without this the button stayed missing until somebody pressed Refresh.
    /// </remarks>
    public void RefreshSourceDocument(Func<PaymentRow, string?> find) => SourceDocument = find(Row);

    /// <summary>What the button that opens it says - a statement is not a payout report.</summary>
    public string SourceDocumentLabel =>
        Row.SourceFile.Length > 0 ? "Pokaż zestawienie pobrań" : "Pokaż wyciąg bankowy";

    public string PayerName => Row.PayerName;

    public string Description => Row.Description;

    public string Confidence => Row.Confidence;

    /// <summary>Why the engine classified the payment the way it did.</summary>
    public string Notes => Row.Notes;

    /// <summary>
    /// The language model's reasoning on the transfer, or word that it is still thinking. Empty
    /// when it has not been asked.
    /// </summary>
    public string AdvisorText => Row.AdvisorStatus switch
    {
        "Done" => Row.AdvisorSummary.Length > 0 ? Row.AdvisorSummary : "AI nie wskazało dokumentów.",
        "Running" => "AI analizuje ten przelew…",
        _ => string.Empty,
    };

    public bool HasAdvisorText => AdvisorText.Length > 0;

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
        "ExplicitReferencesRounding" => "numery z tytułu, różnica groszowa",
        "ExplicitReferencesPartial" => "numery z tytułu, kwota inna",
        "SingleDocumentPartialPayment" => $"jeden dokument, {DirectionLabel} częściowa",
        "SubsetSumOnContractor" => $"zestaw {SideLabel} kontrahenta",
        "ReferencesExtendedBySubsetSum" => "numery z tytułu + dobrane pozycje",
        "OldestFirstFallback" => $"od najstarszych {SideLabel}",
        "BankOrderReference" => "referencja zlecenia z banku",
        _ => Row.Strategy,
    };

    /// <summary>The acronym alone - for the narrow column in the grid.</summary>
    public string ContractorAcronym => ResolvedAcronym
        ?? (Row.ContractorId != 0
            ? Row.ContractorAcronym
            : Row.OtherParty.Length > 0 ? Row.OtherParty : "—");

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
    /// The contractor on the transfer as an entry the picker can show, or null when there is none.
    /// </summary>
    /// <remarks>
    /// The drop-down can only display what is in its list, so the one already in force has to be
    /// in it - before anything has been searched for, it is the only entry there is. The NIP is
    /// not carried on the transfer and is left empty; nothing on this list is matched by it.
    /// </remarks>
    public ContractorRow? CurrentContractor => EffectiveContractorId == 0
        ? null
        : new ContractorRow(
            EffectiveContractorId,
            ResolvedAcronym ?? Row.ContractorAcronym,
            ResolvedAcronym is null ? Row.ContractorName : string.Empty,
            string.Empty);

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

    // ------------------------------------- the account the entry is posted against ---

    private string? _selectedAccount = row.EntryAccount.Length > 0 ? row.EntryAccount : null;

    /// <summary>
    /// The account in the chart of accounts this transfer will be settled to, chosen from the
    /// contractor's accounts.
    /// </summary>
    /// <remarks>
    /// Nothing is written to ERP when this changes. The account reaches the cash entry at the
    /// moment the transfer is settled, in the same breath as the party - a settlement and the
    /// account it is booked to are one decision, and splitting them into two buttons invited an
    /// account saved against a settlement that never happened.
    /// </remarks>
    public string? SelectedAccount
    {
        get => _selectedAccount;
        set
        {
            // The picker offers no empty entry, so "nothing" never comes from the operator - it
            // comes from the drop-down losing its selection while the list underneath it is
            // rebuilt for another contractor. Taking that would wipe the account the settlement is
            // going to use, and it did: an entry with 203-AGROTAKA on it came out blank in both
            // the column and the picker.
            if (string.IsNullOrWhiteSpace(value) && !string.IsNullOrWhiteSpace(_selectedAccount)) return;

            Set(ref _selectedAccount, value);
        }
    }

    /// <summary>
    /// Says the account again, without changing it.
    /// </summary>
    /// <remarks>
    /// Rebuilding the picker's list drops the drop-down's own selection, and nothing pushes the
    /// value back afterwards - the model has not changed, so the binding stays quiet and the
    /// control shows an empty box over a column that reads correctly. Raising the change once the
    /// new list is in place is what makes it resolve again.
    /// </remarks>
    public void RefreshSelectedAccount() => Raise(nameof(SelectedAccount));

    /// <summary>
    /// The accounts belonging to the contractor this transfer is settled with, commonest first.
    /// </summary>
    /// <remarks>
    /// It is not read off the contractor's card: for all 200 multi-account contractors checked
    /// here <c>CDN.KntKonta</c> declares nothing in any period, so what their entries actually use
    /// is the only evidence there is. The picker puts these at the head of the whole chart of
    /// accounts.
    /// </remarks>
    public IReadOnlyList<string> ContractorAccounts { get; private set; } = [];

    /// <summary>
    /// Whether the account may be chosen at all. It may not until a contractor is established -
    /// the account is booked against the party, and without one the choice would mean nothing.
    /// </summary>
    public bool CanChooseAccount => EffectiveContractorId != 0;

    /// <summary>Whose accounts the list currently holds - so it is not fetched twice.</summary>
    public int AccountOptionsFor { get; private set; }

    /// <summary>
    /// The account to book the settlement against - empty while the picker is showing somebody
    /// else's.
    /// </summary>
    /// <remarks>
    /// Highlighting a contractor in the hit list puts their accounts on the picker before anything
    /// is committed. Settling at that moment would post the entry against the contractor still on
    /// it and an account belonging to the one merely being looked at. Empty here means the
    /// statement falls back to the account this contractor's own entries carry, which is right.
    /// </remarks>
    public string AccountForSettlement =>
        AccountOptionsFor == EffectiveContractorId ? SelectedAccount ?? string.Empty : string.Empty;

    // ------------------------------------------- what the contractor has overpaid ---

    private IReadOnlyList<ContractorOverpayment> _overpayments = [];

    /// <summary>
    /// Money from this contractor that ERP holds against nothing, per currency.
    /// </summary>
    /// <remarks>
    /// Shown beside the totals under the documents, because it changes what the accountant does:
    /// a transfer that does not cover the invoices may be meant to be topped up from an
    /// overpayment already sitting there, and without this they would have to go looking in ERP to
    /// find out.
    /// </remarks>
    public IReadOnlyList<ContractorOverpayment> Overpayments
    {
        get => _overpayments;
        set
        {
            _overpayments = value;

            Raise(nameof(Overpayments));
            Raise(nameof(HasOverpayment));
            Raise(nameof(OverpaymentAmount));
            Raise(nameof(OverpaymentDetail));
        }
    }

    /// <summary>The overpayment in this transfer's own currency - the one worth comparing.</summary>
    public decimal OverpaymentAmount =>
        _overpayments.FirstOrDefault(o => o.Currency == Currency)?.Amount ?? 0m;

    public bool HasOverpayment => _overpayments.Count > 0;

    /// <summary>How many payments it is made of, how old, and what is in other currencies.</summary>
    public string OverpaymentDetail
    {
        get
        {
            if (_overpayments.Count == 0) return "brak nierozliczonych wpłat";

            var mine = _overpayments.FirstOrDefault(o => o.Currency == Currency);
            var others = _overpayments.Where(o => o.Currency != Currency).ToList();

            var text = mine is null
                ? "nic w walucie przelewu"
                : $"{mine.Count} {Payments(mine.Count)}, najstarsza {mine.Oldest:dd.MM.yyyy}";

            return others.Count == 0
                ? text
                : $"{text}; poza tym {string.Join(", ", others.Select(o => $"{o.Amount:N2} {o.Currency}"))}";
        }
    }

    private static string Payments(int count) => count == 1 ? "wpłata" : count is >= 2 and <= 4 ? "wpłaty" : "wpłat";

    public void SetAccountOptions(IReadOnlyList<string> accounts, int contractorId)
    {
        AccountOptionsFor = contractorId;
        ContractorAccounts = accounts;

        // The account on the entry counts only while these options belong to the party the entry
        // is actually booked against. Once the operator swaps the contractor it is the previous
        // one's account and must not follow them over - the new contractor's commonest is the
        // honest default, and it is the one the settlement would pick anyway.
        var ours = contractorId == Row.ContractorId ? Row.EntryAccount : string.Empty;

        // Assigned to the field, not through the property: the property refuses to be emptied,
        // which is what stops the drop-down clobbering it, and here the model itself is deciding.
        //
        // What is on the entry wins over the contractor's commonest account even when the picker
        // does not offer it. Somebody typed it there - the accountant in ERP, or a settlement of
        // ours - and replacing it with a statistic would hide that, on the list and in the picker
        // alike.
        _selectedAccount =
            accounts.FirstOrDefault(a => string.Equals(a, ours, StringComparison.OrdinalIgnoreCase))
            ?? (ours.Length > 0 ? ours : accounts.FirstOrDefault());

        Raise(nameof(SelectedAccount));
        Raise(nameof(ContractorAccounts));
        Raise(nameof(CanChooseAccount));
    }

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

    /// <summary>Remembers the contractor named by the account or by the operator.</summary>
    /// <param name="byOperator">
    /// True when a human chose the contractor. The account is then absent from the register -
    /// otherwise the lookup by number would have found it.
    /// </param>
    public void UseContractor(int id, string acronym, string display, bool byOperator = false)
    {
        if (!byOperator) AccountKnown = true;
        EffectiveContractorId = id;
        ResolvedAcronym = acronym;
        ResolvedContractor = display;
        Raise(nameof(ContractorAcronym));
        Raise(nameof(ContractorDisplay));
        Raise(nameof(CurrentContractor));
        Raise(nameof(ContractorSourceLabel));
        Raise(nameof(AccountKnown));
        Raise(nameof(CanAssignAccount));
        Raise(nameof(CanChooseAccount));
    }

    /// <summary>
    /// Whether the entry is booked against something other than a contractor - a tax office or an
    /// employee. Nothing here can settle such an entry: XL settles contractor open items, and both
    /// the party update and <c>XLRozliczaj</c> take the party for a contractor.
    /// </summary>
    public bool IsOtherParty => Row.OtherParty.Length > 0;

    public string ContractorDisplay => ResolvedContractor
        ?? (Row.OtherParty.Length > 0
            ? $"{Row.OtherParty} — nie kontrahent"
            : Row.ContractorId == 0
                ? "nierozpoznany"
                : string.IsNullOrEmpty(Row.ContractorName)
                    ? Row.ContractorAcronym
                    : $"{Row.ContractorAcronym} — {Row.ContractorName}");

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
    public bool HasSettlements =>
        Row.SettlementState is SettlementState.Partial or SettlementState.Settled;

    /// <summary>
    /// Whether the transfer can be marked as not subject to settlement.
    /// </summary>
    /// <remarks>
    /// Only an untouched entry. The flag means "this is not an open item", and once somebody has
    /// matched part of the amount to something that is untrue - the settlement has to be revoked
    /// first.
    /// </remarks>
    public bool CanMarkDoNotSettle =>
        Row.SettlementState is SettlementState.Unsettled or SettlementState.DoNotSettle;

    /// <summary>Whether ERP already holds this transfer as not subject to settlement.</summary>
    public bool IsDoNotSettle => Row.SettlementState == SettlementState.DoNotSettle;

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

    /// <summary>
    /// The documents this transfer is to be settled against.
    /// </summary>
    /// <remarks>
    /// A tick alone is not enough: documents this transfer has already settled arrive ticked, as a
    /// record of what ERP holds, and sending them to XL a second time would only earn an error.
    /// </remarks>
    public IReadOnlyList<DocumentItem> Selected => [.. Documents.Where(d => d.IsSelected && d.CanSettle)];

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
    public decimal SelectedInvoices => Selected
        .Where(d => !d.Row.IsLiability)
        .Sum(d => d.Row.Remaining);

    public decimal SelectedLiabilities => Selected
        .Where(d => d.Row.IsLiability)
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
