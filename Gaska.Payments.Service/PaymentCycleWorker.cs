using Gaska.Payments.Application.Couriers;
using Gaska.Payments.Application.Posting;
using Gaska.Payments.Application.Settlement;
using Gaska.Payments.Domain.Couriers;
using Gaska.Payments.Domain.Diagnostics;
using Gaska.Payments.Integrations.Bank;
using Gaska.Payments.Integrations.Couriers;
using Microsoft.Extensions.Options;

using Gaska.Payments.Application.Bank;

namespace Gaska.Payments.Service;

/// <summary>
/// The automat's cycle: download operations from BNP, match them against documents, create cash
/// entries in Comarch ERP XL and settle whatever the engine considers certain.
/// </summary>
/// <remarks>
/// It all happens in one process and one pass. Posting used to be a separate program run by hand,
/// with modes - hence the requirement that the service be 32-bit: the XL API is native and comes
/// no other way.
/// </remarks>
public sealed class PaymentCycleWorker(
    BankSettlementPipeline pipeline,
    ErpPostingService posting,
    StatementArchive statements,
    CodPipeline cod,
    IOptions<SettlementOptions> options,
    ILogger<PaymentCycleWorker> logger) : BackgroundService
{
    private readonly SettlementOptions _options = options.Value;

    /// <summary>Which pass this is since the service started - it puts every line in order.</summary>
    private int _pass;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMinutes(_options.IntervalMinutes);

        logger.LogInformation(
            "Service started: cycle every {Interval}, version {Version}, machine {Machine}, " +
            "working directory {Directory}.",
            interval.ToString("c"),
            typeof(PaymentCycleWorker).Assembly.GetName().Version?.ToString() ?? "unknown",
            Environment.MachineName, AppContext.BaseDirectory);

        using var timer = new PeriodicTimer(interval);

        var schemaReady = false;

        do
        {
            // The schema is prepared inside the loop, not before it. Outside, a database that
            // happened to be unreachable at startup took the whole host down instead of being
            // retried in the next window - and a service that dies on a network blip and never
            // comes back is worse than one that waits an hour.
            if (!schemaReady && _options.EnsureSchemaOnStartup)
            {
                schemaReady = await EnsureSchemaAsync(stoppingToken);
                if (!schemaReady) continue;
            }

            await RunOnceAsync(stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>Prepares the tables. Returns false when the database could not be reached.</summary>
    private async Task<bool> EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        try
        {
            await pipeline.EnsureSchemaAsync(cancellationToken);
            logger.LogInformation("Tables pay.* are ready.");
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception,
                "Could not prepare the tables; trying again in {Minutes} min.", _options.IntervalMinutes);
            return false;
        }
    }

    /// <summary>
    /// A single cycle. An error in one pass must not stop the service - it is recorded in
    /// <c>pay.Run</c> and we try again in the next window.
    /// </summary>
    private async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        var pass = Interlocked.Increment(ref _pass);

        using var cycle = TimedOperation.Start(logger, $"Cycle {pass}");

        await ArchiveStatementsAsync(cancellationToken);

        try
        {
            var summary = await pipeline.RunAsync(cancellationToken);

            logger.LogInformation(
                "Cycle {Pass} / run {RunId}: {Fetched} bank operations, {Saved} proposals saved.",
                pass, summary.RunId, summary.PaymentsFetched, summary.ProposalsSaved);

            // Cash on delivery joins the same run: the parcels become rows of the same shape as
            // the bank operations, so the posting below creates their entries and settles them
            // without knowing where they came from.
            await CollectCodAsync(summary.RunId, cancellationToken);

            var posted = await posting.RunAsync(cancellationToken);

            logger.LogInformation(
                "Cycle {Pass} / run {RunId}: {Reports} cash reports, {Entries} cash entries, " +
                "{Settled} settlements, {Split} split payment legs, {Failures} failures.",
                pass, summary.RunId, posted.ReportsCreated, posted.EntriesPosted, posted.PaymentsSettled,
                posted.SplitPaymentsLinked, posted.Failures);

            cycle.Result(
                $"{summary.PaymentsFetched} operations fetched, {posted.EntriesPosted} entries posted, "
                + $"{posted.PaymentsSettled} settled, {posted.Failures} failures");

            // After posting, and only then: a courier transfer whose every parcel has been settled
            // is set aside, so that a five-figure receipt does not sit in the queue for ever.
            await CloseCodPayoutsAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            cycle.Result("failed");
            logger.LogError(exception,
                "Cycle {Pass} failed after {ElapsedMs} ms; trying again in {Minutes} min.",
                pass, cycle.ElapsedMs, _options.IntervalMinutes);
        }
    }

    /// <summary>
    /// Reads the courier payout reports out of the mailbox.
    /// </summary>
    /// <remarks>
    /// Guarded on its own, like the statement archive: a mail server that will not answer is no
    /// reason to stop settling what the bank has already sent us.
    /// </remarks>
    private async Task CollectCodAsync(int runId, CancellationToken cancellationToken)
    {
        try
        {
            using var step = TimedOperation.Start(logger, "Cash on delivery: reading the mailbox");

            var summary = await cod.RunAsync(runId, cancellationToken);

            step.Result(
                $"{summary.FilesArchived} files archived, {summary.Posted} reports paid, "
                + $"{summary.Mismatched} with a divergence, {summary.Waiting} waiting for their transfer");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Reading the cash on delivery mailbox failed; the cycle carries on.");
        }
    }

    /// <summary>
    /// Sets aside the courier transfers whose reports are settled in full.
    /// </summary>
    private async Task CloseCodPayoutsAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var step = TimedOperation.Trace(logger, "Cash on delivery: closing paid-out transfers");

            step.Result($"{await cod.CloseSettledPayoutsAsync(cancellationToken)} transfers set aside");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Could not close the courier transfers; the cycle carries on.");
        }
    }

    /// <summary>
    /// Saves the daily PDF statements to disk.
    /// </summary>
    /// <remarks>
    /// Deliberately outside the settlement cycle and with its own guard: the archive is a
    /// convenience for the accounting team, and a bank that will not hand over a PDF is no reason
    /// to stop posting and settling.
    /// </remarks>
    private async Task ArchiveStatementsAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var step = TimedOperation.Trace(logger, "PDF statement archive");

            var summary = await statements.RunAsync(cancellationToken);

            step.Result(
                $"{summary.Saved} saved, {summary.Refreshed} refreshed (today's still open), "
                + $"{summary.Unavailable} days the bank had none for");

            if (summary.Saved > 0)
            {
                logger.LogInformation("Archived {Saved} PDF statements.", summary.Saved);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "The PDF statement archive failed; the cycle carries on.");
        }
    }
}
