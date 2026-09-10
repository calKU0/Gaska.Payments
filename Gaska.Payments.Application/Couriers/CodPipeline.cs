using Gaska.Payments.Application.Settlement;
using Gaska.Payments.Domain.Diagnostics;
using Gaska.Payments.Integrations.Archive;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Gaska.Payments.Domain.Couriers;
using Gaska.Payments.Domain.Matching;
using Gaska.Payments.Erp;
using Gaska.Payments.Integrations.Couriers;

namespace Gaska.Payments.Application.Couriers;

/// <summary>What one pass over the mailbox and the waiting reports came to.</summary>
public sealed record CodSummary(
    int FilesArchived,
    int Waiting,
    int Posted,
    int Mismatched,
    int ParcelsProposed,
    int ParcelsSkipped,
    int WithoutDocuments)
{
    public static readonly CodSummary Nothing = new(0, 0, 0, 0, 0, 0, 0);
}

/// <summary>
/// Turns the courier payout reports arriving by e-mail into cash entries waiting to be posted.
/// </summary>
/// <remarks>
/// A report is not acted on when it arrives - it is acted on when the money does. The entries carry
/// the day the courier's transfer was booked, and that day is nowhere in the file: GLS declared
/// 2026-05-25 on a payout that reached the account on the 26th, and DPD sends its file with the
/// payout date still days in the future. So a report is archived, then waits, and is taken up again
/// on whichever later pass finds its transfer in the bank register.
///
/// The money arrives as one transfer but is booked parcel by parcel: each parcel is one customer
/// paying for one invoice, and it is the invoice that has to be closed. That is how the accountants
/// have always entered them by hand, and the register holds 167 030 such entries.
/// </remarks>
public sealed class CodPipeline(
    CodMailbox mailbox,
    CodStore store,
    ShipmentReader erp,
    CodNotifier notifier,
    IEnumerable<ICodReportReader> readers,
    IOptions<CodOptions> options,
    IOptions<SettlementOptions> settlementOptions,
    IOptions<ArchiveOptions> archiveOptions,
    ILogger<CodPipeline> logger)
{
    /// <summary>
    /// How much of a parcel number has to be there before a transfer's title counts as naming it.
    /// Waybills run to ten digits and more; anything shorter would be found in half the titles in
    /// the bank.
    /// </summary>
    private const int MinimumParcelDigits = 6;

    /// <summary>
    /// How many parcels a transfer may be credited with beyond the ones its title names. A title
    /// that got one number wrong is what this is for, not a title that named nothing much: the
    /// wider the search, the likelier two different sets of parcels both fit the money.
    /// </summary>
    private const int MaximumParcelsMadeUp = 4;

    private readonly CodOptions _options = options.Value;
    private readonly SettlementOptions _settlement = settlementOptions.Value;
    private readonly ArchiveOptions _archive = archiveOptions.Value;
    private readonly List<ICodReportReader> _readers = [.. readers];

    public async Task<CodSummary> RunAsync(int runId, CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled) return CodSummary.Nothing;

        var summary = await CollectAsync(cancellationToken);

        return await SettleWaitingAsync(runId, summary, cancellationToken);
    }

    /// <summary>
    /// Reads the mailbox and files what came: each attachment is archived and recorded, and the
    /// ones that are ours to post are left waiting for their transfer.
    /// </summary>
    private async Task<CodSummary> CollectAsync(CancellationToken cancellationToken)
    {
        var seen = await store.GetReadMessagesAsync(cancellationToken);
        var messages = await mailbox.FetchAsync(seen, cancellationToken);

        var summary = CodSummary.Nothing;

        foreach (var message in messages)
        {
            foreach (var attachment in message.Attachments)
            {
                cancellationToken.ThrowIfCancellationRequested();
                summary = await CollectOneAsync(message, attachment, summary, cancellationToken);
            }
        }

        return summary;
    }

    private async Task<CodSummary> CollectOneAsync(
        CodMessage message, CodAttachment attachment, CodSummary summary, CancellationToken cancellationToken)
    {
        var reader = _readers.FirstOrDefault(r => r.Recognises(attachment.FileName, attachment.Content));

        if (reader is null)
        {
            // Signatures, logos, anything else that travels with the report. Recorded so the next
            // pass does not look at it again.
            await Ignore(message, attachment, string.Empty, string.Empty, "attachment not recognised");
            return summary;
        }

        var configured = _options.Courier(reader.Format);

        if (configured is null)
        {
            // A format we can read but nobody configured. Deliberately not recorded: this is a gap
            // in configuration, and once somebody fills it in the report is to be picked up rather
            // than lost because an earlier pass had written it off.
            logger.LogWarning(
                "The attachment {File} is in format {Format}, which is not listed under Cod:Couriers - " +
                "skipping it, and deliberately not marking it read.",
                attachment.FileName, reader.Format);

            return summary;
        }

        if (!configured.Accepts(message.Sender))
        {
            await Ignore(message, attachment, configured.Name, reader.Format,
                $"sender {message.Sender} is not on the list for {configured.Name}");

            return summary;
        }

        CodReport report;

        try
        {
            report = reader.Read(attachment.Content);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Not recorded as read: a file we failed on is worth another try after a fix, and it is
            // better to see it again than to lose a payout silently.
            logger.LogError(exception, "Could not read the {Courier} report {File}.",
                configured.Name, attachment.FileName);

            return summary;
        }

        var path = Archive(configured.Name, report, attachment);
        if (path.Length > 0) summary = summary with { FilesArchived = summary.FilesArchived + 1 };

        logger.LogInformation(
            "Read a {Courier} report: {Parcels} parcels worth {Total:N2}, declared payout {Day:yyyy-MM-dd}, " +
            "reference {Reference}, account {Account}.",
            configured.Name, report.Parcels.Count, report.ParcelTotal, report.PayoutDate,
            report.Reference.Length > 0 ? report.Reference : "(none)",
            report.Account.Length > 0 ? report.Account : "(not named in the file)");

        var row = CodReportRow.For(message, attachment, CodReportStatus.Pending) with
        {
            Courier = configured.Name,
            Format = reader.Format,
            FilePath = path,
            PayoutDate = report.PayoutDate.Date,
            PayoutTotal = report.PayoutTotal,
            ParcelCount = report.Parcels.Count,
        };

        if (Rejection(configured, report) is { } reason)
        {
            logger.LogInformation(
                "The {Courier} report {File} produces no entries: {Reason}",
                configured.Name, attachment.FileName, reason);

            await store.SaveAsync(row with { Status = CodReportStatus.Ignored, Note = reason }, cancellationToken);
            return summary;
        }

        if (path.Length == 0)
        {
            // Without the archived file there is nothing to come back to on a later pass, and the
            // transfer is not here yet. Left unrecorded so the mailbox is read again.
            logger.LogError(
                "The {Courier} report {File} did not reach the archive - there would be nothing to come " +
                "back to when its transfer arrives, so it is left unread.",
                configured.Name, attachment.FileName);

            return summary;
        }

        await store.SaveAsync(row, cancellationToken);

        return summary;
    }

    /// <summary>
    /// Why this report produces no entries at all, or null when it is ours to post.
    /// </summary>
    private string? Rejection(CourierOptions courier, CodReport report)
    {
        if (!report.AddsUp)
        {
            // The parcels are the courier's own breakdown of its transfer. When they do not add up
            // to it, we have read the file wrongly, and posting off a misread file is worse than
            // posting nothing.
            return $"parcels add up to {report.ParcelTotal:N2}, the file declares {report.PayoutTotal:N2}";
        }

        if (!courier.AcceptsAccount(report.Account))
        {
            // Somebody else's money. FedEx's dropshipping service pays the customer directly, and
            // its reports look exactly like ours except for the account they name.
            return $"account {report.Account} is not in Cod:Couriers:Accounts";
        }

        if (report.PayoutDate.Date < _options.PostFrom.Date)
        {
            // Those days were entered by hand and are already in ERP.
            return $"payout {report.PayoutDate:yyyy-MM-dd} predates the takeover on {_options.PostFrom:yyyy-MM-dd}";
        }

        return null;
    }

    /// <summary>
    /// Takes up the reports that were waiting, for each one looking in the bank registers for the
    /// courier's transfer.
    /// </summary>
    private async Task<CodSummary> SettleWaitingAsync(
        int runId, CodSummary summary, CancellationToken cancellationToken)
    {
        var pending = await store.GetPendingAsync(cancellationToken);
        if (pending.Count == 0) return summary;

        using var step = TimedOperation.Trace(
            logger, $"Cash on delivery: looking for the transfers of {pending.Count} reports");

        var oldest = pending.Min(r => r.PayoutDate ?? DateTime.Today);
        var window = Math.Max(1, _options.PayoutWindowDays);

        var payouts = await erp.GetPayoutsAsync(
            _settlement.Registers, oldest.AddDays(-window), DateTime.Today.AddDays(window), cancellationToken);

        var posted = await store.GetPostedWaybillsAsync(
            _options.Register, _options.PostFrom, cancellationToken);

        logger.LogDebug(
            "Candidate transfers between {From:yyyy-MM-dd} and {To:yyyy-MM-dd}: {Payouts}; " +
            "{Waybills} waybills already on {Register}.",
            oldest.AddDays(-window), DateTime.Today.AddDays(window), payouts.Count,
            posted.Count, _options.Register);

        // A transfer answers to one report and no other. Without this, two reports whose waybills
        // both appear in one title - a payout covering parcels from two files - would each take it,
        // and the same money would be booked twice over.
        var claimed = await store.GetClaimedPayoutsAsync(cancellationToken);

        foreach (var row in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var free = payouts.Where(p => !claimed.Contains(p.EntryId)).ToList();
            summary = await SettleOneAsync(row, free, posted, runId, summary, cancellationToken);

            if (await store.GetPayoutOfAsync(row.Uid, row.FileName, cancellationToken) is { } taken)
            {
                claimed.Add(taken);
            }
        }

        logger.LogInformation(
            "Cash on delivery: {Files} files archived, {Posted} reports paid, {Mismatched} with a " +
            "divergence, {Waiting} waiting for their transfer; {Proposed} parcels to post, " +
            "{Skipped} already in ERP, {Unmatched} with no document.",
            summary.FilesArchived, summary.Posted, summary.Mismatched, summary.Waiting,
            summary.ParcelsProposed, summary.ParcelsSkipped, summary.WithoutDocuments);

        step.Result($"{summary.Posted} paid, {summary.Mismatched} divergent, {summary.Waiting} still waiting");

        return summary;
    }

    private async Task<CodSummary> SettleOneAsync(
        CodReportRow row, IReadOnlyList<CodPayout> payouts, HashSet<string> posted, int runId,
        CodSummary summary, CancellationToken cancellationToken)
    {
        var reader = _readers.FirstOrDefault(r => r.Format == row.Format);
        if (reader is null || !File.Exists(row.FilePath)) return summary with { Waiting = summary.Waiting + 1 };

        CodReport report;

        try
        {
            report = reader.Read(await File.ReadAllBytesAsync(row.FilePath, cancellationToken));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Could not read the archived report {File} again.", row.FilePath);
            return summary with { Waiting = summary.Waiting + 1 };
        }

        if (report.Shape == CodPayoutShape.PerParcel)
        {
            return await SettlePerParcelAsync(
                row, report, payouts, posted, runId, summary, cancellationToken);
        }

        if (report.Shape == CodPayoutShape.NamedGroups)
        {
            return await SettleNamedGroupsAsync(
                row, report, payouts, posted, runId, summary, cancellationToken);
        }

        var match = payouts
            .Select(p => p.Match(report))
            .Where(m => m.Found)
            .OrderByDescending(m => m.Strength)  // reference, then agreeing amount, then hits
            .FirstOrDefault();

        if (match is null)
        {
            // The money is not here yet. The report waits - as long as it has to, because a payout
            // dated in the future is normal and there is nothing to alarm anybody about.
            logger.LogDebug(
                "No transfer yet for the {Courier} report {Reference} worth {Total:N2}, declared {Day:yyyy-MM-dd}.",
                row.Courier, report.Reference, report.ParcelTotal, report.PayoutDate);

            return summary with { Waiting = summary.Waiting + 1 };
        }

        var payout = match.Payout;

        // The one check that decides everything: what the courier actually sent has to be what the
        // file says it sent, to the penny.
        var agrees = Math.Abs(payout.Amount - report.ParcelTotal) <= 0.004m;

        logger.LogInformation(
            "Matched the {Courier} report {Reference} to transfer {Entry} of {Amount:N2} booked " +
            "{Day:yyyy-MM-dd} on {Register}, {Reason}; the sums {Verdict}.",
            row.Courier, report.Reference, payout.EntryId, payout.Amount, payout.BookedOn,
            payout.Register, match.Reason, agrees ? "agree" : "do NOT agree");

        var counts = await ProposeAsync(
            report, row.Courier, row.FilePath, payout.BookedOn, agrees, posted, runId, cancellationToken);

        logger.LogInformation(
            "The {Courier} report {Reference}: {Proposed} parcels ready to post dated {Day:yyyy-MM-dd}, " +
            "{Skipped} already in ERP, {Unmatched} with no document.",
            row.Courier, report.Reference, counts.Proposed, payout.BookedOn,
            counts.Skipped, counts.WithoutDocuments);

        var note = agrees
            ? $"transfer {payout.EntryId} of {payout.Amount:N2} booked {payout.BookedOn:yyyy-MM-dd}, {match.Reason}"
            : $"transfer {payout.Amount:N2} against parcels of {report.ParcelTotal:N2} - nothing settled";

        var notified = row.Notified;

        if (!agrees && !notified)
        {
            logger.LogError(
                "The {Courier} report {Reference} does not agree with its transfer: paid {Payout:N2} on " +
                "{Day:yyyy-MM-dd}, parcels add up to {Sum:N2}, difference {Difference:N2}. " +
                "Nothing from this report will be settled automatically.",
                row.Courier, report.Reference, payout.Amount, payout.BookedOn, report.ParcelTotal,
                payout.Amount - report.ParcelTotal);

            notified = await notifier.ReportMismatchAsync(
                row.Courier, report, payout, row.FilePath, cancellationToken);
        }

        await store.SaveAsync(row with
        {
            Status = agrees ? CodReportStatus.Posted : CodReportStatus.Mismatch,
            PayoutEntryId = payout.EntryId,
            PayoutBookedOn = payout.BookedOn,
            PayoutAmount = payout.Amount,
            ParcelCount = report.Parcels.Count,
            Note = note,
            Notified = notified,
        }, cancellationToken);

        return summary with
        {
            Posted = summary.Posted + (agrees ? 1 : 0),
            Mismatched = summary.Mismatched + (agrees ? 0 : 1),
            ParcelsProposed = summary.ParcelsProposed + counts.Proposed,
            ParcelsSkipped = summary.ParcelsSkipped + counts.Skipped,
            WithoutDocuments = summary.WithoutDocuments + counts.WithoutDocuments,
        };
    }

    /// <summary>
    /// A report whose parcels are each paid by a transfer of their own.
    /// </summary>
    /// <remarks>
    /// Hellmann only. Its file is a running list rather than a payout: the same rows come back in
    /// the next file, most of them long since paid, and each parcel is settled by a transfer whose
    /// title carries the order number from the first column - sometimes with the invoice number
    /// after it, as in "1014118551, FS-38408/26/SPR".
    ///
    /// So there is nothing to check a total against, and instead each parcel is paired with its
    /// own transfer on two conditions at once: the title names the order number, and the transfer
    /// is worth exactly what the row says. Both, because an order number is ten digits and could
    /// in principle turn up inside another number, and because an amount alone says nothing at
    /// all. That is a stricter test per parcel than the collective one it replaces.
    ///
    /// A transfer answers to one parcel and no other, so a title naming two order numbers cannot
    /// pay for both. Parcels whose money has not arrived are simply left - the file lists them
    /// long before Hellmann pays.
    /// </remarks>
    private async Task<CodSummary> SettlePerParcelAsync(
        CodReportRow row, CodReport report, IReadOnlyList<CodPayout> payouts, HashSet<string> posted,
        int runId, CodSummary summary, CancellationToken cancellationToken)
    {
        var taken = new HashSet<int>();
        var paid = new Dictionary<string, CodPayout>(StringComparer.OrdinalIgnoreCase);

        foreach (var parcel in report.Parcels)
        {
            if (posted.Contains(parcel.Waybill)) continue;

            var digits = new string([.. parcel.Waybill.Where(char.IsAsciiDigit)]);
            if (digits.Length < 6) continue;

            // The takeover date belongs to the transfer here, not to the file: the list reaches
            // back months and those parcels were entered by hand long ago.
            var payout = payouts.FirstOrDefault(p =>
                !taken.Contains(p.EntryId)
                && p.BookedOn.Date >= _options.PostFrom.Date
                && Math.Abs(p.Amount - parcel.Amount) <= 0.004m
                && p.Digits.Contains(digits, StringComparison.Ordinal));

            if (payout is null) continue;

            taken.Add(payout.EntryId);
            paid[parcel.Waybill] = payout;
        }

        if (paid.Count == 0)
        {
            logger.LogDebug(
                "No transfers yet for any of the {Count} parcels in the {Courier} report {File}.",
                report.Parcels.Count, row.Courier, Path.GetFileName(row.FilePath));

            return summary with { Waiting = summary.Waiting + 1 };
        }

        logger.LogInformation(
            "The {Courier} report {File}: {Paid} of {Count} parcels have their own transfer, " +
            "worth {Total:N2} together.",
            row.Courier, Path.GetFileName(row.FilePath), paid.Count, report.Parcels.Count,
            paid.Values.Sum(p => p.Amount));

        // Every entry is dated by its own transfer, so the date passed here is never the one used -
        // only the parcels in "paid" are proposed at all. It is stated as the file's date rather
        // than today's so that loosening that filter would show up as an obviously wrong date
        // rather than as a silent "booked today".
        var counts = await ProposeAsync(
            report, row.Courier, row.FilePath, report.PayoutDate, settleable: true,
            posted, runId, cancellationToken, paid);

        var remaining = report.Parcels.Count(p => !posted.Contains(p.Waybill) && !paid.ContainsKey(p.Waybill));

        await store.SaveAsync(row with
        {
            // The row stays open while any parcel could still be paid: this file is a list, and the
            // next transfer against it may be weeks away.
            Status = remaining == 0 ? CodReportStatus.Posted : CodReportStatus.Pending,
            ParcelCount = report.Parcels.Count,
            Note = $"{paid.Count} parcels paid one by one, {remaining} still waiting",
        }, cancellationToken);

        return summary with
        {
            Posted = summary.Posted + (remaining == 0 ? 1 : 0),
            Waiting = summary.Waiting + (remaining == 0 ? 0 : 1),
            ParcelsProposed = summary.ParcelsProposed + counts.Proposed,
            ParcelsSkipped = summary.ParcelsSkipped + counts.Skipped,
            WithoutDocuments = summary.WithoutDocuments + counts.WithoutDocuments,
        };
    }

    /// <summary>
    /// A report whose parcels are paid in groups, each group named by the transfer that pays it.
    /// </summary>
    /// <remarks>
    /// Diera only. Its file is a running list like Hellmann's - no total, no reference - but its
    /// money arrives for several parcels at once, under a title that names them:
    /// "ZWROT POBRAN 2602153784,2602156427" against 4 101,93, which is 1 940,33 and 2 161,60.
    ///
    /// So the transfer carries its own check and the file needs none: the parcels it pays for have
    /// to add up to exactly what it is worth. Two payouts from the register were taken apart that
    /// way before this was written, 4 101,93 and 5 397,56, and both came out to the grosz.
    ///
    /// The title is where a group starts but not where it ends. A number typed wrongly, or a title
    /// the bank cut short, would otherwise leave the whole transfer unsettled for the sake of one
    /// parcel, so what the title misses is looked for by amount instead - and taken only when
    /// exactly one combination of the remaining parcels closes the difference.
    ///
    /// What the title must do is name at least one parcel of the file. The candidates are every
    /// incoming transfer of a month on every register - some four thousand of them - and among that
    /// many a sum can be met by coincidence. One named parcel is what says the money is Diera's.
    /// </remarks>
    private async Task<CodSummary> SettleNamedGroupsAsync(
        CodReportRow row, CodReport report, IReadOnlyList<CodPayout> payouts, HashSet<string> posted,
        int runId, CodSummary summary, CancellationToken cancellationToken)
    {
        var paid = new Dictionary<string, CodPayout>(StringComparer.OrdinalIgnoreCase);
        var groups = 0;

        foreach (var payout in payouts)
        {
            // The takeover date belongs to the transfer here, not to the file: the list reaches
            // back over parcels that were settled by hand long ago.
            if (payout.BookedOn.Date < _options.PostFrom.Date) continue;

            var free = report.Parcels
                .Where(p => !posted.Contains(p.Waybill) && !paid.ContainsKey(p.Waybill))
                .ToList();

            if (free.Count == 0) break;
            if (GroupFor(payout, free) is not { } group) continue;

            foreach (var parcel in group) paid[parcel.Waybill] = payout;
            groups++;

            logger.LogDebug(
                "Transfer {Entry} of {Amount:N2} booked {Day:yyyy-MM-dd} pays {Count} parcels of the "
                + "{Courier} report: {Parcels}.",
                payout.EntryId, payout.Amount, payout.BookedOn, group.Count, row.Courier,
                string.Join(", ", group.Select(p => p.Waybill)));
        }

        if (paid.Count == 0)
        {
            logger.LogDebug(
                "No transfers yet for any of the {Count} parcels in the {Courier} report {File}.",
                report.Parcels.Count, row.Courier, Path.GetFileName(row.FilePath));

            return summary with { Waiting = summary.Waiting + 1 };
        }

        logger.LogInformation(
            "The {Courier} report {File}: {Paid} of {Count} parcels are covered by {Groups} transfers, "
            + "worth {Total:N2} together.",
            row.Courier, Path.GetFileName(row.FilePath), paid.Count, report.Parcels.Count, groups,
            paid.Values.Distinct().Sum(p => p.Amount));

        // Dated by its own transfer, as in the per-parcel branch - see the note there on why the
        // date passed here is the file's own.
        var counts = await ProposeAsync(
            report, row.Courier, row.FilePath, report.PayoutDate, settleable: true,
            posted, runId, cancellationToken, paid);

        var remaining = report.Parcels.Count(p => !posted.Contains(p.Waybill) && !paid.ContainsKey(p.Waybill));

        await store.SaveAsync(row with
        {
            // The row stays open while any parcel could still be paid: this file is a list, and the
            // next transfer against it may be weeks away.
            Status = remaining == 0 ? CodReportStatus.Posted : CodReportStatus.Pending,
            ParcelCount = report.Parcels.Count,
            Note = $"{paid.Count} parcels paid by {groups} transfers, {remaining} still waiting",
        }, cancellationToken);

        return summary with
        {
            Posted = summary.Posted + (remaining == 0 ? 1 : 0),
            Waiting = summary.Waiting + (remaining == 0 ? 0 : 1),
            ParcelsProposed = summary.ParcelsProposed + counts.Proposed,
            ParcelsSkipped = summary.ParcelsSkipped + counts.Skipped,
            WithoutDocuments = summary.WithoutDocuments + counts.WithoutDocuments,
        };
    }

    /// <summary>
    /// The parcels one transfer pays for, or null when it pays for none of this file's.
    /// </summary>
    private static List<CodParcel>? GroupFor(CodPayout payout, IReadOnlyList<CodParcel> free)
    {
        var named = free
            .Where(p => OnlyDigits(p.Waybill) is { Length: >= MinimumParcelDigits } digits
                        && payout.Digits.Contains(digits, StringComparison.Ordinal))
            .ToList();

        // Nothing of ours is named, so the money is not ours to take - whatever it adds up to.
        if (named.Count == 0) return null;

        var missing = payout.Amount - named.Sum(p => p.Amount);

        if (Math.Abs(missing) <= 0.004m) return named;

        // The title names more than the transfer is worth. Which of them it really paid for is not
        // something to guess at, so the transfer is left for somebody to look at.
        if (missing < 0m) return null;

        // The title fell short of the money. Whatever it left out has to be among the parcels still
        // waiting, and it is taken only when one combination of them closes the gap exactly - two
        // ways of making up the difference are two different answers, and neither is evidence.
        var rest = free.Where(p => !named.Contains(p)).ToList();

        var solutions = SubsetSumSolver.Solve(
            [.. rest.Select(p => SubsetSumSolver.ToCents(p.Amount))],
            SubsetSumSolver.ToCents(missing),
            MaximumParcelsMadeUp,
            tolerance: 0,
            maxSolutions: 2);

        if (solutions.Count != 1) return null;

        return [.. named, .. solutions[0].Indices.Select(i => rest[i])];
    }

    private static string OnlyDigits(string value) => new([.. value.Where(char.IsAsciiDigit)]);

    private async Task<(int Proposed, int Skipped, int WithoutDocuments)> ProposeAsync(
        CodReport report, string courier, string path, DateTime bookedOn, bool settleable,
        HashSet<string> posted, int runId, CancellationToken cancellationToken,
        IReadOnlyDictionary<string, CodPayout>? perParcel = null)
    {
        var pending = report.Parcels.Where(p => !posted.Contains(p.Waybill)).ToList();

        // Counted before the next filter, so that "already in ERP" means exactly that. With a
        // courier paying parcel by parcel most of the file is neither posted nor payable yet, and
        // lumping the two together would report 62 parcels as booked that nobody has booked.
        var skipped = report.Parcels.Count - pending.Count;

        // Parcels whose transfer has not arrived are not proposed at all - with a courier paying
        // one at a time there is no reason to book a parcel before its money is here.
        if (perParcel is not null) pending = [.. pending.Where(p => perParcel.ContainsKey(p.Waybill))];

        if (pending.Count == 0) return (0, skipped, 0);

        // Hellmann's file carries no waybill, only the invoice number somebody typed in, so there
        // is nothing to look up in the shipment tables and the number is followed straight to the
        // document - forgivingly, because it is typed by hand.
        var byWaybill = perParcel is not null
            ? new Dictionary<string, List<CodDocument>>(StringComparer.OrdinalIgnoreCase)
            : await erp.ByWaybillAsync([.. pending.Select(p => p.Waybill)], cancellationToken);

        // Only for the parcels shipping has no record of - a cancelled and resent shipment loses
        // its link, and then the number printed on the report is all there is.
        var unresolved = pending.Where(p => !byWaybill.ContainsKey(p.Waybill)).ToList();

        var byNumber = unresolved.Count == 0
            ? new Dictionary<string, List<CodDocument>>(StringComparer.OrdinalIgnoreCase)
            : perParcel is not null
                ? await erp.ByPrintedNumberLooselyAsync(
                    [.. unresolved.SelectMany(p => p.DocumentNumbers)], cancellationToken)
                : await erp.ByDocumentNumberAsync(
                    [.. unresolved.SelectMany(p => p.DocumentNumbers)], cancellationToken);

        var proposed = 0;
        var withoutDocuments = 0;

        foreach (var parcel in pending)
        {
            var documents = Documents(parcel, byWaybill, byNumber, out var source);

            // With several documents sharing an ordinal across series, the amount says which one is
            // meant - and it is the same amount the transfer was worth, so nothing is guessed.
            if (perParcel is not null && documents.Count > 1)
            {
                var onAmount = documents
                    .Where(d => Math.Abs(d.Remaining - parcel.Amount) <= 0.004m)
                    .ToList();

                if (onAmount.Count == 1) documents = onAmount;
            }

            if (documents.Count == 0) withoutDocuments++;

            // Penny for penny, or not at all. A parcel worth less than the invoice it points at
            // would otherwise be settled in part, and a part settlement nobody asked for is worse
            // than an open item somebody looks at.
            var outstanding = documents.Sum(d => d.Remaining);
            var exact = documents.Count > 0 && Math.Abs(outstanding - parcel.Amount) <= 0.004m;

            var payout = perParcel is not null ? perParcel[parcel.Waybill] : null;

            var entry = new CodEntry(
                StableId(courier, parcel.Waybill),
                _options.Register,
                courier,
                payout?.BookedOn ?? bookedOn,
                parcel.Waybill,
                parcel.Amount,
                parcel.Recipient,
                CodDescription.For(parcel.Waybill, courier),
                documents.FirstOrDefault()?.ContractorId ?? 0,
                settleable && exact ? "High" : "None",
                Note(parcel, documents, source, outstanding, exact, settleable),
                path,
                documents,
                payout?.EntryId ?? 0);

            if (await store.SaveEntryAsync(entry, runId, cancellationToken)) proposed++;
        }

        return (proposed, skipped, withoutDocuments);
    }

    /// <summary>
    /// The documents a parcel is to close: what shipping recorded, or failing that what the report
    /// printed.
    /// </summary>
    private static List<CodDocument> Documents(
        CodParcel parcel,
        IReadOnlyDictionary<string, List<CodDocument>> byWaybill,
        IReadOnlyDictionary<string, List<CodDocument>> byNumber,
        out string source)
    {
        if (byWaybill.TryGetValue(parcel.Waybill, out var found) && found.Count > 0)
        {
            source = "wysyłka";
            return found;
        }

        source = "numer z zestawienia";

        return [.. parcel.DocumentNumbers
            .SelectMany(n => byNumber.TryGetValue(n, out var d) ? d : [])
            .DistinctBy(d => (d.DocType, d.DocId, d.DocLp))];
    }

    /// <summary>What is written on the row, so an accountant can see what happened to the parcel.</summary>
    private static string Note(
        CodParcel parcel, IReadOnlyList<CodDocument> documents, string source,
        decimal outstanding, bool exact, bool settleable)
    {
        if (documents.Count == 0)
        {
            return parcel.DocumentNumbers.Count == 0
                ? "Zestawienie nie podaje dokumentu, a wysyłki nie ma w ERP."
                : $"Nie znaleziono nierozliczonych płatności dokumentów: {string.Join(", ", parcel.DocumentNumbers)}.";
        }

        var numbers = string.Join(", ", documents.Select(d => d.DocNumber).Distinct());

        if (!exact)
        {
            return $"Pobranie {parcel.Amount:N2} nie zgadza się co do grosza z pozostałą kwotą "
                   + $"dokumentów {numbers} ({outstanding:N2}) – do rozliczenia ręcznego.";
        }

        return settleable
            ? $"Pobranie dopasowane przez {source}: {numbers}."
            : $"Pobranie dopasowane przez {source}: {numbers}, ale zestawienie nie zgadza się z przelewem.";
    }

    /// <summary>
    /// Sets aside the courier transfers whose reports have been settled in full.
    /// </summary>
    /// <remarks>
    /// Run after posting, because it is only then that a parcel counts as settled. All or nothing:
    /// one parcel of a report left open means the transfer still has something to answer for.
    /// </remarks>
    public async Task<int> CloseSettledPayoutsAsync(CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled) return 0;

        var closed = 0;

        foreach (var row in await store.GetFullySettledAsync(cancellationToken))
        {
            var description =
                $"Rozpisane na paczki w {_options.Register} wg zestawienia {row.Courier} "
                + $"{Path.GetFileNameWithoutExtension(row.FilePath)}";

            if (!await store.CloseCodPayoutAsync(row.PayoutEntryId!.Value, description, cancellationToken))
            {
                continue;
            }

            closed++;

            logger.LogInformation(
                "Transfer {Entry} from {Courier} flagged as not subject to settlement - all {Count} " +
                "of its parcels are settled in {Register}.",
                row.PayoutEntryId, row.Courier, row.ParcelCount, _options.Register);
        }

        // The couriers that pay parcel by parcel: there the transfer to set aside belongs to the
        // parcel, not to the report, so it is the settled parcels that name it.
        foreach (var (entryId, courier, file) in await store.GetSettledParcelPayoutsAsync(cancellationToken))
        {
            var description =
                $"Rozpisane na paczki w {_options.Register} wg zestawienia "
                + $"{Path.GetFileNameWithoutExtension(file)}";

            if (!await store.CloseCodPayoutAsync(entryId, description, cancellationToken)) continue;

            closed++;

            logger.LogInformation(
                "Transfer {Entry} from a {Source} parcel flagged as not subject to settlement - " +
                "its parcel is settled in {Register}.",
                entryId, courier, _options.Register);
        }

        return closed;
    }

    /// <summary>
    /// Saves the report next to the bank statements. An archive that fails is logged and let go.
    /// </summary>
    private string Archive(string courier, CodReport report, CodAttachment attachment)
    {
        if (!_archive.IsConfigured) return string.Empty;

        var path = ArchivePaths.CodReport(
            _archive.Root, courier, report.PayoutDate, report.Reference, attachment.FileName);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            // Written under a temporary name and moved into place, so that a process killed
            // halfway cannot leave a half a spreadsheet behind.
            var temporary = path + ".part";
            File.WriteAllBytes(temporary, attachment.Content);
            File.Move(temporary, path, overwrite: true);

            return path;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception,
                "Could not save the report {File} to the archive at {Path}.", attachment.FileName, path);
            return string.Empty;
        }
    }

    private Task Ignore(
        CodMessage message, CodAttachment attachment, string courier, string format, string reason) =>
        store.SaveAsync(CodReportRow.For(message, attachment, CodReportStatus.Ignored) with
        {
            Courier = courier,
            Format = format,
            Note = reason,
        });

    /// <summary>
    /// A repeatable identifier for a parcel, so that reading the same report twice writes the same
    /// row rather than a second one.
    /// </summary>
    /// <remarks>
    /// FNV-1a over courier and waybill, the same scheme the bank operations use - .NET's own string
    /// hash is randomised per process and would give a different answer after every restart.
    /// </remarks>
    private static long StableId(string courier, string waybill)
    {
        var hash = 14695981039346656037UL;

        foreach (var ch in $"COD#{courier}#{waybill}")
        {
            hash ^= ch;
            hash *= 1099511628211UL;
        }

        return (long)(hash & 0x7FFF_FFFF_FFFF_FFFFUL);
    }
}
