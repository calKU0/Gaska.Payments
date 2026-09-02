using Gaska.Payments.Application.Settlement;
using Gaska.Payments.Domain.Diagnostics;
using Gaska.Payments.Integrations.Archive;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Gaska.Payments.Domain.Couriers;
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

    private async Task<(int Proposed, int Skipped, int WithoutDocuments)> ProposeAsync(
        CodReport report, string courier, string path, DateTime bookedOn, bool settleable,
        HashSet<string> posted, int runId, CancellationToken cancellationToken)
    {
        var pending = report.Parcels.Where(p => !posted.Contains(p.Waybill)).ToList();
        var skipped = report.Parcels.Count - pending.Count;

        if (pending.Count == 0) return (0, skipped, 0);

        var byWaybill = await erp.ByWaybillAsync([.. pending.Select(p => p.Waybill)], cancellationToken);

        // Only for the parcels shipping has no record of - a cancelled and resent shipment loses
        // its link, and then the number printed on the report is all there is.
        var unresolved = pending.Where(p => !byWaybill.ContainsKey(p.Waybill)).ToList();

        var byNumber = unresolved.Count == 0
            ? new Dictionary<string, List<CodDocument>>(StringComparer.OrdinalIgnoreCase)
            : await erp.ByDocumentNumberAsync(
                [.. unresolved.SelectMany(p => p.DocumentNumbers)], cancellationToken);

        var proposed = 0;
        var withoutDocuments = 0;

        foreach (var parcel in pending)
        {
            var documents = Documents(parcel, byWaybill, byNumber, out var source);
            if (documents.Count == 0) withoutDocuments++;

            // Penny for penny, or not at all. A parcel worth less than the invoice it points at
            // would otherwise be settled in part, and a part settlement nobody asked for is worse
            // than an open item somebody looks at.
            var outstanding = documents.Sum(d => d.Remaining);
            var exact = documents.Count > 0 && Math.Abs(outstanding - parcel.Amount) <= 0.004m;

            var entry = new CodEntry(
                StableId(courier, parcel.Waybill),
                _options.Register,
                courier,
                bookedOn,
                parcel.Waybill,
                parcel.Amount,
                parcel.Recipient,
                CodDescription.For(parcel.Waybill, courier),
                documents.FirstOrDefault()?.ContractorId ?? 0,
                settleable && exact ? "High" : "None",
                Note(parcel, documents, source, outstanding, exact, settleable),
                path,
                documents);

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
