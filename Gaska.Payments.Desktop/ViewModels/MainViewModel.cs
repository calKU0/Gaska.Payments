using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows.Data;
using System.Windows.Threading;
using System.Windows;
using Gaska.Payments.Desktop.Data;
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
    private string _documentQuery = string.Empty;
    private bool _onlySuggested;
    private OperatorAccess? _operator;

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

        foreach (var option in SettlementFilters) Watch(option, nameof(SettlementFilterSummary));
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

        RefreshCommand = new RelayCommand(async _ => await LoadAsync(), _ => !IsBusy);
        SettleCheckedCommand = new RelayCommand(async _ => await SettleAsync(Checked), _ => !IsBusy && Checked.Count > 0);
        SettleCurrentCommand = new RelayCommand(
            async _ => await SettleAsync(Selected is null ? [] : [Selected]),
            _ => !IsBusy && Selected is { } p && p.Selected.Count > 0);
        RevokeCommand = new RelayCommand(
            async _ => await RevokeAsync(),
            _ => !IsBusy && Selected is { HasSettlements: true });
        FindContractorCommand = new RelayCommand(async _ => await FindContractorsAsync(), _ => !IsBusy);
        FindDocumentCommand = new RelayCommand(async _ => await FindDocumentsAsync(), _ => !IsBusy);
        UseContractorCommand = new RelayCommand(async o => await UseContractorAsync(o as ContractorRow), _ => !IsBusy);
        DoNotSettleCommand = new RelayCommand(
            async _ => await MarkDoNotSettleAsync(),
            _ => !IsBusy && DoNotSettleTargets().Count > 0);
        AssignAccountCommand = new RelayCommand(
            async _ => await AssignAccountAsync(),
            _ => !IsBusy && Selected is { CanAssignAccount: true });
        RestoreContractorCommand = new RelayCommand(
            async _ => await RestoreContractorAsync(),
            _ => !IsBusy && Selected is { ContractorOverridden: true });
        OpenSourceDocumentCommand = new RelayCommand(
            _ => OpenSourceDocument(),
            _ => Selected is { HasSourceDocument: true });
    }

    // ------------------------------------------------------------------ data ---

    public ObservableCollection<PaymentItem> Payments { get; } = [];

    public ICollectionView View { get; }

    public ObservableCollection<ContractorRow> ContractorHits { get; } = [];

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
    /// transfers and those settled only in part. Fully settled ones are there to be looked at.
    /// </summary>
    public IReadOnlyList<SettlementFilterOption> SettlementFilters { get; } =
    [
        new(SettlementState.Unsettled, "nierozliczone", selected: true),
        new(SettlementState.Partial, "rozliczone częściowo", selected: true),
        new(SettlementState.Settled, "rozliczone w pełni", selected: false),
    ];

    // The text on the collapsed filters. The same rule computes them all, so they differ only
    // in the collection they summarise.
    public string SettlementFilterSummary => FilterOption.Summarize(SettlementFilters);

    public string ConfidenceFilterSummary => FilterOption.Summarize(ConfidenceFilters);

    public string DirectionFilterSummary => FilterOption.Summarize(DirectionFilters);

    public string RegisterFilterSummary => FilterOption.Summarize(RegisterFilters);

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
            if (!Set(ref _selected, value)) return;

            Raise(nameof(HasSelection));
            ContractorHits.Clear();
            HideToast();
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

    public string ContractorQuery
    {
        get => _contractorQuery;
        set => Set(ref _contractorQuery, value);
    }

    public string DocumentQuery
    {
        get => _documentQuery;
        set => Set(ref _documentQuery, value);
    }

    /// <summary>Narrows the queue to transfers the engine proposed anything for.</summary>
    public bool OnlySuggested
    {
        get => _onlySuggested;
        set { if (Set(ref _onlySuggested, value)) View.Refresh(); }
    }

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
    public RelayCommand FindContractorCommand { get; }
    public RelayCommand FindDocumentCommand { get; }
    public RelayCommand UseContractorCommand { get; }
    public RelayCommand AssignAccountCommand { get; }
    public RelayCommand RestoreContractorCommand { get; }
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
            var queue = await _repository.GetQueueAsync(from);
            var suggestions = (await _repository.GetSuggestionsAsync(from))
                .GroupBy(s => s.PaymentId)
                .ToDictionary(g => g.Key, g => (IReadOnlyList<SuggestionRow>)[.. g]);

            foreach (var old in Payments) old.PropertyChanged -= OnPaymentChanged;
            Payments.Clear();
            Selected = null;

            foreach (var row in queue)
            {
                var item = new PaymentItem(row) { SourceDocument = _documents.Find(row) };
                if (suggestions.TryGetValue(row.PaymentId, out var hits)) item.Suggestions = hits;
                item.PropertyChanged += OnPaymentChanged;
                Payments.Add(item);
            }

            loading.Result(
                $"{queue.Count} entries from {From:yyyy-MM-dd}, {suggestions.Count} with the engine's hints, "
                + $"{RegisterFilters.Count} registers on the filter");

            // Counters only after filtering - they count what is visible on the list.
            View.Refresh();

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
        foreach (var existing in item.Documents) existing.SelectionChanged -= item.RaiseTotals;
        item.Documents.Clear();

        var suggested = item.Suggestions
            .Select(s => (s.DocType, s.DocId, s.DocLp))
            .ToHashSet();

        foreach (var row in documents)
        {
            var isSuggested = suggested.Contains((row.DocType, row.DocId, row.DocLp));
            var document = new DocumentItem(row, isSuggested, item.Row.IsIncoming);
            document.SelectionChanged += item.RaiseTotals;
            item.Documents.Add(document);
        }

        // Hints naming documents outside the loaded list (another contractor, or a document
        // already settled) simply have nothing to tick - which is right, because they are stale.
        item.RaiseTotals();
    }

    // ---------------------------------------------------------- contractor ---

    private async Task FindContractorsAsync()
    {
        if (ContractorQuery.Trim().Length < 2)
        {
            Notify("Podaj co najmniej dwa znaki, żeby wyszukać kontrahenta.", ToastKind.Warning);
            return;
        }

        try
        {
            IsBusy = true;
            ContractorHits.Clear();
            foreach (var hit in await _repository.FindContractorsAsync(ContractorQuery.Trim()))
            {
                ContractorHits.Add(hit);
            }

            Notify($"Znaleziono {ContractorHits.Count} kontrahentów.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Contractor search failed.");
            Notify($"Błąd wyszukiwania kontrahenta: {ex.Message}", ToastKind.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task UseContractorAsync(ContractorRow? contractor)
    {
        if (contractor is null || Selected is null) return;

        try
        {
            IsBusy = true;
            Selected.UseContractor(contractor.Id, contractor.Acronym, contractor.Display, byOperator: true);
            await FillDocumentsAsync(Selected, contractor.Id);
            Selected.DocumentsLoaded = true;
            await RefreshAccountKnownAsync(Selected);
            Notify($"Pokazuję nierozliczone dokumenty kontrahenta {contractor.Acronym}.");
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
    /// Returns to the contractor and documents the service worked out, discarding the manual swap.
    /// </summary>
    private async Task RestoreContractorAsync()
    {
        if (Selected is not { ContractorOverridden: true } item) return;

        try
        {
            IsBusy = true;
            item.RestoreServiceContractor();
            ContractorHits.Clear();
            ContractorQuery = string.Empty;

            await FillDocumentsAsync(item, item.Row.ContractorId);
            item.DocumentsLoaded = true;
            await RefreshAccountKnownAsync(item);

            Notify("Przywrócono kontrahenta i dokumenty wskazane przez serwis.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not restore the contractor.");
            Notify($"Błąd przywracania kontrahenta: {ex.Message}", ToastKind.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Appends documents found by number to the list - without swapping the contractor.</summary>
    private async Task FindDocumentsAsync()
    {
        if (Selected is null) return;
        if (!int.TryParse(new string([.. DocumentQuery.Where(char.IsDigit)]), out var number) || number == 0)
        {
            Notify("Podaj numer dokumentu, na przykład 33575.", ToastKind.Warning);
            return;
        }

        try
        {
            IsBusy = true;
            var hits = await _repository.FindDocumentsByNumberAsync(number);
            var known = Selected.Documents
                .Select(d => (d.Row.DocType, d.Row.DocId, d.Row.DocLp))
                .ToHashSet();

            var added = 0;
            foreach (var row in hits.Where(h => !known.Contains((h.DocType, h.DocId, h.DocLp))))
            {
                var document = new DocumentItem(row, false, Selected.Row.IsIncoming);
                document.SelectionChanged += Selected.RaiseTotals;
                Selected.Documents.Add(document);
                added++;
            }

            Notify(
                added == 0
                    ? $"Nie znaleziono nierozliczonych dokumentów o numerze {number}."
                    : $"Dołączono {added} dokumentów o numerze {number}.",
                added == 0 ? ToastKind.Warning : ToastKind.Info);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Document search failed.");
            Notify($"Błąd wyszukiwania dokumentu: {ex.Message}", ToastKind.Error);
        }
        finally
        {
            IsBusy = false;
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
    /// Marks the given transfers as not subject to settlement - one at a time or in bulk.
    /// </summary>
    private async Task MarkDoNotSettleAsync()
    {
        var items = DoNotSettleTargets();
        if (items.Count == 0) return;

        var skipped = (CheckedCount > 0 ? CheckedCount : Selected is null ? 0 : 1) - items.Count;

        var question = items.Count == 1
            ? "Oznaczyć ten przelew w ERP jako niepodlegający rozliczeniu?"
            : $"Oznaczyć {items.Count} przelewów w ERP jako niepodlegające rozliczeniu?";

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
                foreach (var (paymentId, entryId) in plan) _store.MarkDoNotSettle(paymentId, entryId, User);
            });

            await ReloadAndAdvanceAsync(plan[^1].PaymentId);

            Notify(
                skipped == 0
                    ? $"Oznaczono {plan.Count} przelewów jako niepodlegające rozliczeniu."
                    : $"Oznaczono {plan.Count} przelewów. Pominięto {skipped} częściowo rozliczonych.",
                skipped == 0 ? ToastKind.Success : ToastKind.Warning);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Marking as not subject to settlement failed.");
            Notify($"Błąd oznaczania: {ex.Message}", ToastKind.Error);
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
        var pending = items
            .Where(i => i.SettlementState != SettlementState.Settled && i.Selected.Count > 0)
            .ToList();

        if (pending.Count == 0)
        {
            Notify(
                closed > 0
                    ? "Zaznaczone przelewy są już rozliczone w całości."
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
                    _store.UpdateEntryContractor(request.PaymentId, request.EntryId, request.ContractorId);
                }

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
                found.Id, entered.BankCode, entered.Name, entered.City, entered.PostalCode));

            if (entered.Bic.Length > 0) _bankBics[found.Id] = entered.Bic;
            return found with { BindsInXl = true };
        }

        var name = entered.Name.Length > 0 ? entered.Name : item.Row.BankName;

        var created = await Task.Run(() => _store.CreateBank(
            entered.Bic, name, entered.BankCode, entered.CountryCode, entered.City, entered.PostalCode));

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
        if (OnlySuggested && item.Suggestions.Count == 0) return false;

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
        FindContractorCommand.RaiseCanExecuteChanged();
        FindDocumentCommand.RaiseCanExecuteChanged();
        UseContractorCommand.RaiseCanExecuteChanged();
        AssignAccountCommand.RaiseCanExecuteChanged();
        RestoreContractorCommand.RaiseCanExecuteChanged();
        DoNotSettleCommand.RaiseCanExecuteChanged();
    }
}
