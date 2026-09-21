using Gaska.Payments.Application.Cards;
using Gaska.Payments.Application.Couriers;
using Gaska.Payments.Application.Settlement;
using Gaska.Payments.Domain.Couriers;
using Gaska.Payments.Domain.Diagnostics;
using Gaska.Payments.Domain.Model;
using Gaska.Payments.Erp;
using Gaska.Payments.Integrations.Couriers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using cdn_api;

namespace Gaska.Payments.Application.Posting;

/// <summary>A summary of one posting run.</summary>
public sealed record PostingSummary(
    int ReportsCreated, int EntriesPosted, int PaymentsSettled, int SplitPaymentsLinked, int Failures);

/// <summary>
/// Creates ERP cash entries for the operations downloaded from the bank and settles the payments
/// the engine considers certain.
/// </summary>
/// <remarks>
/// Every XL API call runs in one stretch on the one thread that owns the session, with no
/// <c>await</c> between them - the native library is thread-bound (see <see cref="XlSessionHost"/>).
/// Results are written as we go, synchronously: were the process to break off midway, ERP would be
/// left holding documents we know nothing about.
/// </remarks>
public sealed class ErpPostingService(
    PostingRepository repository,
    XlSessionHost xl,
    IOptions<XlOptions> options,
    IOptions<SettlementOptions> settlementOptions,
    IOptions<CodOptions> codOptions,
    IOptions<CardOptions> cardOptions,
    ILogger<ErpPostingService> logger)
{
    private const int ContractorGidType = 32;
    private const int BatchMode = 2;            // batch mode - no dialog windows

    private readonly XlOptions _options = options.Value;

    /// <summary>
    /// The registers come from the <c>Settlement</c> section - the same one statement downloading
    /// uses. A second list under <c>Xl</c> said the same thing and was apt to drift. The registers
    /// without settlement sit here alongside the rest: cash entries are created on them just the
    /// same, and the difference is made only by the operation category, which keeps them out of
    /// settlement.
    /// </summary>
    private readonly IReadOnlyList<string> _registers =
        Registers(settlementOptions.Value, codOptions.Value, cardOptions.Value);

    private readonly CodOptions _cod = codOptions.Value;

    private readonly CardOperationSymbols _cardOperations = cardOptions.Value.Enabled
        ? new(cardOptions.Value.SaleOperation, cardOptions.Value.RefundOperation, cardOptions.Value.FeeOperation)
        : CardOperationSymbols.None;

    /// <summary>
    /// The registers posted to: the bank ones, plus the cash on delivery register and the card
    /// terminal's when they are switched on. Neither is in the <c>Settlement</c> lists, because no
    /// statement is ever downloaded for them - their money comes from reports, not from the bank.
    /// </summary>
    private static IReadOnlyList<string> Registers(SettlementOptions settlement, CodOptions cod, CardOptions cards)
    {
        var registers = new List<string>(settlement.AllRegisters);

        if (cod is { Enabled: true, Register.Length: > 0 }) registers.Add(cod.Register);
        if (cards is { Enabled: true, Register.Length: > 0 }) registers.Add(cards.Register);

        return [.. registers.Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    private readonly XlSettlementEngine _engine = new(logger);

    /// <summary>
    /// One posting pass: it creates the missing ERP cash entries, settles the payments deemed
    /// certain, and finally closes the open items of entries that matched only now.
    /// </summary>
    /// <remarks>
    /// It all runs in a single XL session. The settlement backlog used to be a separate mode run
    /// by hand - this is the same code, only called from the cycle, so a match that appears after
    /// an entry was posted closes itself on the next round.
    /// </remarks>
    public async Task<PostingSummary> RunAsync(CancellationToken cancellationToken = default)
    {
        var released = await repository.ReleaseMissingEntriesAsync(cancellationToken);
        if (released > 0)
        {
            logger.LogWarning(
                "{Count} cash entries have gone from ERP - their operations go back to be posted again.",
                released);
        }

        var adopted = await repository.AdoptOrphanedEntriesAsync(cancellationToken);
        if (adopted > 0)
        {
            logger.LogWarning(
                "Adopted {Count} entries that exist in ERP but were not recorded on our side.", adopted);
        }

        var operations = await repository.GetOperationsToPostAsync(
            _options.PostFrom, _registers, _options.MaxOperationsPerRun, _options.FeeOperation,
            _cod.OperationSymbol, _options.ToBuffer, _cardOperations, cancellationToken);

        await ReportStrandedAsync(cancellationToken);

        // Entries already in ERP that matched only after they had been posted.
        var backlog = await repository.GetSettlementBacklogAsync(
            _options.PostFrom, _registers, _options.MaxOperationsPerRun, cancellationToken);

        if (operations.Count == 0 && backlog.Count == 0)
        {
            logger.LogInformation("Nothing to post and nothing to settle.");
            return new PostingSummary(0, 0, 0, await LinkSplitPaymentsAsync(cancellationToken), 0);
        }

        // Everything the XL session needs is loaded up front.
        var allocations = new Dictionary<long, IReadOnlyList<PendingAllocation>>();

        var needAllocations = operations.Where(IsReadyForAutomaticSettlement).Select(o => o.PaymentId)
            .Concat(backlog.Select(b => b.PaymentId))
            .Distinct();

        foreach (var paymentId in needAllocations)
        {
            allocations[paymentId] = await repository.GetAllocationsAsync(paymentId, cancellationToken);
        }

        // Keyed by the report an entry goes into - its own day's, or its month's on KARTA.
        var existingReports = new HashSet<(string, DateTime)>();
        foreach (var (series, day) in operations
                     .Select(o => (o.RegisterSeries, PaymentCategory.ReportDay(o.PostingCategory, o.BookingDate)))
                     .Distinct())
        {
            if (await repository.ReportExistsAsync(series, day, cancellationToken)) existingReports.Add((series, day));
        }

        logger.LogInformation(
            "To post: {Count} operations from registers {Registers}; to settle on top of that: {Backlog}.",
            operations.Count, string.Join(", ", _registers), backlog.Count);

        PostingSummary summary;

        using (var posting = TimedOperation.Start(logger, "Posting to ERP through the XL API"))
        {
            // On the XL thread, always the same one, against the session signed in when the
            // service started - see XlSessionHost.
            summary = await xl.RunAsync(
                session => PostInXl(session, operations, allocations, existingReports, backlog),
                cancellationToken);
            posting.Result(
                $"{summary.EntriesPosted} entries, {summary.PaymentsSettled} settlements, "
                + $"{summary.Failures} failures");
        }

        return summary with { SplitPaymentsLinked = await LinkSplitPaymentsAsync(cancellationToken) };
    }

    /// <summary>
    /// Says out loud what can no longer be posted.
    /// </summary>
    /// <remarks>
    /// An operation whose day never got a report, on a register that has since moved on to a later
    /// one, cannot be posted at all: the entry needs a report of its own day and no report can be
    /// opened in the past (<c>XLDodajRaport</c> answers 8181). It is left out of the posting query
    /// so it stops costing an API call every hour, which means nothing else would ever mention it
    /// again. This is that mention: money that reached the bank and has not reached ERP, and that
    /// somebody has to enter by hand.
    ///
    /// A later report on its own strands nothing - an entry goes into its own day's open report
    /// whether or not newer ones exist. See <c>PostingRepository.PastDay</c> for the measurements.
    /// </remarks>
    private async Task ReportStrandedAsync(CancellationToken cancellationToken)
    {
        var stranded = await repository.GetStrandedAsync(_options.PostFrom, _registers, cancellationToken);
        if (stranded.Count == 0) return;

        logger.LogWarning(
            "{Count} operations worth {Amount:N2} cannot be posted: their day has no open report " +
            "and their register has already moved on, so no report can be opened for it any more. " +
            "They have to be entered by hand. Days affected: {Days}",
            stranded.Sum(s => s.Count), stranded.Sum(s => s.Amount),
            string.Join(", ", stranded.Select(s => $"{s.Register} {s.Day:yyyy-MM-dd} ({s.Count})")));
    }

    /// <summary>
    /// Ties the legs of a split payment together - the main transfer with its VAT leg.
    /// </summary>
    /// <remarks>
    /// A SQL query does this, not the XL API: the API has no function that modifies an existing
    /// cash entry, and the link is two fields on entries that are already in ERP.
    /// </remarks>
    private async Task<int> LinkSplitPaymentsAsync(CancellationToken cancellationToken)
    {
        var linked = await repository.LinkSplitPaymentsAsync(cancellationToken);
        if (linked > 0) logger.LogInformation("Linked {Count} split payment legs.", linked);

        return linked;
    }

    /// <summary>All the work with the XL API - synchronous, in one session, on one thread.</summary>
    private PostingSummary PostInXl(
        XlSession session,
        IReadOnlyList<PendingOperation> operations,
        Dictionary<long, IReadOnlyList<PendingAllocation>> allocations,
        HashSet<(string, DateTime)> existingReports,
        IReadOnlyList<PendingSettlement> backlog)
    {
        var journal = new PostingJournal(repository.ConnectionString);

        var reportsCreated = 0;
        var posted = 0;
        var settled = 0;
        var failures = 0;

        // Operations arrive ordered by day, and each day's report is created only when its first
        // entry is about to be posted. Creating them all up front - which is what this did - shuts
        // the door on the earlier days: a report cannot be opened in the past, so on 2026-09-02 the
        // report for that day was created first and the 112 operations of 2026-09-01 that the bank
        // had just delivered were left with no report of their own to go into.
        foreach (var operation in operations)
        {
            reportsCreated += EnsureReport(session, operation, existingReports);

            var entryId = AddCashEntry(session, operation, out var error);

            if (entryId is null)
            {
                failures++;
                journal.MarkFailed(operation.PaymentId, error ?? "nieznany błąd");
                continue;
            }

            // Written at once - should the process break off later, the link is already there.
            journal.MarkPosted(operation.PaymentId, entryId.Value);
            posted++;

            if (IsReadyForAutomaticSettlement(operation) &&
                allocations.TryGetValue(operation.PaymentId, out var lines) && lines.Count > 0)
            {
                if (Settle(session, journal, operation.PaymentId, entryId.Value, operation.Amount,
                        operation.ContractorId, lines))
                {
                    settled++;
                    continue;
                }

                failures++;
            }

            // Left unsettled, but on a contractor we are sure of: the entry gets that contractor's
            // account now, as a settlement would have given it. Fiserv's payout and its commission
            // are never settled at all, and went without one until somebody typed it in.
            if (operation.CarriesAccount) journal.UpdateEntryAccount(entryId.Value, operation.ErpContractorId);
        }

        // The backlog is closed in the same session - the same open items, only for entries
        // that reached ERP earlier.
        foreach (var item in backlog)
        {
            if (!allocations.TryGetValue(item.PaymentId, out var lines) || lines.Count == 0) continue;

            if (Settle(session, journal, item.PaymentId, item.EntryId, item.Amount,
                    item.ContractorId, lines)) settled++;
            else failures++;
        }

        logger.LogInformation(
            "Posted {Posted} entries, settled {Settled} payments, {Failures} failures.",
            posted, settled, failures);

        return new PostingSummary(reportsCreated, posted, settled, 0, failures);
    }

    /// <summary>
    /// Closes the open items of one entry and records the outcome. Returns true on success.
    /// </summary>
    private bool Settle(
        XlSession session, PostingJournal journal, long paymentId, int entryId, decimal amount,
        int contractorId, IReadOnlyList<PendingAllocation> lines)
    {
        // Lines are tied back by document GID - the engine knows nothing of our identifiers.
        // Grouped rather than ToDictionary: two lines pointing at the same document payment are
        // not supposed to happen, but a duplicate here would throw in the middle of an open XL
        // session and abandon the rest of the pass.
        var byDocument = lines
            .GroupBy(l => (l.DocType, l.DocId, l.DocLp))
            .ToDictionary(g => g.Key, g => g.First().AllocationId);
        var result = _engine.Settle(session, entryId, [.. lines.Select(ToSettlementLine)], amount);

        if (!result.Succeeded)
        {
            journal.MarkFailed(paymentId, result.Error!);
            return false;
        }

        foreach (var outcome in result.Settlements)
        {
            var key = (outcome.Line.DocType, outcome.Line.DocId, outcome.Line.DocLp);
            if (byDocument.TryGetValue(key, out var allocationId))
            {
                journal.MarkAllocationSettled(allocationId, outcome.Gid.Numer);
            }
        }

        // The entry was posted on the anonymous party whenever the payer's account was on nobody's
        // card in ERP. By the time it settles the party is known - the account was added by hand in
        // the meantime, or the documents named it - and the entry has to say so, or the open item
        // sits on the contractor's invoice while the entry belongs to nobody. Does nothing when the
        // entry already names them, which is the ordinary case.
        if (contractorId != 0) journal.UpdateEntryContractor(paymentId, entryId, contractorId);

        // The entry now names the invoices it paid instead of the bank's reference - that is what
        // the accountants look an entry up by once it is settled.
        journal.UpdateEntryDocumentNumber(entryId, [.. lines.Select(l => l.DocNumber)]);

        journal.MarkSettled(paymentId);
        return true;
    }

    /// <summary>
    /// Makes sure the daily report for this operation exists, creating it if it does not.
    /// </summary>
    /// <remarks>
    /// Called for every operation, immediately before its entry is posted, so that the reports come
    /// into being in the same order as the entries - oldest first. A report cannot be opened for a
    /// day the register has already passed, so one created ahead of its turn leaves every earlier
    /// day that had no report of its own with no way of ever getting one.
    ///
    /// In buffer mode there is nothing to create: an entry in the buffer hangs off the register
    /// rather than off a report (<c>KAZ_KRPTyp = 752</c>), and only on confirmation does ERP pull
    /// it into the report matching its date.
    /// </remarks>
    /// <returns>1 when a report was created, 0 when there was nothing to do.</returns>
    private int EnsureReport(
        XlSession session, PendingOperation operation, HashSet<(string, DateTime)> existingReports)
    {
        if (_options.ToBuffer) return 0;

        // The report the entry belongs to: its own day's, or for the card terminal the month's,
        // opened on the first. XL puts an entry dated the 12th into the month's report by itself.
        var day = PaymentCategory.ReportDay(operation.PostingCategory, operation.BookingDate);
        if (!existingReports.Add((operation.RegisterSeries, day))) return 0;

        var report = new XLRaportInfo_20251
        {
            Wersja = session.Version,
            Tryb = BatchMode,
            Kasa = operation.RegisterSeries,
            DataOtw = XlDate.FromDateTime(day),
            // Opened at midnight, as the half-automat opened KARTA's. Left out, XL stamps the report
            // with the time of day it was created, and then takes no entry of that day stamped any
            // earlier: FORPL's report for 14 September, opened at 13:51, refused every entry of the
            // 14th posted at 08:47 the next morning with 8158, "no report of that id".
            DataCzasOtw = XlDate.Moment(day),
        };

        // The id the API hands back is its own handle on the new report, not KRP_GIDNumer, and it
        // is not needed: AddCashEntry passes 0 and lets XL find the report from the register and
        // the entry's date. Passing a report's GID there is what earns an 8158.
        var reportId = 0;
        var result = cdn_api.cdn_api.XLDodajRaport(session.Id, ref reportId, report);

        if (result != 0)
        {
            // 8181 means a report with a later opening date already exists. ERP requires reports to
            // be created in chronological order, so a day in the past cannot be filled in - but the
            // rest of the pass is to carry on regardless. Operations in that position are filtered
            // out before the pass begins, so an 8181 here means a report appeared while it ran.
            logger.LogError("XLDodajRaport returned {Result} for register {Series} and day {Day:yyyy-MM-dd}.",
                result, operation.RegisterSeries, day);
            return 0;
        }

        logger.LogInformation("Created report {Series} no. {Number} for {Day:yyyy-MM-dd} (GID {Gid}).",
            operation.RegisterSeries, report.Numer, day, report.GIDNumer);

        return 1;
    }

    /// <summary>
    /// Adds a cash entry. The report is named by register symbol and date - XL assigns the entry
    /// to the right daily report itself (or to the register's buffer when posting to the buffer).
    /// </summary>
    private int? AddCashEntry(XlSession session, PendingOperation operation, out string? error)
    {
        if (string.IsNullOrWhiteSpace(operation.OperationSymbol))
        {
            error = "Brak symbolu operacji kasowej – sprawdź konfigurację rejestru.";
            logger.LogError(
                "Operation {Id} on register {Series}: no cash operation symbol - check the register's "
                + "statement import configuration, or Cod:OperationSymbol for a cash on delivery.",
                operation.PaymentId, operation.RegisterSeries);
            return null;
        }

        var entry = new XLZapisKasowyInfo_20251
        {
            Wersja = session.Version,
            Tryb = BatchMode,
            Bufor = _options.ToBuffer ? 1 : 0,
            Kasa = operation.RegisterSeries,
            Operacja = operation.OperationSymbol,
            Data = XlDate.FromDateTime(operation.BookingDate),
            DataDok = XlDate.FromDateTime(operation.BookingDate),
            DataCzas = EntryMoment(operation.BookingDate),
            Kwota = XlSession.Amount(operation.Amount),
            WalutaRoz = operation.Currency,
            Numer = Trim(operation.EntryNumber, 31),
            Tresc = Trim(operation.Description, 255),
            Opis = Trim(operation.PayerName, 255),
            KNTTyp = operation.NamesContractor ? ContractorGidType : 0,
            KNTNumer = operation.ErpContractorId,
            // NieRozliczaj is deliberately left unset - ERP takes the flag from the cash
            // operation's definition (KAO_NieRozliczaj), so setting it here would merely
            // duplicate the register's configuration.
        };

        var result = cdn_api.cdn_api.XLDodajZapis(session.Id, 0, entry);

        if (result != 0 || entry.GIDNumer == 0)
        {
            error = $"XLDodajZapis zwrócił {result}.";
            logger.LogError(
                "XLDodajZapis returned {Result} for operation {Id}: {Amount} {Currency} on {Series} "
                + "dated {Day:yyyy-MM-dd}, operation {Symbol}.",
                result, operation.PaymentId, operation.Amount, operation.Currency,
                operation.RegisterSeries, operation.BookingDate, operation.OperationSymbol);
            return null;
        }

        error = null;
        return entry.GIDNumer;
    }

    private static SettlementLine ToSettlementLine(PendingAllocation allocation) =>
        new(allocation.DocType, allocation.DocId, allocation.DocLp, allocation.DocNumber, allocation.Amount);

    /// <summary>
    /// Operations that are certain and still untouched are settled automatically, in both
    /// directions.
    /// </summary>
    /// <remarks>
    /// A debit can only be certain through an order reference returned by the bank: the engine
    /// does not match debits by title, so every certain debit names its document outright.
    /// </remarks>
    /// <summary>
    /// The date and time stamped on an entry: the last second of its day when the day is past,
    /// otherwise left to XL.
    /// </summary>
    /// <remarks>
    /// XL takes an entry into a report only if the entry is stamped no earlier than the report was
    /// opened. Left to itself it stamps the entry with its own day and the current time, so an entry
    /// for yesterday, posted this morning, falls before a report of yesterday's opened in the
    /// afternoon - and is refused with 8158 until the clock passes that hour. The last second of the
    /// day is after any opening on that day and before the next day's report, which is what makes
    /// the entry belong where its date says. Today's entries keep XL's own stamp: that is now, and
    /// no report of today can have been opened later than now.
    /// </remarks>
    private static int EntryMoment(DateTime bookingDate) =>
        bookingDate.Date < DateTime.Today ? XlDate.Moment(bookingDate.Date.AddDays(1).AddSeconds(-1)) : 0;

    private static bool IsReadyForAutomaticSettlement(PendingOperation operation) =>
        operation is { Confidence: "High", Status: "Proposed" };

    private static string Trim(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}
