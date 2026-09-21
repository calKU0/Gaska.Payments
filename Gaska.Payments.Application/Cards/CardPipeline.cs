using System.Globalization;
using System.Text.RegularExpressions;

using Gaska.Payments.Application.Couriers;
using Gaska.Payments.Domain.Cards;
using Gaska.Payments.Domain.Couriers;
using Gaska.Payments.Domain.Model;
using Gaska.Payments.Erp;
using Gaska.Payments.Integrations.Archive;
using Gaska.Payments.Integrations.Cards;
using Gaska.Payments.Integrations.Couriers;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Gaska.Payments.Application.Cards;

/// <summary>What one pass over the card register came to.</summary>
public sealed record CardSummary(
    int ReportsRead, int Transactions, int ToSettle, int WithoutSettlement, int Skipped, int Commissions)
{
    public static readonly CardSummary Nothing = new(0, 0, 0, 0, 0, 0);
}

/// <summary>
/// Books the payments taken on our card terminals onto KARTA, from Fiserv's reports.
/// </summary>
/// <remarks>
/// Three steps, each on whatever the one before left behind:
///
/// <list type="number">
/// <item>Reports are taken from the mailbox and archived - only Fiserv's; everything else in the
/// mailbox is the couriers' business, and this step runs before theirs so that each message is
/// claimed by whoever it belongs to.</item>
/// <item>Each transaction becomes an entry: a sale KP-K, a refund KW-K, dated the day it was made,
/// on the register's monthly report. The document it pays is the receipt or invoice whose payment
/// notes carry its transaction number - where the cashier has always written it. When exactly one
/// open document has that number and the same amount to the grosz, the entry is settled against
/// it; otherwise it is booked unsettled and the accountants settle it in the application.</item>
/// <item>When Fiserv's transfer for a batch arrives, the commission is what the batch came to less
/// what was transferred, booked as PROW on POLCARD. Fiserv states the commission in the title
/// too, and for a transfer covering one point the two have to agree before anything is booked.</item>
/// </list>
/// </remarks>
public sealed partial class CardPipeline(
    CodMailbox mailbox,
    CodStore reports,
    CardStore store,
    ShipmentReader erp,
    FiservReportReader reader,
    IOptions<CardOptions> options,
    IOptions<ArchiveOptions> archiveOptions,
    ILogger<CardPipeline> logger)
{
    private const string Courier = "Fiserv";
    private const string SaleText = "Przychód z karty płatniczej";
    private const string RefundText = "Rozchód z karty płatniczej";
    private const string FeeText = "Prowizja POLCARD";

    /// <summary>How many batches one transfer may pay for. Fiserv pays one, or two over a weekend.</summary>
    private const int MaximumBatchesPerTransfer = 4;

    private readonly CardOptions _options = options.Value;
    private readonly ArchiveOptions _archive = archiveOptions.Value;

    public async Task<CardSummary> RunAsync(int runId, CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled) return CardSummary.Nothing;

        var read = await CollectAsync(cancellationToken);
        var booked = await BookWaitingAsync(runId, cancellationToken);
        var commissions = await BookCommissionsAsync(runId, cancellationToken);

        return booked with { ReportsRead = read, Commissions = commissions };
    }

    // ---------------------------------------------------------------- mailbox ---

    /// <summary>Takes Fiserv's reports from the mailbox and leaves them waiting to be booked.</summary>
    /// <returns>How many reports were read.</returns>
    private async Task<int> CollectAsync(CancellationToken cancellationToken)
    {
        // A message is fetched again when it was written off as an unreadable XML attachment:
        // before this step existed, the couriers' step saw Fiserv's report and set it aside as
        // something it did not recognise.
        var seen = await reports.GetReadMessagesAsync(cancellationToken);
        var setAside = await store.GetUnrecognisedXmlMessagesAsync(cancellationToken);
        seen.ExceptWith(setAside);

        var messages = await mailbox.FetchAsync(seen, cancellationToken);
        var read = 0;

        foreach (var message in messages)
        {
            foreach (var attachment in message.Attachments)
            {
                if (!reader.Recognises(attachment.FileName, attachment.Content))
                {
                    // Set aside by the couriers' step and not Fiserv's either - looked at once is enough.
                    if (setAside.Contains(message.Uid) && attachment.FileName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                    {
                        await store.MarkNotOursAsync(message.Uid, attachment.FileName, cancellationToken);
                    }

                    continue;
                }

                if (!_options.Accepts(message.Sender))
                {
                    await reports.SaveAsync(CodReportRow.For(message, attachment, CodReportStatus.Ignored) with
                    {
                        Courier = Courier,
                        Format = FiservReportReader.FormatName,
                        Note = $"sender {message.Sender} is not on the list under Cards:Senders",
                    }, cancellationToken);

                    logger.LogWarning(
                        "A Fiserv report {File} came from {Sender}, who is not under Cards:Senders - not booked.",
                        attachment.FileName, message.Sender);
                    continue;
                }

                CardReport report;

                try
                {
                    report = reader.Read(attachment.Content);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // Not recorded: a file we failed on is worth another look after a fix.
                    logger.LogError(exception, "Could not read the Fiserv report {File}.", attachment.FileName);
                    continue;
                }

                var path = Archive(report, attachment);

                await reports.SaveAsync(CodReportRow.For(message, attachment, CodReportStatus.Pending) with
                {
                    Courier = Courier,
                    Format = FiservReportReader.FormatName,
                    FilePath = path,
                    PayoutDate = report.PeriodFrom,
                    PayoutTotal = report.Transactions.Sum(t => t.SignedAmount),
                    ParcelCount = report.Transactions.Count,
                }, cancellationToken);

                read++;

                logger.LogInformation(
                    "Read a Fiserv report {File}: {Count} transactions worth {Total:N2} from {From:yyyy-MM-dd} "
                    + "to {To:yyyy-MM-dd}, batches {Batches}.",
                    attachment.FileName, report.Transactions.Count, report.Transactions.Sum(t => t.SignedAmount),
                    report.PeriodFrom, report.PeriodTo,
                    string.Join(", ", report.Transactions.Select(t => t.BatchKey).Distinct()));
            }
        }

        return read;
    }

    // ---------------------------------------------------------------- entries ---

    /// <summary>Turns the transactions of every waiting report into entries to be posted.</summary>
    private async Task<CardSummary> BookWaitingAsync(int runId, CancellationToken cancellationToken)
    {
        var summary = CardSummary.Nothing;

        var waiting = (await reports.GetPendingAsync(cancellationToken))
            .Where(r => r.Format == FiservReportReader.FormatName)
            .ToList();

        foreach (var row in waiting)
        {
            if (!File.Exists(row.FilePath))
            {
                logger.LogError("The archived Fiserv report {File} is gone - it cannot be booked.", row.FilePath);
                continue;
            }

            CardReport report;

            try
            {
                report = reader.Read(await File.ReadAllBytesAsync(row.FilePath, cancellationToken));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "Could not read the archived Fiserv report {File} again.", row.FilePath);
                continue;
            }

            var result = await BookAsync(report, row.FilePath, runId, cancellationToken);

            await reports.SaveAsync(row with
            {
                Status = CodReportStatus.Posted,
                Note = $"{result.Transactions} new entries: {result.ToSettle} to settle, "
                       + $"{result.WithoutSettlement} without settlement; {result.Skipped} booked already or left alone",
            }, cancellationToken);

            summary = summary with
            {
                Transactions = summary.Transactions + result.Transactions,
                ToSettle = summary.ToSettle + result.ToSettle,
                WithoutSettlement = summary.WithoutSettlement + result.WithoutSettlement,
                Skipped = summary.Skipped + result.Skipped,
            };
        }

        return summary;
    }

    private async Task<CardSummary> BookAsync(CardReport report, string path, int runId, CancellationToken cancellationToken)
    {
        var ours = report.Transactions.Where(t => t.Date >= _options.PostFrom.Date).ToList();
        var before = report.Transactions.Count - ours.Count;

        if (before > 0)
        {
            logger.LogInformation(
                "{Count} transactions of the Fiserv report are older than Cards:PostFrom {From:yyyy-MM-dd} - "
                + "those days are the accountants' and are left alone.",
                before, _options.PostFrom);
        }

        if (ours.Count == 0) return CardSummary.Nothing with { Skipped = before };

        var window = Math.Max(0, _options.DocumentWindowDays);
        var documents = await store.FindDocumentsAsync(
            [.. ours.Select(t => t.Number).Distinct()],
            ours.Min(t => t.Date).AddDays(-window),
            ours.Max(t => t.Date),
            cancellationToken);

        // Counted only for entries this pass actually adds. A report read a second time finds its
        // transactions booked already and their documents settled - reporting them again as
        // "without settlement" would say the opposite of what happened.
        int toSettle = 0, without = 0, known = 0, skipped = before;

        foreach (var transaction in ours)
        {
            if (transaction.Kind == CardTransactionKind.Other)
            {
                // Cash withdrawn at the till, or cashback. Never booked on KARTA before, and
                // nothing to settle it against.
                logger.LogWarning(
                    "Card transaction {Number} of {Amount:N2} on {Day:yyyy-MM-dd} is of type {Type} - neither a sale "
                    + "nor a refund, so nothing is booked for it. It has to be entered by hand if it belongs on KARTA.",
                    transaction.Number, transaction.Amount, transaction.Date, transaction.TypeCode);
                skipped++;
                continue;
            }

            var (document, note) = Pick(transaction, documents.GetValueOrDefault(transaction.Number) ?? [], window);

            var entry = new CardEntry(
                StableId("POLCARD", transaction.Point, transaction.Number, transaction.Date),
                $"POLCARD {transaction.Point}/{transaction.Number}",
                _options.Register,
                PaymentCategory.Polcard,
                transaction.Date,
                transaction.Amount,
                transaction.Kind == CardTransactionKind.Sale,
                document?.ContractorId ?? 0,
                transaction.Kind == CardTransactionKind.Sale ? SaleText : RefundText,
                // The transaction number goes where the accountants have always looked for it.
                transaction.Number,
                document is null ? "None" : "High",
                note,
                path,
                transaction.BatchKey,
                document);

            if (!await store.SaveEntryAsync(entry, runId, cancellationToken)) known++;
            else if (document is null) without++;
            else toSettle++;
        }

        logger.LogInformation(
            "The Fiserv report: {New} new entries - {Settle} to be settled against their document, {Without} "
            + "without settlement for the accountants; {Known} booked already, {Skipped} left alone.",
            toSettle + without, toSettle, without, known, skipped);

        return new CardSummary(0, toSettle + without, toSettle, without, skipped + known, 0);
    }

    /// <summary>
    /// The document a transaction settles, and what is written on the entry about it.
    /// </summary>
    /// <remarks>
    /// Only a document it is certain of: its notes carry the transaction number, it was issued in
    /// the week up to the payment, it is on the right side - a receipt or invoice for a sale, a
    /// correction for a refund - and what is left on it is the transaction's amount to the grosz.
    /// Anything short of that is booked unsettled with the reason, because a card payment settled
    /// against the wrong receipt is harder to find than one not settled at all.
    /// </remarks>
    private static (CardDocument? Document, string Note) Pick(
        CardTransaction transaction, IReadOnlyList<CardDocument> candidates, int window)
    {
        var side = transaction.Kind == CardTransactionKind.Refund ? 1 : 2;
        var onSide = candidates.Where(d => d.PaymentType == side).ToList();

        if (onSide.Count == 0)
        {
            return (null, $"Transakcja kartą nr {transaction.Number} z {transaction.Date:dd.MM.yyyy}: brak otwartego "
                          + $"dokumentu z tym numerem w notatce płatności z {window} dni wstecz – do rozliczenia ręcznego.");
        }

        var exact = onSide.Where(d => Math.Abs(d.Remaining - transaction.Amount) <= 0.004m).ToList();

        if (exact.Count == 1)
        {
            return (exact[0], $"Transakcja kartą nr {transaction.Number}: {exact[0].DocNumber}.");
        }

        var named = string.Join(", ", onSide.Select(d => $"{d.DocNumber} ({d.Remaining:N2})"));

        return exact.Count > 1
            ? (null, $"Transakcja kartą nr {transaction.Number}: kilka dokumentów na tę samą kwotę – {named} – do rozliczenia ręcznego.")
            : (null, $"Transakcja kartą nr {transaction.Number} na {transaction.Amount:N2}, a dokument z tym numerem "
                     + $"ma do zapłaty inną kwotę: {named} – do rozliczenia ręcznego.");
    }

    // ------------------------------------------------------------- commission ---

    /// <summary>Books the commission of every batch whose transfer from Fiserv has arrived.</summary>
    /// <returns>How many commissions were booked.</returns>
    private async Task<int> BookCommissionsAsync(int runId, CancellationToken cancellationToken)
    {
        var batches = await store.GetUnpaidBatchesAsync(cancellationToken);
        if (batches.Count == 0) return 0;

        var used = await store.GetUsedPayoutsAsync(cancellationToken);
        var from = batches.Min(b => b.LastDay);

        var payouts = (await erp.GetPayoutsAsync(_options.PayoutRegisters, from, DateTime.Today, cancellationToken))
            .Where(p => !used.Contains(p.EntryId) && p.Compact.Contains("/OPF/", StringComparison.Ordinal))
            .OrderBy(p => p.BookedOn)
            .ThenBy(p => p.EntryId)
            .ToList();

        var open = batches.ToDictionary(b => b.Key, StringComparer.Ordinal);
        var booked = 0;

        foreach (var payout in payouts)
        {
            var paid = BatchesPaidBy(payout, [.. open.Values]);
            if (paid is null) continue;

            var net = paid.Sum(b => b.Net);
            var commission = net - payout.Amount;
            var sales = paid.Sum(b => b.Sales);

            if (commission < -0.004m || commission > sales * _options.MaximumCommissionShare)
            {
                logger.LogWarning(
                    "Fiserv's transfer {Entry} of {Amount:N2} on {Day:yyyy-MM-dd} looks like batches {Batches}, but they "
                    + "come to {Net:N2} - a commission of {Commission:N2} is not believable, so none is booked.",
                    payout.EntryId, payout.Amount, payout.BookedOn, string.Join(", ", paid.Select(b => b.Key)), net, commission);
                continue;
            }

            if (!AgreesWithStatedCommission(payout, paid, commission))
            {
                logger.LogWarning(
                    "Fiserv's transfer {Entry} of {Amount:N2} pays batches {Batches}, which leaves a commission of "
                    + "{Commission:N2}, but the title does not state that commission. Not booked - a refund or a "
                    + "transaction missing from the reports would do this.",
                    payout.EntryId, payout.Amount, string.Join(", ", paid.Select(b => b.Key)), commission);
                continue;
            }

            var keys = paid.Select(b => b.Key).ToList();

            var fee = new CardEntry(
                StableId("POLCARD-FEE", payout.EntryId.ToString(CultureInfo.InvariantCulture), "", payout.BookedOn),
                $"PROWIZJA POLCARD {payout.EntryId}",
                _options.Register,
                PaymentCategory.PolcardFee,
                payout.BookedOn,
                Math.Round(commission, 2),
                IsIncoming: false,
                _options.FeeContractorId,
                FeeText,
                $"zbiorówki {string.Join(", ", keys)}",
                "None",
                $"Prowizja Fiserv: zbiorówki {string.Join(", ", keys)} na {net:N2}, przelew {payout.EntryId} "
                + $"z {payout.BookedOn:dd.MM.yyyy} na {payout.Amount:N2}.",
                string.Empty,
                string.Empty,
                Document: null,
                payout.EntryId);

            // A batch Fiserv paid in full leaves nothing to book, and XL will not take an entry for
            // nothing - the batches are marked paid all the same.
            if (await store.SaveCommissionAsync(fee, keys, runId, bookFee: commission > 0.004m, cancellationToken))
            {
                booked++;
                foreach (var key in keys) open.Remove(key);

                logger.LogInformation(
                    "Commission of {Commission:N2} on Fiserv's transfer {Entry} of {Amount:N2} booked {Day:yyyy-MM-dd}: "
                    + "batches {Batches} came to {Net:N2}.",
                    commission, payout.EntryId, payout.Amount, payout.BookedOn, string.Join(", ", keys), net);
            }
        }

        foreach (var late in open.Values.Where(b => b.LastDay < DateTime.Today.AddDays(-_options.PayoutWindowDays)))
        {
            // Not necessarily no transfer: one that pays this batch together with a batch the
            // service never saw - from before Cards:PostFrom, or from a report that did not
            // arrive - cannot be taken apart, and waits here just the same.
            logger.LogWarning(
                "Batch {Batch} of {Count} card transactions worth {Net:N2}, last on {Day:yyyy-MM-dd}: no transfer "
                + "from Fiserv could be paired with it in {Days} days, so its commission is not booked. It has to be "
                + "entered by hand - a transfer that also paid a batch missing from the reports would do this.",
                late.Key, late.Count, late.Net, late.LastDay, _options.PayoutWindowDays);
        }

        return booked;
    }

    /// <summary>
    /// The batches a transfer pays, or null when it cannot be said for certain.
    /// </summary>
    /// <remarks>
    /// Fiserv's title names each point it pays for after a backslash, then the batches, their count,
    /// what they came to and the commission: "\73346135 105 26 1339,32 -24,47". The bank breaks it
    /// every 35 characters, often inside a number, so it is read with the spaces taken out.
    ///
    /// Every point the title names has to be accounted for by batches of our own: a run of one to
    /// four of that point's unpaid batches whose numbers are all in the title and whose sales total
    /// is in it too, digit for digit. Exactly one such run per point, or the transfer waits - a
    /// point we have no transactions for would otherwise have its money counted as commission.
    /// </remarks>
    private static List<CardBatch>? BatchesPaidBy(CodPayout payout, IReadOnlyList<CardBatch> open)
    {
        var points = PointInTitle().Matches(payout.Compact).Select(m => m.Groups["point"].Value).Distinct().ToList();
        if (points.Count == 0) return null;

        var paid = new List<CardBatch>();

        foreach (var point in points)
        {
            var mine = open
                .Where(b => b.Point == point)
                .OrderBy(b => int.TryParse(b.Batch, out var n) ? n : int.MaxValue)
                .ToList();

            var runs = new List<List<CardBatch>>();

            for (var start = 0; start < mine.Count; start++)
            {
                for (var length = 1; length <= MaximumBatchesPerTransfer && start + length <= mine.Count; length++)
                {
                    var run = mine.GetRange(start, length);

                    var named = run.All(b => payout.Digits.Contains(b.Batch, StringComparison.Ordinal));
                    var total = Digits(run.Sum(b => b.Sales));

                    if (named && payout.Digits.Contains(total, StringComparison.Ordinal)) runs.Add(run);
                }
            }

            if (runs.Count != 1) return null;
            paid.AddRange(runs[0]);
        }

        return paid;
    }

    /// <summary>
    /// Whether the commission worked out is the one Fiserv states - checked where it can be.
    /// </summary>
    /// <remarks>
    /// A title covering one point states one commission, "-24,47", and it has to be ours to the
    /// grosz. A title covering two states one each and nothing says how our total divides between
    /// them, so there the check is the one already made: every point's sales found in the title.
    /// </remarks>
    private static bool AgreesWithStatedCommission(CodPayout payout, IReadOnlyList<CardBatch> paid, decimal commission)
    {
        if (paid.Select(b => b.Point).Distinct().Count() > 1) return true;

        var stated = "-" + commission.ToString("0.00", CultureInfo.GetCultureInfo("pl-PL"));
        return payout.Compact.Contains(stated, StringComparison.Ordinal);
    }

    /// <summary>An amount as the digits it is written with - 1339,32 as "133932".</summary>
    private static string Digits(decimal amount) =>
        amount.ToString("0.00", CultureInfo.InvariantCulture).Replace(".", string.Empty, StringComparison.Ordinal);

    [GeneratedRegex(@"\\(?<point>\d{8})")]
    private static partial Regex PointInTitle();

    // ----------------------------------------------------------------- helpers ---

    private string Archive(CardReport report, CodAttachment attachment)
    {
        if (!_archive.IsConfigured) return string.Empty;

        var path = ArchivePaths.CodReport(_archive.Root, Courier, report.PeriodFrom, report.Merchant, attachment.FileName);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            var temporary = path + ".part";
            File.WriteAllBytes(temporary, attachment.Content);
            File.Move(temporary, path, overwrite: true);

            return path;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Could not save the Fiserv report {File} to the archive at {Path}.",
                attachment.FileName, path);
            return string.Empty;
        }
    }

    /// <summary>
    /// A repeatable identifier, so that reading a report twice writes the same rows rather than a
    /// second set. FNV-1a, as the couriers and the bank operations use.
    /// </summary>
    private static long StableId(string kind, string point, string number, DateTime day)
    {
        var hash = 14695981039346656037UL;

        foreach (var ch in $"{kind}#{point}#{number}#{day:yyyyMMdd}")
        {
            hash ^= ch;
            hash *= 1099511628211UL;
        }

        return (long)(hash & 0x7FFF_FFFF_FFFF_FFFFUL);
    }
}
