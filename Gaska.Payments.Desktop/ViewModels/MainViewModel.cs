using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows.Data;
using System.Windows.Threading;
using System.Windows;
using Gaska.Payments.Desktop.Data;
using Gaska.Payments.Erp.Posting;
using Gaska.Payments.Desktop.Mvvm;
using Gaska.Payments.Domain.Diagnostics;
using Gaska.Payments.Erp;
using Microsoft.Extensions.Logging;

namespace Gaska.Payments.Desktop.ViewModels;

/// <summary>
/// The queue of transfers awaiting manual settlement and everything the accountant can do with it.
/// </summary>
/// <remarks>
/// All work with the XL API runs on the worker thread in one stretch, with no <c>await</c> between
/// calls - the native library is thread-bound, and interleaving it with asynchronous work ends in
/// a crash on sign-out.
/// </remarks>
public sealed class MainViewModel : ObservableObject
{
    private readonly QueueRepository _repository;
    private readonly RegisterSettings _registers;
    private readonly SourceDocuments _documents;
    private readonly DecisionStore _store;
    private readonly XlWorker _worker;
    private readonly XlSettlementEngine _engine;
    private readonly string _header;
    private readonly ILogger _logger;

    /// <summary>Who is working in the application. The settlement itself records the ERP operator (R2_OpeNumerRL).</summary>
    private static readonly string User = Environment.UserName;

    private PaymentItem? _selected;
    private string _search = string.Empty;
    private DateTime _from = DateTime.Today.AddDays(-60);
    private bool _isBusy;
    private string _status = string.Empty;
    private string _contractorQuery = string.Empty;

    /// <summary>Cancels the contractor search still in flight when another key is pressed.</summary>
    private CancellationTokenSource? _contractorSearch;
    private OperatorAccess? _operator;
    private string _accountFilter = string.Empty;

    /// <summary>The whole chart of accounts, read once when the queue is loaded.</summary>
    private IReadOnlyList<AccountRow> _chart = [];

    public MainViewModel(
        QueueRepository repository, DecisionStore store, XlWorker worker, RegisterSettings registers,
        SourceDocuments documents, string header, ILogger logger)
    {
        _repository = repository;
        _registers = registers;
        _documents = documents;
        _store = store;
        _worker = worker;
        _header = header;
        _logger = logger;
        _engine = new XlSettlementEngine(logger);

        _toastTimer.Tick += (_, _) => HideToast();

        View = CollectionViewSource.GetDefaultView(Payments);
        View.Filter = o => Matches((PaymentItem)o);

        // The queue defaults to newest first - that is how the accounting team reads it, and
        // sorting on the view rather than only in the query makes the header show the direction
        // arrow straight away.
        View.SortDescriptions.Add(
            new SortDescription(nameof(PaymentItem.BookingDate), ListSortDirection.Descending));

        foreach (var option in SettlementFilters) WatchSettlement(option);
        foreach (var option in ConfidenceFilters) Watch(option, nameof(ConfidenceFilterSummary));

        foreach (var option in DirectionFilters)
        {
            Watch(option, nameof(DirectionFilterSummary));

            // The counterparty column's header depends on what is on the list.
            option.PropertyChanged += (_, _) => Raise(nameof(PartyHeader));
        }

        // The registers come from configuration rather than from the queue, so that every one
        // the service handles is on the filter from the first moment - including a register that
        // happens to have nothing on it today.
        foreach (var series in _registers.All)
        {
            var option = new RegisterOption(
                series, _registers.IsCard(series), _registers.StartsUnchecked(series));

            Watch(option, nameof(RegisterFilterSummary));
            RegisterFilters.Add(option);
        }

        foreach (var side in DocumentSideFilters) WatchDocuments(side, nameof(DocumentSideFilterSummary));
        foreach (var state in DocumentStateFilters) WatchDocuments(state, nameof(DocumentStateFilterSummary));

        RefreshCommand = new RelayCommand(async _ => await LoadAsync(), _ => !IsBusy);
        SettleCheckedCommand = new RelayCommand(async _ => await SettleAsync(Checked), _ => !IsBusy && Checked.Count > 0);
        SettleCurrentCommand = new RelayCommand(
            async _ => await SettleAsync(Selected is null ? [] : [Selected]),
            _ => !IsBusy && Selected is { IsDoNotSettle: false } p && p.Selected.Count > 0);
        RevokeCommand = new RelayCommand(
            async _ => await RevokeAsync(),
            _ => !IsBusy && Selected is { HasSettlements: true });
        UseContractorCommand = new RelayCommand(
            async o => await UseContractorAsync(o as ContractorRow ?? ContractorPick),
            _ => !IsBusy && Selected is { } p && ContractorPick is { } pick && pick.Id != p.EffectiveContractorId);
        DoNotSettleCommand = new RelayCommand(
            async _ => await MarkDoNotSettleAsync(),
            _ => !IsBusy && DoNotSettleTargets().Any(i => i.IsDoNotSettle != DoNotSettleSets()));
        AssignAccountCommand = new RelayCommand(
            async _ => await AssignAccountAsync(),
            _ => !IsBusy && Selected is { CanAssignAccount: true });
        OpenSourceDocumentCommand = new RelayCommand(
            _ => OpenSourceDocument(),
            _ => Selected is { HasSourceDocument: true });
    }

    // ------------------------------------------------------------------ data ---

    public BulkObservableCollection<PaymentItem> Payments { get; } = [];

    public ICollectionView View { get; }

    public ObservableCollection<ContractorRow> ContractorHits { get; } = [];

    /// <summary>
    /// The accounts offered by the picker: this contractor's first, then the rest of the plan.
    /// </summary>
    /// <remarks>
    /// One list for the window rather than one per row - the plan runs to 41 537 accounts, and a
    /// copy on every transfer in the queue would be absurd.
    ///
    /// Replaced wholesale on every rebuild rather than emptied and refilled. Emptying a live
    /// <c>ItemsSource</c> takes the drop-down's selection with it, and pushing the same account
    /// back afterwards changes nothing - a dependency property ignores an assignment equal to what
    /// it already holds, so the control kept an unresolved value and painted an empty box over a
    /// column that read correctly. A new list makes the control resolve the selection again.
    /// </remarks>
    public IReadOnlyList<AccountOption> AccountOptions
    {
        get => _accountOptions;
        private set => Set(ref _accountOptions, value);
    }

    private IReadOnlyList<AccountOption> _accountOptions = [];

    /// <summary>
    /// What the operator has typed into the picker. Narrows the list to accounts whose number or
    /// name contains it.
    /// </summary>
    public string AccountFilter
    {
        get => _accountFilter;
        set { if (Set(ref _accountFilter, value)) RebuildAccountOptions(); }
    }

    /// <summary>How many accounts the list shows at once.</summary>
    /// <remarks>
    /// The plan is far too long to hand to a drop-down whole. The contractor's own accounts always
    /// fit inside this, and the rest is what the operator narrows by typing - which is why the
    /// filter searches the name as well as the number.
    /// </remarks>
    private const int AccountsShown = 200;

    /// <summary>
    /// Rebuilds the picker's list for the selected transfer.
    /// </summary>
    /// <remarks>
    /// The contractor's accounts come first, commonest first, because one of them is nearly always
    /// the answer. Everything else follows in account order, so a new account can still be chosen
    /// without leaving the application for ERP.
    /// </remarks>
    private void RebuildAccountOptions()
    {
        var mine = Selected?.ContractorAccounts ?? [];
        var filter = _accountFilter.Trim();

        bool Matches(string account, string name) =>
            filter.Length == 0
            || account.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || name.Contains(filter, StringComparison.OrdinalIgnoreCase);

        var names = _chart.ToDictionary(a => a.Account, a => a.Name, StringComparer.OrdinalIgnoreCase);

        var chosen = new List<AccountOption>();
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Whatever is on the entry heads the list whatever the filter says. Without it the
        // drop-down has nothing to resolve its own value against and comes up empty - which is
        // exactly what happened to accounts sitting past the 200 shown.
        if (Selected?.SelectedAccount is { Length: > 0 } current)
        {
            chosen.Add(new AccountOption(current, names.GetValueOrDefault(current, string.Empty),
                Contractors: mine.Contains(current, StringComparer.OrdinalIgnoreCase)));
            taken.Add(current);
        }

        foreach (var account in mine)
        {
            if (taken.Contains(account)) continue;

            var name = names.GetValueOrDefault(account, string.Empty);
            if (!Matches(account, name)) continue;

            chosen.Add(new AccountOption(account, name, Contractors: true));
            taken.Add(account);
        }

        foreach (var row in _chart)
        {
            if (chosen.Count >= AccountsShown) break;
            if (taken.Contains(row.Account) || !Matches(row.Account, row.Name)) continue;

            chosen.Add(new AccountOption(row.Account, row.Name, Contractors: false));
        }

        AccountOptions = chosen;

        Raise(nameof(AccountsFound));

        // The new list arrives with the selection unresolved, so the account is said once more -
        // now there is something in the list for the drop-down to match it against.
        Selected?.RefreshSelectedAccount();
    }

    /// <summary>What the picker says underneath itself about how much it is showing.</summary>
    public string AccountsFound =>
        _chart.Count == 0
            ? "Plan kont nie został wczytany."
            : AccountOptions.Count >= AccountsShown
                ? $"Pokazuję {AccountsShown} z {_chart.Count} kont – wpisz fragment numeru lub nazwy."
                : $"{AccountOptions.Count} z {_chart.Count} kont.";

    /// <summary>
    /// The registers visible in the queue - the ones named in <c>appsettings.json</c>, narrowed
    /// down to what the signed-in ERP operator may see. The card ones enter unticked.
    /// </summary>
    public ObservableCollection<RegisterOption> RegisterFilters { get; } = [];

    /// <summary>
    /// Confidence filter entries, most certain first. The code beside the label only colours the
    /// badge - filtering runs on the label, the same one visible on the list. All are ticked by
    /// default: confidence is narrowed down when somebody wants it narrowed.
    /// </summary>
    public IReadOnlyList<ConfidenceOption> ConfidenceFilters { get; } =
    [
        new("pewne", "High"),
        new("do akceptacji", "Medium"),
        new("słabe", "Low"),
        new("brak dopasowania", string.Empty),
    ];

    /// <summary>
    /// The directions shown on the list. Money in by default - that is what the accounting team
    /// works through, and what the queue is built around.
    /// </summary>
    public IReadOnlyList<DirectionOption> DirectionFilters { get; } =
    [
        new("wpłaty", isIncoming: true, selected: true),
        new("wypłaty", isIncoming: false, selected: false),
    ];

    /// <summary>
    /// The settlement states shown on the list. By default the ones with work in them: untouched
    /// transfers and those settled only in part. Fully settled ones and those flagged "nie
    /// rozliczaj" are there to be looked at - and, for the flagged ones, to be un-flagged.
    /// </summary>
    public IReadOnlyList<SettlementFilterOption> SettlementFilters { get; } =
        SettlementFilterOption.ForQueue();

    /// <summary>
    /// Which side of the ledger is shown among the contractor's open items. Receivables only to
    /// begin with - that is what an incoming transfer settles.
    /// </summary>
    public IReadOnlyList<DocumentSideOption> DocumentSideFilters { get; } =
    [
        new("należności", isLiability: false, selected: true),
        new("zobowiązania", isLiability: true, selected: true),
    ];

    /// <summary>
    /// The settlement state of the contractor's open items - the same filter as over the queue,
    /// reading the same words. Items ERP is flagged not to settle start hidden: somebody has
    /// already decided they are not going to be pursued.
    /// </summary>
    public IReadOnlyList<SettlementFilterOption> DocumentStateFilters { get; } =
        SettlementFilterOption.ForDocuments();

    public string DocumentSideFilterSummary => FilterOption.Summarize(DocumentSideFilters);

    public string DocumentStateFilterSummary => FilterOption.Summarize(DocumentStateFilters);

    /// <summary>
    /// The document list as the grid shows it - the selected transfer's items put through the two
    /// filters above.
    /// </summary>
    /// <remarks>
    /// A fresh view per transfer, because each holds its own collection. Nothing is cached: the
    /// list is at most a few hundred rows and the alternative is a dictionary of views to keep in
    /// step with the queue.
    /// </remarks>
    public ICollectionView? DocumentsView
    {
        get => _documentsView;
        private set => Set(ref _documentsView, value);
    }

    private ICollectionView? _documentsView;

    /// <summary>
    /// Whether a document belongs on the list.
    /// </summary>
    /// <remarks>
    /// A ticked document always does, whatever is filtered out. Its amount is in the totals under
    /// the list and it is about to be settled - hiding it would leave the sums unexplained, and a
    /// correction the service itself matched is exactly the liability the default filter drops.
    /// </remarks>
    private bool ShowsDocument(DocumentItem document) =>
        document.IsSelected
        || (DocumentSideFilters.Any(f => f.IsSelected && f.IsLiability == document.Row.IsLiability)
            && DocumentStateFilters.Any(f => f.IsSelected && f.State == document.SettlementState));

    private void BuildDocumentsView(PaymentItem? item)
    {
        if (item is null)
        {
            DocumentsView = null;
            return;
        }

        var view = new CollectionViewSource { Source = item.Documents }.View;
        view.Filter = o => ShowsDocument((DocumentItem)o);
        DocumentsView = view;
    }

    private void RefreshDocumentsView() => DocumentsView?.Refresh();

    // The text on the collapsed filters. The same rule computes them all, so they differ only
    // in the collection they summarise.
    public string SettlementFilterSummary => FilterOption.Summarize(SettlementFilters);

    public string ConfidenceFilterSummary => FilterOption.Summarize(ConfidenceFilters);

    public string DirectionFilterSummary => FilterOption.Summarize(DirectionFilters);

    public string RegisterFilterSummary => FilterOption.Summarize(RegisterFilters);

    /// <summary>
    /// Whether the filter is asking for entries that are finished business.
    /// </summary>
    /// <remarks>
    /// Both states start unticked, so the usual answer is no and the queue fetches a twentieth of
    /// the rows. Ticking either is what makes the application go and get the rest.
    /// </remarks>
    private bool NeedsHistory => SettlementFilters.Any(
        f => f.IsSelected && f.State is SettlementState.Settled or SettlementState.DoNotSettle);

    /// <summary>Whether what is loaded now includes that history.</summary>
    private bool _historyLoaded;

    /// <summary>
    /// The settlement filter is watched apart from the others: ticking a state that is not in
    /// memory has to fetch it before the list can show it.
    /// </summary>
    private void WatchSettlement(FilterOption option)
    {
        option.PropertyChanged += (_, _) =>
        {
            Raise(nameof(SettlementFilterSummary));

            if (NeedsHistory && !_historyLoaded && !IsBusy)
            {
                _ = LoadAsync();
                return;
            }

            View.Refresh();
            Raise(nameof(QueueCount));
            Raise(nameof(QueueTotal));
        };
    }

    /// <summary>Refreshes the document list when one of its own filters is ticked.</summary>
    private void WatchDocuments(FilterOption option, string summaryProperty)
    {
        option.PropertyChanged += (_, _) =>
        {
            RefreshDocumentsView();
            Raise(summaryProperty);
        };
    }

    /// <summary>Header of the counterparty column - it depends on the directions chosen.</summary>
    public string PartyHeader
    {
        get
        {
            var incoming = DirectionFilters.Any(d => d.IsSelected && d.IsIncoming);
            var outgoing = DirectionFilters.Any(d => d.IsSelected && !d.IsIncoming);

            if (incoming && !outgoing) return "Nadawca";
            if (outgoing && !incoming) return "Odbiorca";

            return "Nadawca / odbiorca";
        }
    }

    public IReadOnlyList<PaymentItem> Checked => [.. Payments.Where(p => p.IsChecked)];

    public int CheckedCount => Checked.Count;

    /// <summary>Total of the ticked transfers - settling in bulk requires seeing the whole.</summary>
    public decimal CheckedTotal => Checked.Sum(p => p.Remaining);

    public decimal CheckedAllocated => Checked.Sum(p => p.SelectedNet);

    public decimal CheckedDifference => CheckedTotal - CheckedAllocated;

    public bool HasChecked => CheckedCount > 0;

    public PaymentItem? Selected
    {
        get => _selected;
        set
        {
            var previous = _selected;

            if (!Set(ref _selected, value)) return;

            // A preview left on the row being left would outlive it: the picker would go on
            // showing somebody else's account beside the contractor that is actually on the entry.
            if (previous is not null) _ = PreviewAccountsAsync(previous, null);

            Raise(nameof(HasSelection));

            // The search box is emptied along with the hits. Leaving the text behind over an empty
            // list meant retyping the same name found nothing - the query had not changed, so no
            // search ran - and the caption claimed there was nothing to find.
            ContractorQuery = string.Empty;
            ContractorHits.Clear();

            // The drop-down starts on the contractor the transfer already has, so the closed field
            // reads the same as the column beside it. Assigned to the field: going through the
            // property would preview accounts we are about to load anyway.
            _contractorPick = value?.CurrentContractor;
            Raise(nameof(ContractorPick));
            RebuildContractorOptions();
            HideToast();
            // Today's statement appears on disk during the day, so the archive is asked again on
            // entering the row rather than only when the queue was loaded.
            value?.RefreshSourceDocument(_documents.Find);

            // A filter left over from the previous transfer would hide the new contractor's own
            // accounts, which are the ones the operator wants first.
            _accountFilter = string.Empty;
            Raise(nameof(AccountFilter));
            RebuildAccountOptions();

            BuildDocumentsView(value);

            _ = LoadDocumentsAsync(value);
            RefreshCommands();
        }
    }

    public bool HasSelection => Selected is not null;

    /// <summary>
    /// Asking for confirmation. It agrees by default - the application supplies the window so that
    /// the view model does not depend on <c>MessageBox</c>.
    /// </summary>
    public Func<string, bool> Confirm { get; set; } = _ => true;

    /// <summary>
    /// Asking for the details of a bank absent from the ERP register. Returns null when the
    /// operator gave up.
    /// </summary>
    /// <remarks>
    /// The parameters are the account number, the country code and the clearing code - as much as
    /// we could work out ourselves, so that the operator does not copy it off the screen.
    /// </remarks>
    public Func<BankPrompt, BankDetails?> AskForBank { get; set; } = _ => null;

    // -------------------------------------------------------------- filters ---

    public string Search
    {
        get => _search;
        set { if (Set(ref _search, value)) View.Refresh(); }
    }

    /// <summary>The earliest day transfers are shown from.</summary>
    public DateTime From
    {
        get => _from;
        set => Set(ref _from, value);
    }

    /// <summary>
    /// What is typed into the contractor box. Typing searches by itself from three characters on.
    /// </summary>
    /// <remarks>
    /// Three, not two: two characters match thousands of cards on this register and the list that
    /// comes back is no help. Below that the previous hits are cleared rather than left standing -
    /// a list that no longer answers what is in the box invites choosing from it.
    /// </remarks>
    /// <summary>
    /// The contractor highlighted in the hit list.
    /// </summary>
    /// <remarks>
    /// Picking one is already a choice, so the account picker follows it at once - the accounts on
    /// offer are that contractor's, and the head of the list is the one the settlement would use.
    /// Nothing reaches ERP until the button is pressed: this is what the swap is going to look
    /// like, shown before it happens, so the account can be corrected in the same breath.
    ///
    /// Dropping the highlight puts the transfer's own contractor back on the picker.
    /// </remarks>
    /// <summary>
    /// What the contractor drop-down offers: the one in force, then whatever the search found.
    /// </summary>
    /// <remarks>
    /// The one in force and the one picked are both kept in the list whatever is typed, for the
    /// same reason the account picker keeps its own: a drop-down cannot show a selection that is
    /// not among its entries, and emptying the search box would otherwise blank the field.
    /// </remarks>
    public IReadOnlyList<ContractorRow> ContractorOptions
    {
        get => _contractorOptions;
        private set => Set(ref _contractorOptions, value);
    }

    private IReadOnlyList<ContractorRow> _contractorOptions = [];

    private void RebuildContractorOptions()
    {
        var chosen = new List<ContractorRow>();
        var taken = new HashSet<int>();

        void Add(ContractorRow? row)
        {
            if (row is null || !taken.Add(row.Id)) return;
            chosen.Add(row);
        }

        Add(ContractorPick);
        Add(Selected?.CurrentContractor);
        foreach (var hit in ContractorHits) Add(hit);

        ContractorOptions = chosen;
    }

    public ContractorRow? ContractorPick
    {
        get => _contractorPick;
        set
        {
            if (!Set(ref _contractorPick, value)) return;

            RebuildContractorOptions();
            _ = PreviewAccountsAsync(Selected, value?.Id);
            RefreshCommands();
        }
    }

    private ContractorRow? _contractorPick;

    /// <summary>
    /// Puts the accounts of the given contractor on the picker, without touching ERP.
    /// </summary>
    /// <param name="contractorId">
    /// Null means "no pick": the transfer's own contractor, which is how a preview is undone.
    /// </param>
    private async Task PreviewAccountsAsync(PaymentItem? item, int? contractorId)
    {
        if (item is null) return;

        var target = contractorId ?? item.EffectiveContractorId;
        if (item.AccountOptionsFor == target)
        {
            if (ReferenceEquals(item, Selected)) RebuildAccountOptions();
            return;
        }

        try
        {
            var accounts = target == 0 ? [] : await _repository.GetContractorAccountsAsync(target);
            item.SetAccountOptions(accounts, target);

            if (ReferenceEquals(item, Selected)) RebuildAccountOptions();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read the accounts of contractor {Contractor}.", target);
        }
    }

    public string ContractorQuery
    {
        get => _contractorQuery;
        set
        {
            if (!Set(ref _contractorQuery, value)) return;

            _contractorSearch?.Cancel();
            _contractorSearch = new CancellationTokenSource();

            Raise(nameof(ContractorHitsSummary));

            if (value.Trim().Length < ContractorQueryMinimum)
            {
                ContractorHits.Clear();
                RebuildContractorOptions();
                return;
            }

            _ = FindContractorsAsync(_contractorSearch.Token);
        }
    }

    /// <summary>How much has to be typed before the register is searched.</summary>
    private const int ContractorQueryMinimum = 3;

    // --------------------------------------------------------------- state ---

    public bool IsBusy
    {
        get => _isBusy;
        private set { if (Set(ref _isBusy, value)) RefreshCommands(); }
    }

    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    // The timer is put on the application's dispatcher explicitly: were the view model ever
    // created off the UI thread, a parameterless DispatcherTimer would land on a dispatcher that
    // never ticks and the toast would stay on screen forever.
    private readonly DispatcherTimer _toastTimer =
        new(DispatcherPriority.Normal, System.Windows.Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher);

    private bool _toastVisible;
    private ToastKind _toastKind;

    /// <summary>Whether the message is visible. A bar at the bottom went unnoticed.</summary>
    public bool ToastVisible
    {
        get => _toastVisible;
        private set => Set(ref _toastVisible, value);
    }

    /// <summary>Severity of the message - the toast's colour follows from it.</summary>
    public ToastKind ToastKind
    {
        get => _toastKind;
        private set { if (Set(ref _toastKind, value)) Raise(nameof(ToastGlyph)); }
    }

    /// <summary>The glyph in the circle on the left. Colour comes from the style; only the shape is here.</summary>
    public string ToastGlyph => ToastKind switch
    {
        ToastKind.Success => "✓",
        ToastKind.Warning => "!",
        ToastKind.Error => "✕",
        _ => "i",
    };

    /// <summary>
    /// How long a message stays up. An error has to be read and understood in time; confirmation
    /// of a success need only be noticed.
    /// </summary>
    private static TimeSpan LifetimeOf(ToastKind kind) => kind switch
    {
        ToastKind.Error => TimeSpan.FromSeconds(14),
        ToastKind.Warning => TimeSpan.FromSeconds(9),
        _ => TimeSpan.FromSeconds(5),
    };

    /// <summary>Shows a message above the list and puts it out after a moment.</summary>
    private void Notify(string message, ToastKind kind = ToastKind.Info)
    {
        if (message.Length == 0)
        {
            HideToast();
            return;
        }

        Status = message;
        ToastKind = kind;
        ToastVisible = true;

        _toastTimer.Stop();
        _toastTimer.Interval = LifetimeOf(kind);
        _toastTimer.Start();
    }

    /// <summary>Hides the message - also called from the cross on the toast.</summary>
    public void HideToast()
    {
        _toastTimer.Stop();
        ToastVisible = false;
        Status = string.Empty;
    }

    /// <summary>
    /// The caption above the queue: the database, and the ERP operator once they have signed in.
    /// </summary>
    public string Header => Operator is null ? _header : $"{_header}  ·  {Operator.Label}";

    // The counters in the header show what is visible on the list rather than everything loaded
    // from the database - otherwise the filtered-out card registers would inflate the queue with
    // operations nobody can see.
    public int QueueCount => View.Cast<PaymentItem>().Count();

    public decimal QueueTotal => View.Cast<PaymentItem>().Sum(p => p.Remaining);

    /// <summary>
    /// Hooks up a filter entry: every toggle refreshes the list, the summary on the button and the
    /// counters in the header.
    /// </summary>
    private void Watch(FilterOption option, string summaryProperty)
    {
        option.PropertyChanged += (_, _) =>
        {
            View.Refresh();
            Raise(summaryProperty);
            Raise(nameof(QueueCount));
            Raise(nameof(QueueTotal));
        };
    }

    // -------------------------------------------------------------- commands ---

    public RelayCommand RefreshCommand { get; }
    public RelayCommand OpenSourceDocumentCommand { get; }
    public RelayCommand SettleCheckedCommand { get; }
    public RelayCommand SettleCurrentCommand { get; }
    public RelayCommand RevokeCommand { get; }
    public RelayCommand UseContractorCommand { get; }
    public RelayCommand AssignAccountCommand { get; }
    public RelayCommand DoNotSettleCommand { get; }

    // --------------------------------------------------------------- loading ---

    /// <summary>Waits for sign-in through the Comarch ERP XL window, then loads the queue.</summary>
    public async Task StartAsync()
    {
        Notify("Czekam na zalogowanie w oknie Comarch ERP XL…");

        using var start = TimedOperation.Start(_logger, "Signing in through the Comarch ERP XL window");

        try
        {
            await _worker.Ready;
            start.Result("signed in");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Signing in to ERP failed.");
            Notify($"Nie zalogowano do ERP: {ex.Message}", ToastKind.Error);
            LoginFailed?.Invoke(ex.Message);
            return;
        }

        await ApplyOperatorRightsAsync();
        await LoadAsync();
    }

    /// <summary>Raised when sign-in failed - the application then has nothing to do.</summary>
    public event Action<string>? LoginFailed;

    /// <summary>
    /// Opens the archived document behind the selected entry - the bank statement, or the courier
    /// payout report a cash on delivery came out of.
    /// </summary>
    /// <remarks>
    /// Handed to the shell rather than shown in a window of our own: a PDF, an old Excel workbook
    /// and a CSV have nothing in common but the fact that the accountant already has a program for
    /// each of them.
    /// </remarks>
    private void OpenSourceDocument()
    {
        if (Selected?.SourceDocument is not { Length: > 0 } path) return;

        _logger.LogInformation("Opening the source document {Path}.", path);

        try
        {
            using var process = System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not open the file {Path}.", path);
            Notify($"Nie udało się otworzyć pliku: {ex.Message}", ToastKind.Error);
        }
    }

    /// <summary>
    /// Narrows the register filter down to what the signed-in ERP operator is entitled to.
    /// </summary>
    /// <remarks>
    /// Rights come from ERP, not from us: the session id returned by <c>XLLogin</c> identifies the
    /// operator and their centre in <c>CDN.Sesje</c>, and the centre is what carries rights to
    /// registers. An operator in IT reaches seven of the fourteen; one in an accounting centre
    /// reaches all of them, the card registers included.
    ///
    /// A failure here does not stop the application: it can only widen what is shown, never post
    /// anything, and ERP still guards every operation the operator actually performs.
    /// </remarks>
    private async Task ApplyOperatorRightsAsync()
    {
        try
        {
            var sessionId = await _worker.RunAsync(session => session.Id);
            var access = await _repository.GetOperatorAccessAsync(sessionId);

            Operator = access;

            if (!access.IsRestricted) return;

            foreach (var option in RegisterFilters.Where(r => !access.MaySee(r.Series)).ToList())
                RegisterFilters.Remove(option);

            // Only to the log. The accountant is not told which registers their centre does not
            // reach - they cannot do anything about it from here, and it is not their business
            // that the register exists at all.
            _logger.LogInformation(
                "Operator {Operator} in centre {Centre} is entitled to registers: {Registers}.",
                access.Ident, access.CentreName, string.Join(", ", access.Registers));

            Raise(nameof(RegisterFilterSummary));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not work out which registers the operator is entitled to.");
        }
    }

    /// <summary>The signed-in ERP operator, once sign-in has gone through.</summary>
    public OperatorAccess? Operator
    {
        get => _operator;
        private set
        {
            if (Set(ref _operator, value)) Raise(nameof(Header));
        }
    }

    /// <summary>
    /// Loads the queue and settles on the row after the given one.
    /// </summary>
    /// <remarks>
    /// After a settlement the accountant carries on down the list - putting them back at the top,
    /// or leaving nothing selected, forced them to find their place again.
    /// </remarks>
    private async Task ReloadAndAdvanceAsync(long afterPaymentId)
    {
        var order = View.Cast<PaymentItem>().Select(p => p.PaymentId).ToList();
        var index = order.IndexOf(afterPaymentId);

        await LoadAsync();

        var remaining = View.Cast<PaymentItem>().ToList();
        if (remaining.Count == 0) return;

        // A settled row usually disappears, so the next one now stands in its place.
        Selected = remaining[Math.Clamp(index, 0, remaining.Count - 1)];
    }

    public async Task LoadAsync()
    {
        using var loading = TimedOperation.Start(_logger, "Loading the queue");

        try
        {
            IsBusy = true;

            var from = From.Date;

            // The three queries that do not depend on one another go together. The chart is read
            // once per session: the picker offers all of it, and per row it would be a megabyte and
            // a half of text every time somebody clicked.
            // Settled entries and those flagged "nie rozliczaj" are fetched only when the filter
            // asks for them - they are eighteen thousand rows against five hundred, and twenty
            // times the wait.
            var withHistory = NeedsHistory;
            var queueQuery = _repository.GetQueueAsync(from, withHistory);
            var suggestionQuery = _repository.GetSuggestionsAsync(from);
            var chartQuery = _chart.Count > 0
                ? Task.FromResult(_chart)
                : _repository.GetChartOfAccountsAsync();

            await Task.WhenAll(queueQuery, suggestionQuery, chartQuery);

            var queue = await queueQuery;
            _chart = await chartQuery;
            _historyLoaded = withHistory;

            var suggestions = (await suggestionQuery)
                .GroupBy(s => s.PaymentId)
                .ToDictionary(g => g.Key, g => (IReadOnlyList<SuggestionRow>)[.. g]);

            // The accounts of every contractor on the list, in one query - the same reason the
            // hints are fetched this way. Fetched per row as the operator clicked through, the
            // column could only ever fill in for rows already visited, and the picker stayed shut
            // until the query came back.
            var accounts = await _repository.GetContractorAccountsAsync(
                [.. queue.Select(r => r.ContractorId)]);

            // A statement written since the last load has to be found, so what the archive said
            // before is dropped rather than carried over.
            _documents.Forget();

            // Built away from the interface thread. Eighteen thousand rows, each asking the
            // archive whether its statement is on disk, is a second or more of straight-line work,
            // and on the thread that draws the window it stops the window: the progress bar freezes
            // mid-animation and the application looks hung just as it is finishing. Nothing here
            // touches a control - the items are not bound to anything until they are handed over
            // below.
            var built = await Task.Run(() =>
            {
                var list = new List<PaymentItem>(queue.Count);

                foreach (var row in queue)
                {
                    var item = new PaymentItem(row) { SourceDocument = _documents.Find(row) };
                    if (suggestions.TryGetValue(row.PaymentId, out var hits)) item.Suggestions = hits;

                    if (accounts.TryGetValue(row.ContractorId, out var forContractor))
                    {
                        item.SetAccountOptions(forContractor, row.ContractorId);
                    }

                    list.Add(item);
                }

                return list;
            });

            foreach (var item in built) item.PropertyChanged += OnPaymentChanged;

            foreach (var old in Payments) old.PropertyChanged -= OnPaymentChanged;
            Selected = null;

            // One change for the whole list. Added row by row, the view re-ran its filter over
            // everything added so far and told the grid about each one - that, not the queries,
            // was most of the wait on a sixty-day window.
            Payments.Reset(built);

            loading.Result(
                $"{queue.Count} entries from {From:yyyy-MM-dd}, {suggestions.Count} with the engine's hints, "
                + $"{RegisterFilters.Count} registers on the filter, "
                + $"{accounts.Count} contractors with accounts, "
                + $"{_chart.Count} accounts in the plan");

            // Counters only after filtering - they count what is visible on the list. Leaving the
            // deferral above already refreshed the view, so no second pass is needed.
            Raise(nameof(QueueCount));
            Raise(nameof(QueueTotal));
            RaiseCheckedTotals();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not load the queue.");
            Notify($"Błąd wczytywania: {ex.Message}", ToastKind.Error);
        }
        finally
        {
            IsBusy = false;
        }

        // A state ticked while this load was already running found the application busy and was
        // left to the list as it stood. Now that it is free, fetch what that tick asked for.
        if (NeedsHistory && !_historyLoaded) await LoadAsync();
    }

    /// <summary>
    /// A contractor's documents are loaded only on entering the row - for the whole queue that
    /// would be several hundred queries, and the accountant looks at transfers one at a time anyway.
    /// </summary>
    private async Task LoadDocumentsAsync(PaymentItem? item)
    {
        if (item is null) return;

        try
        {
            IsBusy = true;

            if (!item.DocumentsLoaded)
            {
                await FillDocumentsAsync(item, item.Row.ContractorId);
                item.DocumentsLoaded = true;
            }

            await RefreshAccountKnownAsync(item);
            await LoadOverpaymentAsync(item);
            RebuildAccountOptions();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not load the contractor's documents.");
            Notify($"Błąd wczytywania dokumentów: {ex.Message}", ToastKind.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Reads what the contractor has paid that nobody has allocated.
    /// </summary>
    /// <remarks>
    /// A failure is not reported: it is a figure beside the totals, not something the settlement
    /// depends on, and a toast about it would interrupt work that is not blocked.
    /// </remarks>
    private async Task LoadOverpaymentAsync(PaymentItem item)
    {
        var contractorId = item.EffectiveContractorId;

        if (contractorId == 0)
        {
            item.Overpayments = [];
            return;
        }

        try
        {
            item.Overpayments = await _repository.GetOverpaymentsAsync(contractorId, item.Row.ErpEntryId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read the overpayment of contractor {Contractor}.", contractorId);
            item.Overpayments = [];
        }
    }

    /// <summary>
    /// Checks in the ERP register whether the named contractor already holds this account.
    /// </summary>
    /// <remarks>
    /// The "add account" button depends on this. The flag in our own table only says whether the
    /// account is what identified the contractor - once somebody added it by hand in ERP later, or
    /// the operator swapped the contractor, the flag lies and the button tempts for no reason.
    /// </remarks>
    private async Task RefreshAccountKnownAsync(PaymentItem item)
    {
        var account = item.Row.PayerAccount;
        var contractorId = item.EffectiveContractorId;

        if (account.Length == 0 || contractorId == 0)
        {
            item.SetAccountKnown(false);
        }
        else
        {
            var withAccount = await _repository.ContractorsWithAccountAsync([(contractorId, account)]);
            item.SetAccountKnown(withAccount.Contains((contractorId, account)));
        }

        RefreshCommands();
    }

    private async Task FillDocumentsAsync(PaymentItem item, int contractorId)
    {
        // Outgoing transfers have no contractor from the engine - we try to find one from the
        // recipient's account.
        if (contractorId == 0)
        {
            var byAccount = await _repository.FindContractorByAccountAsync(item.Row.PayerAccount);

            if (byAccount is not null)
            {
                contractorId = byAccount.Id;
                item.UseContractor(byAccount.Id, byAccount.Acronym, byAccount.Display);
                Notify(
                    $"Kontrahenta {byAccount.Acronym} wskazał rachunek bankowy – sprawdź, czy się zgadza.",
                    ToastKind.Warning);
            }
        }

        var documents = contractorId == 0
            ? []
            : await _repository.GetOpenDocumentsAsync(contractorId);

        Fill(item, documents);
    }

    private void Fill(PaymentItem item, IReadOnlyList<DocumentRow> documents)
    {
        foreach (var existing in item.Documents)
        {
            existing.SelectionChanged -= item.RaiseTotals;
            existing.SelectionChanged -= RefreshDocumentsView;
            existing.DoNotSettleChanged -= OnDoNotSettleChanged;
        }

        item.Documents.Clear();

        var suggested = item.Suggestions
            .Select(s => (s.DocType, s.DocId, s.DocLp))
            .ToHashSet();

        foreach (var row in documents)
        {
            var isSuggested = suggested.Contains((row.DocType, row.DocId, row.DocLp));
            var document = new DocumentItem(row, isSuggested, item.Row.IsIncoming);

            // A tick can bring a document past the filter, and clearing one can take it away
            // again - the list has to be re-run either way.
            document.SelectionChanged += item.RaiseTotals;
            document.SelectionChanged += RefreshDocumentsView;
            document.DoNotSettleChanged += OnDoNotSettleChanged;
            item.Documents.Add(document);
        }

        // Hints naming documents outside the loaded list (another contractor, or a document
        // already settled) simply have nothing to tick - which is right, because they are stale.
        item.RaiseTotals();
    }

    // ---------------------------------------------------------- contractor ---

    /// <summary>
    /// Searches the register for what is in the box - after a short pause, so that a word being
    /// typed does not send a query per keystroke.
    /// </summary>
    private async Task FindContractorsAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(250), token);

            var query = ContractorQuery.Trim();
            if (query.Length < ContractorQueryMinimum) return;

            var hits = await _repository.FindContractorsAsync(query, token);
            if (token.IsCancellationRequested) return;

            ContractorHits.Clear();
            foreach (var hit in hits) ContractorHits.Add(hit);

            RebuildContractorOptions();
            Raise(nameof(ContractorHitsSummary));
        }
        catch (OperationCanceledException)
        {
            // Another key was pressed - the newer search answers instead.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Contractor search failed.");
            Notify($"Błąd wyszukiwania kontrahenta: {ex.Message}", ToastKind.Error);
        }
    }

    /// <summary>What is written under the hit list, so an empty list is not read as a failure.</summary>
    public string ContractorHitsSummary => ContractorQuery.Trim().Length < ContractorQueryMinimum
        ? $"Wpisz co najmniej {ContractorQueryMinimum} znaki – akronim, nazwę albo NIP."
        : ContractorHits.Count == 0
            ? "Nic nie znaleziono."
            : $"Znaleziono {ContractorHits.Count}.";

    /// <summary>
    /// Puts the chosen contractor on the transfer: their open items are shown and the cash entry
    /// in ERP is booked against them.
    /// </summary>
    /// <remarks>
    /// The entry is written now rather than at settlement. An accountant swaps a contractor
    /// exactly when the one on the entry is wrong, and an entry left on the wrong party until
    /// something is settled cannot be found in ERP under either name in the meantime. The account
    /// follows the party - it is chosen from that contractor's own, and the statement picks their
    /// commonest when nothing is chosen.
    /// </remarks>
    private async Task UseContractorAsync(ContractorRow? contractor)
    {
        if (contractor is null || Selected is null) return;

        var item = Selected;

        try
        {
            IsBusy = true;
            item.UseContractor(contractor.Id, contractor.Acronym, contractor.Display, byOperator: true);

            await FillDocumentsAsync(item, contractor.Id);
            item.DocumentsLoaded = true;
            await RefreshAccountKnownAsync(item);

            // Loads the accounts only if the preview has not already done it - so an account the
            // operator corrected on the picker before pressing the button is the one that is used,
            // rather than being reset to the contractor's commonest.
            await LoadAccountOptionsAsync(item);
            await LoadOverpaymentAsync(item);

            var account = item.SelectedAccount;
            await Task.Run(() => _store.SetEntryContractor(
                item.Row.PaymentId, item.Row.ErpEntryId, contractor.Id, account));

            RefreshDocumentsView();

            Notify($"Zapis kasowy przepisany na kontrahenta {contractor.Acronym}"
                   + (string.IsNullOrEmpty(account) ? "." : $", konto {account}."));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not swap the contractor.");
            Notify($"Błąd podmiany kontrahenta: {ex.Message}", ToastKind.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Carries the "nie rozliczaj" box on a document payment through to ERP.
    /// </summary>
    /// <remarks>
    /// Written straight away rather than gathered up for a save button: it is one flag on one
    /// payment, and the accountants set it while they are reading the list. If the write fails the
    /// box goes back to what ERP still holds, so the screen never claims something the register
    /// does not.
    /// </remarks>
    private async void OnDoNotSettleChanged(DocumentItem document)
    {
        var wanted = document.DoNotSettle;

        try
        {
            await Task.Run(() => _store.SetPaymentDoNotSettle(
                document.Row.DocType, document.Row.DocId, document.Row.DocLp, wanted));

            RefreshDocumentsView();
            Notify(wanted
                ? $"{document.DocNumber}: ustawiono „nie rozliczaj”."
                : $"{document.DocNumber}: zdjęto „nie rozliczaj”.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not change the do-not-settle flag on {Document}.", document.DocNumber);
            document.ResetDoNotSettle(!wanted);
            Notify($"Nie udało się zmienić znacznika: {ex.Message}", ToastKind.Error);
        }
    }

    // ----------------------------------------------------------- settlement ---

    /// <summary>
    /// The transfers that can be marked as not subject to settlement: the ticked ones, or the
    /// current one when nothing is ticked.
    /// </summary>
    /// <remarks>
    /// A transfer already settled in part drops out. The flag means "this entry is not an open
    /// item", and once somebody has matched part of the amount to something that is untrue - the
    /// settlement has to be revoked first.
    /// </remarks>
    private IReadOnlyList<PaymentItem> DoNotSettleTargets()
    {
        IReadOnlyList<PaymentItem> items = CheckedCount > 0
            ? Checked
            : Selected is null ? [] : [Selected];

        return [.. items.Where(i => !i.HasSettlements)];
    }

    /// <summary>
    /// Which way the one button goes: on unless every transfer it would act on already carries the
    /// flag, and then it takes it off.
    /// </summary>
    /// <remarks>
    /// One button rather than two. Setting and clearing are the same decision seen from opposite
    /// sides, and a second button would sit greyed out most of the time - the accountant would
    /// still have to read which one is live. This way the caption says what will happen.
    ///
    /// A mixed selection sets rather than clears: ticking a dozen transfers of which one is
    /// already flagged means "flag these", and the flagged one is simply left as it is.
    /// </remarks>
    private bool DoNotSettleSets() => DoNotSettleTargets().Any(i => !i.IsDoNotSettle);

    /// <summary>The caption on that button.</summary>
    public string DoNotSettleLabel =>
        DoNotSettleSets() ? "Oznacz: nie rozliczaj" : "Zdejmij: nie rozliczaj";

    /// <summary>What its tooltip explains - the two directions read differently.</summary>
    public string DoNotSettleHint => DoNotSettleSets()
        ? "Ustawia w ERP znacznik „nie podlega rozliczeniu” – dla prowizji, zwrotów i przeksięgowań. "
          + "Działa na zaznaczonych przelewach, a gdy nic nie jest zaznaczone – na bieżącym."
        : "Zdejmuje znacznik „nie podlega rozliczeniu” i wraca przelew do kolejki. "
          + "Działa na zaznaczonych przelewach, a gdy nic nie jest zaznaczone – na bieżącym.";

    /// <summary>
    /// Marks the given transfers as not subject to settlement - one at a time or in bulk.
    /// </summary>
    private async Task MarkDoNotSettleAsync()
    {
        var sets = DoNotSettleSets();

        // Clearing touches only the flagged ones; setting only those without the flag. Either way
        // the transfers already in the wanted state are left alone rather than written twice.
        var items = DoNotSettleTargets().Where(i => i.IsDoNotSettle != sets).ToList();
        if (items.Count == 0) return;

        var considered = CheckedCount > 0 ? CheckedCount : Selected is null ? 0 : 1;
        var skipped = considered - DoNotSettleTargets().Count;

        var question = sets
            ? items.Count == 1
                ? "Oznaczyć ten przelew w ERP jako niepodlegający rozliczeniu?"
                : $"Oznaczyć {items.Count} przelewów w ERP jako niepodlegające rozliczeniu?"
            : items.Count == 1
                ? "Zdjąć z tego przelewu znacznik „nie podlega rozliczeniu” i wrócić go do kolejki?"
                : $"Zdjąć znacznik „nie podlega rozliczeniu” z {items.Count} przelewów?";

        if (skipped > 0)
        {
            question += $" Pomijam {skipped} częściowo rozliczonych – tym najpierw trzeba cofnąć rozliczenie.";
        }

        if (!Confirm(question)) return;

        var plan = items.Select(i => (i.PaymentId, i.Row.ErpEntryId)).ToList();

        try
        {
            IsBusy = true;
            await Task.Run(() =>
            {
                foreach (var (paymentId, entryId) in plan)
                {
                    if (sets) _store.MarkDoNotSettle(paymentId, entryId, User);
                    else _store.ClearDoNotSettle(paymentId, entryId, User);
                }
            });

            await ReloadAndAdvanceAsync(plan[^1].PaymentId);

            var done = sets
                ? $"Oznaczono {plan.Count} przelewów jako niepodlegające rozliczeniu."
                : $"Zdjęto znacznik z {plan.Count} przelewów – wróciły do kolejki.";

            Notify(
                skipped == 0 ? done : $"{done} Pominięto {skipped} częściowo rozliczonych.",
                skipped == 0 ? ToastKind.Success : ToastKind.Warning);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Changing the do-not-settle flag failed.");
            Notify($"Błąd zmiany znacznika: {ex.Message}", ToastKind.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task SettleAsync(IReadOnlyList<PaymentItem> items)
    {
        // A fully settled entry has nothing left to settle - it is on the list only because the
        // state filter let it through, and clicking Settle would end in an error from ERP.
        var closed = items.Count(i => i.SettlementState == SettlementState.Settled);

        // A transfer flagged "nie rozliczaj" is not an open item either - the flag has to come
        // off first, which is what the button beside this one does.
        var flagged = items.Count(i => i.IsDoNotSettle);
        var pending = items
            .Where(i => i.SettlementState is not (SettlementState.Settled or SettlementState.DoNotSettle)
                        && i.Selected.Count > 0)
            .ToList();

        if (pending.Count == 0)
        {
            Notify(
                closed > 0 ? "Zaznaczone przelewy są już rozliczone w całości."
                : flagged > 0 ? "Zaznaczone przelewy mają w ERP znacznik „nie rozliczaj” – najpierw go zdejmij."
                : "Nie zaznaczono żadnego dokumentu do rozliczenia.",
                ToastKind.Warning);
            return;
        }

        // ERP will not settle an entry against a document in another currency. Better to say so
        // at once than to send XL a set that will come back with an error anyway.
        var mismatch = pending.FirstOrDefault(i => i.WrongCurrency.Count > 0);

        if (mismatch is not null)
        {
            var documents = string.Join(", ", mismatch.WrongCurrency.Take(3).Select(d => d.DocNumber));
            Notify($"Przelew jest w {mismatch.Currency}, a zaznaczone dokumenty w innej walucie " +
                   $"({documents}). ERP nie rozliczy takiej pary.", ToastKind.Error);
            return;
        }

        var work = pending
            .Select(i => new SettleRequest(
                i.PaymentId,
                i.Row.ErpEntryId,
                i.Remaining,
                i.EffectiveContractorId,
                i.AccountForSettlement,
                [.. i.Selected.Select(d => new SettlementLine(
                    d.Row.DocType, d.Row.DocId, d.Row.DocLp, d.DocNumber, d.SignedRemaining))]))
            .ToList();

        try
        {
            IsBusy = true;

            var results = await _worker.RunAsync(session => RunSettlement(session, work));

            var settled = 0;
            foreach (var item in pending)
            {
                var error = results[item.PaymentId];
                item.LastResult = error ?? "rozliczono";
                if (error is null) settled++;
            }

            var ostatni = pending[^1].PaymentId;
            var udane = pending.Where(i => i.LastResult == "rozliczono").ToList();

            await OfferAccountAssignmentAsync(udane);
            await ReloadAndAdvanceAsync(ostatni);

            Notify(settled == work.Count
                ? $"Rozliczono {settled} przelewów."
                : $"Rozliczono {settled} z {work.Count}. Powód niepowodzenia jest w kolumnie „Ostatnia próba”.",
                settled == work.Count ? ToastKind.Success : ToastKind.Warning);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Settling failed.");
            Notify($"Błąd rozliczania: {ex.Message}", ToastKind.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Offers to add the accounts missing from the cards of the contractors just settled.
    /// </summary>
    /// <remarks>
    /// Done after settling, not before: adding an account is a convenience for the future, not a
    /// condition of settling. An error on one account does not stop the rest - the accountant gets
    /// one summary of what worked, what did not and why.
    /// </remarks>
    private async Task OfferAccountAssignmentAsync(IReadOnlyList<PaymentItem> settled)
    {
        var possible = settled.Where(i => i.CanAssignAccount).ToList();
        if (possible.Count == 0) return;

        // The flag on the item goes stale, and adding an account that is already there ends in
        // an error from ERP. We ask the register before proposing anything.
        var haveIt = await _repository.ContractorsWithAccountAsync(
            [.. possible.Select(i => (i.EffectiveContractorId, i.Row.PayerAccount))]);

        // A contractor may hold several accounts, so we ask about the pair. Checking by
        // contractor alone lost their second account: since the first was already on the card, the
        // second never reached a proposal and was additionally flagged as known.
        var candidates = possible
            .Where(i => !haveIt.Contains((i.EffectiveContractorId, i.Row.PayerAccount)))
            .ToList();

        foreach (var item in possible.Except(candidates)) item.MarkAccountAssigned();

        if (candidates.Count == 0) return;

        var question = candidates.Count == 1
            ? $"Rachunku {candidates[0].Row.PayerAccount} nie ma w kartotece kontrahenta. Dopisać go?"
            : $"{candidates.Count} rozliczonych przelewów ma rachunek spoza kartoteki kontrahenta. " +
              "Dopisać te rachunki?";

        if (!Confirm(question + " Kolejne przelewy z tych kont serwis rozpozna sam.")) return;

        var plan = new List<(int ContractorId, string Account, string Currency, BankRef? Bank)>();

        foreach (var item in candidates)
        {
            plan.Add((item.EffectiveContractorId, item.Row.PayerAccount, item.Currency,
                await ResolveBankAsync(item)));
        }

        var results = await _worker.RunAsync(session =>
        {
            var accounts = new XlContractorAccounts(_logger);

            return plan
                .Select(x => (x.Account, x.ContractorId, x.Bank,
                    Error: accounts.Assign(
                        session, x.ContractorId, x.Account, x.Currency, x.Bank?.Code, BicOf(x.Bank))))
                .ToList();
        });

        var bankless = await LinkBanksAsync(
            results.Where(r => r.Error is null).Select(r => (r.ContractorId, r.Account, r.Bank)));

        var added = results.Count(r => r.Error is null);
        var failed = results.Where(r => r.Error is not null).ToList();

        var withoutBank = bankless.Count == 0
            ? string.Empty
            : $" Bez banku zostały: {string.Join(", ", bankless.Take(3))} – wskaż bank na karcie w ERP XL.";

        if (failed.Count == 0)
        {
            Notify(
                $"Dopisano {added} rachunków do kartotek kontrahentów.{withoutBank}",
                bankless.Count == 0 ? ToastKind.Success : ToastKind.Warning);
            return;
        }

        var details = string.Join("  ·  ", failed.Take(3).Select(f => $"{f.Account}: {f.Error}"));
        if (failed.Count > 3) details += $"  ·  i {failed.Count - 3} więcej";

        Notify(
            $"Dopisano {added} z {results.Count} rachunków. Nieudane – {details}{withoutBank}",
            ToastKind.Warning);
    }

    private sealed record SettleRequest(
        long PaymentId,
        int EntryId,
        decimal EntryAmount,
        int ContractorId,
        /// <summary>
        /// The account picked in the panel, empty when none was. Empty leaves the choice to the
        /// same rule the service uses - the account this contractor's other entries carry.
        /// </summary>
        string Account,
        IReadOnlyList<SettlementLine> Lines);

    /// <summary>Runs on the XL session's thread - with no await inside.</summary>
    private Dictionary<long, string?> RunSettlement(XlSession session, IReadOnlyList<SettleRequest> work)
    {
        var results = new Dictionary<long, string?>();

        foreach (var request in work)
        {
            var result = _engine.Settle(session, request.EntryId, request.Lines, request.EntryAmount);

            // The status is recorded right after settling rather than at the end of the loop -
            // were the application to die later, ERP would hold settlements we know nothing about.
            if (result.Succeeded)
            {
                // The entry was created before we knew the party - after settling it has to
                // name whoever the open item actually closed with.
                if (request.ContractorId != 0)
                {
                    _store.UpdateEntryContractor(
                        request.PaymentId, request.EntryId, request.ContractorId, request.Account);
                }

                // And it names the invoices it paid instead of the bank's reference - the same as
                // when the service settles, so an entry reads the same whoever closed it.
                _store.UpdateEntryDocumentNumber(
                    request.EntryId, [.. request.Lines.Select(l => l.DocNumber)]);

                _store.MarkSettled(request.PaymentId, User);
            }
            else
            {
                _store.SaveError(request.PaymentId, result.Error!);
            }

            results[request.PaymentId] = result.Error;
        }

        return results;
    }

    private async Task RevokeAsync()
    {
        if (Selected is null) return;

        var paymentId = Selected.PaymentId;
        var entryId = Selected.Row.ErpEntryId;

        if (!Confirm("Usunąć z ERP wszystkie rozliczenia tego zapisu? Przelew wróci do kolejki.")) return;

        try
        {
            IsBusy = true;

            var settlements = await _repository.GetEntrySettlementsAsync(entryId);

            if (settlements.Count == 0)
            {
                Notify("Ten zapis nie ma w ERP żadnych rozliczeń do cofnięcia.", ToastKind.Warning);
                return;
            }

            var error = await _worker.RunAsync(session => RunRevoke(session, paymentId, entryId, settlements));

            await LoadAsync();

            Notify(error ?? $"Cofnięto {settlements.Count} rozliczeń tego przelewu.",
                error is null ? ToastKind.Success : ToastKind.Error);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Revoking the settlement failed.");
            Notify($"Błąd cofania: {ex.Message}", ToastKind.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Runs on the XL session's thread - with no await inside.</summary>
    /// <remarks>
    /// When one revoke fails, the earlier ones are already gone from ERP - and our tables have to
    /// reflect that. Bailing out on an error used to skip writing the state, leaving the transfer
    /// marked as settled although its open items had partly disappeared.
    /// </remarks>
    private string? RunRevoke(
        XlSession session, long paymentId, int entryId, IReadOnlyList<SettlementRow> settlements)
    {
        var revoked = 0;
        string? failure = null;

        foreach (var row in settlements)
        {
            var gid = new SettlementGid(
                XlSettlementEngine.SettlementGidType, row.GidFirma, row.SettlementId, 0);

            var error = _engine.Revoke(session, gid);

            if (error is not null)
            {
                failure = $"{row.DocNumber}: {error}";
                break;
            }

            revoked++;
        }

        // The entry reverts to its pre-swap shape only once something has actually come off.
        if (revoked > 0)
        {
            _store.RestoreEntryContractor(paymentId, entryId);
            _store.MarkUnsettled(paymentId, User);
        }

        return failure is null
            ? null
            : revoked == 0
                ? failure
                : $"Cofnięto {revoked} z {settlements.Count} rozliczeń, potem błąd – {failure}";
    }

    /// <summary>
    /// The code of the bank holding the counterparty's account.
    /// </summary>
    /// <remarks>
    /// The ERP register first - by SWIFT code or by clearing code. A domestic account carries its
    /// clearing code inside the national number, so there it usually ends there. A foreign account
    /// does not, and BNP sends no counterparty bank details in the statement, so when we know
    /// nothing we ask the operator and create the bank from what they give.
    /// </remarks>
    private async Task<BankRef?> ResolveBankAsync(PaymentItem item)
    {
        var account = item.Row.PayerAccount;
        var found = await _repository.FindBankAsync(account, item.Row.BankBic);

        // A card XL will match by itself. That is the case for every domestic account and for
        // any bank that has once been through this route - there is then nothing to ask about.
        if (found is { BindsInXl: true })
        {
            if (item.Row.BankBic.Trim().Length > 0) _bankBics[found.Id] = item.Row.BankBic.Trim();
            return found;
        }

        // What is left is the hard case: either the bank is absent from the register, or it is
        // there but its card holds a clearing code XL will not find in this account number. The
        // length of a bank code depends on the country, so we suggest one and ask a human to
        // confirm.
        var bankCode = IbanParts.BankCode(account);

        // A card to be corrected is described by what is on it. A new bank is not suggested
        // from the register: a name and SWIFT code taken off another account turned out to be a
        // branch, or plainly a different bank, and the operator confirmed them with one click.
        // Only the bank code is computed - it follows from the account number itself and can be
        // verified.
        var known = found is not null ? await _repository.GetBankAsync(found.Id) : null;

        var entered = AskForBank(new BankPrompt(account, bankCode, known, found is not null));

        if (entered is null) return null;

        if (found is not null)
        {
            await Task.Run(() => _store.RepairBankForIban(
                found.Id, entered.BankCode, entered.Name, entered.City, entered.PostalCode,
                entered.Street));

            if (entered.Bic.Length > 0) _bankBics[found.Id] = entered.Bic;
            return found with { BindsInXl = true };
        }

        var name = entered.Name.Length > 0 ? entered.Name : item.Row.BankName;

        var created = await Task.Run(() => _store.CreateBank(
            entered.Bic, name, entered.BankCode, entered.CountryCode, entered.City, entered.PostalCode,
            entered.Street));

        if (created is not null && entered.Bic.Length > 0) _bankBics[created.Id] = entered.Bic;

        return created;
    }

    /// <summary>SWIFT codes of the banks resolved in this session - only to describe an account with.</summary>
    private readonly Dictionary<int, string> _bankBics = [];

    /// <summary>The bank's SWIFT code, worth putting on the account. Empty when unknown.</summary>
    private string BicOf(BankRef? bank) =>
        bank is null ? string.Empty : _bankBics.GetValueOrDefault(bank.Id, string.Empty);

    /// <summary>
    /// Attaches the bank to the accounts XL left without one. Returns those that ended up with no
    /// bank all the same.
    /// </summary>
    /// <remarks>
    /// The account is already in the register, so a failure undoes nothing - the bank is simply
    /// left empty and the accountant has to set it in ERP. It has to be said out loud, because
    /// <c>XLNowyRachunek</c> stays quiet: it reports success even when it attached no bank.
    /// </remarks>
    private async Task<IReadOnlyList<string>> LinkBanksAsync(
        IEnumerable<(int ContractorId, string Account, BankRef? Bank)> assigned)
    {
        var work = assigned.ToList();
        if (work.Count == 0) return [];

        await Task.Run(() =>
        {
            foreach (var x in work.Where(x => x.Bank is not null))
            {
                _store.LinkAccountBank(x.ContractorId, x.Account, x.Bank!.Id, BicOf(x.Bank));
            }
        });

        // The number of rows corrected is not looked at: zero equally means the bank was
        // already there - as it is on domestic accounts, which XL binds itself. What counts is the
        // end state.
        return await _repository.AccountsWithoutBankAsync(
            [.. work.Select(x => (x.ContractorId, x.Account))]);
    }

    /// <summary>
    /// Adds the counterparty's account to the named contractor's card.
    /// </summary>
    /// <remarks>
    /// The account is the only evidence that permits automatic settlement, so recording a manual
    /// choice means the next transfer from that account will go through without a human. It is
    /// attached to the contractor actually chosen - including where the operator corrected the
    /// service's suggestion.
    /// </remarks>
    /// <summary>
    /// Fills the account list with the accounts of the contractor the transfer is settled with.
    /// </summary>
    /// <remarks>
    /// Fetched when a transfer is selected and again whenever the contractor changes, because the
    /// list belongs to the contractor and not to the transfer. Fetched once per contractor - the
    /// query counts entries and there is no reason to repeat it on every click through the queue.
    ///
    /// A failure here is not reported: the list is a convenience, the box beside it stays editable,
    /// and a toast about it would interrupt work that is not blocked.
    /// </remarks>
    private async Task LoadAccountOptionsAsync(PaymentItem? item)
    {
        if (item is null) return;

        var contractorId = item.EffectiveContractorId;

        if (contractorId == 0)
        {
            item.SetAccountOptions([], 0);
            return;
        }

        if (item.AccountOptionsFor == contractorId) return;

        try
        {
            var accounts = await _repository.GetContractorAccountsAsync(contractorId);
            item.SetAccountOptions(accounts, contractorId);

            // The picker's list belongs to the window, so loading the item's accounts is only half
            // of it - without this the drop-down went on offering the previous contractor's
            // accounts at the top after a swap.
            if (ReferenceEquals(item, Selected)) RebuildAccountOptions();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read the accounts of contractor {Contractor}.", contractorId);
        }
    }

    private async Task AssignAccountAsync()
    {
        if (Selected is not { CanAssignAccount: true } item) return;

        var contractorId = item.EffectiveContractorId;
        var account = item.Row.PayerAccount;
        var currency = item.Currency;

        if (!Confirm($"Dopisać rachunek {account} do kartoteki kontrahenta? " +
                     "Kolejne przelewy z tego konta serwis rozpozna sam."))
        {
            return;
        }

        try
        {
            IsBusy = true;

            var bank = await ResolveBankAsync(item);

            var error = await _worker.RunAsync(session => new XlContractorAccounts(_logger)
                .Assign(session, contractorId, account, currency, bank?.Code, BicOf(bank)));

            if (error is not null)
            {
                Notify($"Nie udało się dopisać rachunku: {error}", ToastKind.Error);
                return;
            }

            var bankless = await LinkBanksAsync([(contractorId, account, bank)]);

            item.MarkAccountAssigned();
            RefreshCommands();

            Notify(
                bankless.Count == 0
                    ? "Rachunek dopisany do kartoteki kontrahenta."
                    : "Rachunek dopisany, ale bez banku – wskaż bank na karcie kontrahenta w ERP XL.",
                bankless.Count == 0 ? ToastKind.Success : ToastKind.Warning);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Adding the account failed.");
            Notify($"Błąd dopisywania rachunku: {ex.Message}", ToastKind.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    // --------------------------------------------------------------- helpers ---

    /// <summary>
    /// Reacts to changes in a row: the transfer's tick and the ticks on its documents.
    /// </summary>
    /// <remarks>
    /// Without this the "settle this transfer" button stayed greyed out even though the accountant
    /// had already ticked documents - the command's condition was never re-evaluated.
    /// </remarks>
    private void OnPaymentChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(PaymentItem.IsChecked) or nameof(PaymentItem.SelectedNet))) return;

        RaiseCheckedTotals();
        RefreshCommands();
    }

    private void RaiseCheckedTotals()
    {
        Raise(nameof(CheckedCount));
        Raise(nameof(CheckedTotal));
        Raise(nameof(CheckedAllocated));
        Raise(nameof(CheckedDifference));
        Raise(nameof(HasChecked));
    }

    private bool Matches(PaymentItem item)
    {
        if (!SettlementFilters.Any(f => f.IsSelected && f.State == item.SettlementState)) return false;
        if (!RegisterFilters.Any(f => f.IsSelected && f.Series == item.RegisterSeries)) return false;
        if (!ConfidenceFilters.Any(f => f.IsSelected && f.Label == item.ConfidenceLabel)) return false;
        if (!DirectionFilters.Any(f => f.IsSelected && f.IsIncoming == item.Row.IsIncoming)) return false;

        if (Search.Trim().Length == 0) return true;

        var needle = Search.Trim();
        return item.PayerName.Contains(needle, StringComparison.OrdinalIgnoreCase)
            || item.Description.Contains(needle, StringComparison.OrdinalIgnoreCase)
            || item.ContractorDisplay.Contains(needle, StringComparison.OrdinalIgnoreCase)
            || item.Amount.ToString("F2").Contains(needle, StringComparison.Ordinal);
    }

    /// <summary>
    /// Tells every command to work out again whether it can run.
    /// </summary>
    /// <remarks>
    /// <see cref="RelayCommand"/> deliberately does not hook <c>CommandManager.RequerySuggested</c>,
    /// so a command left out of this list is evaluated once - when its button is first bound, with
    /// nothing selected - and stays disabled for the rest of the session. That is exactly what
    /// happened to the button that opens the source document.
    /// </remarks>
    private void RefreshCommands()
    {
        RefreshCommand.RaiseCanExecuteChanged();
        OpenSourceDocumentCommand.RaiseCanExecuteChanged();
        SettleCheckedCommand.RaiseCanExecuteChanged();
        SettleCurrentCommand.RaiseCanExecuteChanged();
        RevokeCommand.RaiseCanExecuteChanged();
        UseContractorCommand.RaiseCanExecuteChanged();
        AssignAccountCommand.RaiseCanExecuteChanged();
        DoNotSettleCommand.RaiseCanExecuteChanged();

        Raise(nameof(DoNotSettleLabel));
        Raise(nameof(DoNotSettleHint));
    }
}
