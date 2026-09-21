using System.Diagnostics;

using Gaska.Payments.Application.Advisor;
using Gaska.Payments.Application.Cards;
using Gaska.Payments.Application.Settlement;
using Gaska.Payments.Integrations.Advisor;

using Microsoft.Extensions.Options;

namespace Gaska.Payments.Service;

/// <summary>
/// Asks the language model about the transfers the engine could not settle, and keeps asking as
/// new ones arrive.
/// </summary>
/// <remarks>
/// It runs beside the hourly cycle, not inside it. One answer takes minutes and a morning's
/// statement brings dozens of such transfers: inside the cycle they would hold up posting and
/// settling for hours, and between cycles the model would sit idle. Here the next transfer goes
/// the moment the previous answer is back, the newest first - so what came in this morning has
/// its answer by the time the accountants reach it.
///
/// One question at a time, never more: the model runs on our own machine, and two conversations
/// at once slow both down more than they gain.
///
/// Nothing the model says is settled. Its documents are shown to the accountant, unticked, beside
/// the engine's; the choice stays with a human.
/// </remarks>
public sealed class SettlementAdvisorWorker(
    AdvisorStore store,
    IServiceProvider services,
    SchemaGate schema,
    IOptions<AdvisorOptions> options,
    IOptions<CardOptions> cardOptions,
    ILogger<SettlementAdvisorWorker> logger) : BackgroundService
{
    private readonly AdvisorOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            logger.LogInformation("The settlement advisor is switched off - Advisor:Enabled is false.");
            return;
        }

        var client = services.GetRequiredService<SettlementAdvisorClient>();

        await schema.WaitAsync(stoppingToken);

        var excluded = ExcludedContractors();

        logger.LogInformation(
            "Settlement advisor started: {Url}, one question at a time, transfers with confidence {Confidences} "
            + "from the last {Days} days, payers left out: {Excluded}.",
            _options.Url, string.Join("/", _options.Confidences), _options.LookbackDays, string.Join(", ", excluded));

        try
        {
            var released = await store.ReleaseInterruptedAsync(stoppingToken);
            if (released > 0) logger.LogInformation("{Count} questions left open by the last run will be asked again.", released);
        }
        catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
        {
            logger.LogError(exception, "Could not release the questions the last run left open.");
        }

        var poll = TimeSpan.FromSeconds(Math.Max(10, _options.PollSeconds));

        while (!stoppingToken.IsCancellationRequested)
        {
            var next = await TakeNextAsync(excluded, stoppingToken);

            // Nothing to ask: wait, the next cycle may bring new transfers. Otherwise straight on
            // to the next one - the queue is worked through without pauses.
            if (next is null)
            {
                try
                {
                    await Task.Delay(poll, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                continue;
            }

            await AskAsync(client, next, stoppingToken);
        }
    }

    /// <summary>
    /// The configured payers, and Fiserv's card - its transfers pay card batches, which the card
    /// step settles by itself.
    /// </summary>
    private IReadOnlyCollection<int> ExcludedContractors()
    {
        var excluded = new SortedSet<int>(_options.ExcludedContractorIds);
        if (cardOptions.Value.Enabled) excluded.Add(cardOptions.Value.FeeContractorId);
        return excluded;
    }

    private async Task<AdvisorCase?> TakeNextAsync(IReadOnlyCollection<int> excluded, CancellationToken stoppingToken)
    {
        try
        {
            return await store.TakeNextAsync(excluded, stoppingToken);
        }
        catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
        {
            logger.LogError(exception, "Could not read the next transfer for the advisor; trying again shortly.");
            return null;
        }
    }

    private async Task AskAsync(SettlementAdvisorClient client, AdvisorCase question, CancellationToken stoppingToken)
    {
        var watch = Stopwatch.StartNew();

        try
        {
            var advice = await client.AskAsync(question.PaymentId, stoppingToken);
            var kept = await store.SaveAnswerAsync(question.PaymentId, advice, CancellationToken.None);

            logger.LogInformation(
                "Advisor answered on payment {PaymentId} ({Amount:N2} of {Day:yyyy-MM-dd}) in {Seconds:N0} s: "
                + "{Kept} documents proposed.",
                question.PaymentId, question.Amount, question.BookingDate, watch.Elapsed.TotalSeconds, kept);

            if (kept < advice.Proposed.Count)
            {
                logger.LogWarning(
                    "Advisor named {Missing} payments on payment {PaymentId} that are not in ERP; they were left out: {Documents}.",
                    advice.Proposed.Count - kept, question.PaymentId,
                    string.Join(", ", advice.Proposed.Select(d => $"{d.DocType}/{d.DocId}/{d.DocLp}")));
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Stopping - the question stays Running and is asked again at the next start.
        }
        catch (Exception exception)
        {
            var retryAt = DateTime.Now.AddMinutes(_options.RetryAfterMinutes * question.Attempt);
            var last = question.Attempt >= _options.MaxAttempts;

            // HttpClient reports its own timeout as a cancellation - worth saying in words.
            var error = exception is TaskCanceledException
                ? $"Brak odpowiedzi w ciągu {_options.TimeoutMinutes} min."
                : exception.Message;

            // A single failure is routine - the model's machine busy or restarting - and the next
            // attempt usually goes through, so it is only noted. It is an error once the last
            // attempt has failed too: then the transfer is left without an answer for good.
            if (last)
            {
                logger.LogError(exception,
                    "Advisor failed on payment {PaymentId} after {Seconds:N0} s, attempt {Attempt} of {Max} - given up: {Error}",
                    question.PaymentId, watch.Elapsed.TotalSeconds, question.Attempt, _options.MaxAttempts, error);
            }
            else
            {
                logger.LogInformation(
                    "Advisor failed on payment {PaymentId} after {Seconds:N0} s, attempt {Attempt} of {Max} - "
                    + "trying again after {RetryAt:HH:mm}: {Error}",
                    question.PaymentId, watch.Elapsed.TotalSeconds, question.Attempt, _options.MaxAttempts, retryAt, error);
            }

            try
            {
                await store.SaveFailureAsync(question.PaymentId, error, retryAt, CancellationToken.None);
            }
            catch (Exception saving)
            {
                logger.LogError(saving, "Could not record the advisor's failure on payment {PaymentId}.", question.PaymentId);
            }
        }
    }
}
