using Gaska.Payments.Domain.Diagnostics;
using Gaska.Payments.Domain.Matching;
using Gaska.Payments.Domain.Model;
using Gaska.Payments.Domain.Parsing;
using Gaska.Payments.Erp;
using Gaska.Payments.Integrations.Bank;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Gaska.Payments.Application.Settlement;

/// <summary>A summary of one service cycle.</summary>
public sealed record SettlementRunSummary(
    int RunId,
    DateTime PeriodFrom,
    DateTime PeriodTo,
    int PaymentsFetched,
    int ProposalsSaved,
    IReadOnlyDictionary<MatchConfidence, int> ByConfidence);

/// <summary>
/// The full cycle: download operations from the bank, build the document index from ERP, match,
/// and write the proposals to <c>pay.*</c>.
/// </summary>
public sealed class BankSettlementPipeline(
    BnpStatementClient bankClient,
    ErpReadRepository erp,
    PaymentStore store,
    PaymentMatcher matcher,
    IOptions<SettlementOptions> settlementOptions,
    ILogger<BankSettlementPipeline> logger)
{
    /// <summary>Below this remainder an ERP entry counts as settled - it is small change.</summary>
    private const decimal SettledThreshold = 0.02m;

    private readonly SettlementOptions _options = settlementOptions.Value;

    /// <summary>Creates the missing <c>pay.*</c> tables.</summary>
    public Task EnsureSchemaAsync(CancellationToken cancellationToken = default) =>
        store.EnsureSchemaAsync(cancellationToken);

    public async Task<SettlementRunSummary> RunAsync(CancellationToken cancellationToken = default)
    {
        var periodTo = DateTime.Today;
        var periodFrom = periodTo.AddDays(-_options.LookbackDays);

        var runId = await store.StartRunAsync(periodFrom, periodTo, cancellationToken);
        logger.LogInformation(
            "Run {RunId} started: bank operations from {From:yyyy-MM-dd} to {To:yyyy-MM-dd}.",
            runId, periodFrom, periodTo);

        try
        {
            // The accounts to query come from the registers named in configuration, and the
            // numbers from those registers in ERP. Configuration therefore lists series, not
            // account numbers - those would have to agree with the register anyway, and two
            // sources of truth drift apart.
            var registers = await erp.GetBankRegistersAsync(_options.AllRegisters, cancellationToken);

            if (registers.Count == 0)
            {
                throw new InvalidOperationException(
                    $"Żaden z rejestrów {string.Join(", ", _options.AllRegisters)} nie istnieje w ERP.");
            }

            // The bank wants a full IBAN with its country code; ERP keeps the number without
            // one, so the register assembles it from KAR_NrRachunku and KAR_Kraj. A register
            // whose number cannot be assembled is skipped rather than sent in a request we know
            // will be rejected.
            var withoutNumber = registers.Where(r => r.Iban.Length == 0).ToList();
            if (withoutNumber.Count > 0)
            {
                logger.LogError(
                    "Skipping registers with an incomplete account number (no number or no country code): {Series}.",
                    string.Join(", ", withoutNumber.Select(r => r.Series)));
            }

            var accounts = registers
                .Where(r => r.Iban.Length > 0)
                .Select(r => new BnpAccount(r.Iban, r.Currency))
                .ToList();

            // The whole movement on the accounts is downloaded - debits have to reach the cash
            // report as well, even though they are not matched against invoices.
            List<BankPayment> payments;

            using (var download = TimedOperation.Start(logger, $"Run {runId}: downloading from the bank"))
            {
                payments = [.. await bankClient.GetOperationsAsync(
                    accounts, periodFrom, periodTo, cancellationToken)];

                download.Result(
                    $"{payments.Count} operations from {accounts.Count} accounts "
                    + $"({payments.Count(p => p.IsIncoming)} credits, {payments.Count(p => !p.IsIncoming)} debits)");
            }

            if (payments.Count == 0)
            {
                await store.CompleteRunAsync(runId, 0, 0, null, cancellationToken);
                return new SettlementRunSummary(runId, periodFrom, periodTo, 0, 0, new Dictionary<MatchConfidence, int>());
            }

            // Payments the accounting team has already settled by hand are merely recorded.
            // Their invoices are closed, so the engine would have nothing to propose anyway, and
            // the operator's queue would fill up with noise.
            var erpEntries = await erp.GetErpBankEntriesAsync(periodFrom.AddDays(-5), cancellationToken);
            var correlation = BankToErpCorrelator.Correlate(payments, erpEntries);

            // Grouped rather than ToDictionary: two registers can sit on one account number, and
            // in this ERP two already do (ZFŚS and FOR-P). ToDictionary threw on the duplicate and
            // took the whole cycle down with it, every hour, the moment both were configured.
            // The first register wins and the rest are named, because the choice is arbitrary and
            // somebody has to decide which one the entries belong on.
            var registerByAccount = registers
                .GroupBy(r => r.NormalizedAccount, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

            foreach (var duplicate in registers
                         .GroupBy(r => r.NormalizedAccount, StringComparer.Ordinal)
                         .Where(g => g.Count() > 1))
            {
                logger.LogWarning(
                    "Account {Account} is shared by registers {Series} - entries will go to {Chosen}.",
                    duplicate.Key, string.Join(", ", duplicate.Select(r => r.Series)),
                    duplicate.First().Series);
            }
            var ownAccounts = registers.Select(r => r.NormalizedAccount).ToHashSet(StringComparer.Ordinal);
            var ownContractorId = await erp.GetContractorIdByAcronymAsync(
                _options.OwnContractorAcronym, cancellationToken);

            var (receivables, liabilities) = await BuildIndexesAsync(cancellationToken);
            var proposals = new List<SettlementProposal>(payments.Count);

            using var matching = TimedOperation.Start(logger, $"Run {runId}: matching");

            foreach (var payment in payments)
            {
                var register = registerByAccount.GetValueOrDefault(
                    TextNormalizer.NormalizeAccount(payment.CreditedAccount));

                // Registers without settlement are decided by the account the operation sits
                // on, not by its content - which is why we ask before classifying. Neither a card
                // nor the social fund account has open items, so a commission or a split payment
                // message changes nothing here.
                var category = _options.CategoryOf(register?.Series)
                    ?? BankOperationClassifier.Classify(payment, ownAccounts);

                // Split payment legs and commissions reach ERP as cash entries but have
                // nothing to settle against documents. Debits do go on to matching - the engine
                // returns an empty result for them unless the bank supplied an order reference.
                if (category != PaymentCategory.Standard)
                {
                    // On a VAT leg the counterparty is ourselves - it is a movement between
                    // our own accounts, so the party is our own company's card.
                    var contractorId = category == PaymentCategory.SplitPayment ? ownContractorId : 0;

                    proposals.Add(new SettlementProposal(
                        EmptyResult(payment, NoteFor(payment, category)) with { ContractorId = contractorId },
                        null,
                        SettlementStatus.NoSettlement,
                        register?.Series,
                        category));
                    continue;
                }

                correlation.TryGetValue(payment.Id, out var erpEntry);

                // The accounting team can settle a payment in part. We then match only what
                // is genuinely left in ERP - searching for documents worth the full transfer
                // would keep hitting invoices already closed by that same payment.
                var settledInErp = erpEntry is not null &&
                                   (erpEntry.Rozliczony == 1 || erpEntry.Remaining <= SettledThreshold);

                var effective = erpEntry is null ? payment : payment with { Unsettled = erpEntry.Remaining };

                // Credits settle receivables, debits settle liabilities. The engine is the
                // same - only the set of documents it searches differs.
                var index = payment.IsIncoming ? receivables : liabilities;

                var result = settledInErp
                    ? EmptyResult(payment, "Operacja rozliczona w ERP poza serwisem.")
                    : matcher.Match(effective, index);

                if (!settledInErp && erpEntry is not null && erpEntry.Remaining < erpEntry.Amount)
                {
                    result = result with
                    {
                        Notes = [.. result.Notes,
                            $"Wpłata rozliczona w ERP częściowo – do przypisania zostało " +
                            $"{erpEntry.Remaining:N2} z {erpEntry.Amount:N2} {erpEntry.Currency}."],
                    };
                }

                proposals.Add(new SettlementProposal(
                    result,
                    (int?)erpEntry?.Id,
                    settledInErp ? SettlementStatus.SettledInErp : SettlementStatus.Proposed,
                    register?.Series,
                    category));
            }

            matching.Result($"{proposals.Count} operations run through the engine");
            matching.Dispose();

            int saved;

            using (var writing = TimedOperation.Start(logger, $"Run {runId}: saving proposals"))
            {
                saved = await store.SaveAsync(runId, proposals, cancellationToken);
                await store.CompleteRunAsync(runId, payments.Count, saved, null, cancellationToken);
                writing.Result($"{saved} rows written");
            }

            var byConfidence = proposals
                .Where(p => p.Status == SettlementStatus.Proposed)
                .GroupBy(p => p.Result.Confidence)
                .ToDictionary(g => g.Key, g => g.Count());

            logger.LogInformation(
                "Run {RunId} finished: {Saved} saved, {Settled} already settled in ERP, " +
                "{High} certain, {Medium} awaiting acceptance.",
                runId, saved,
                proposals.Count(p => p.Status == SettlementStatus.SettledInErp),
                byConfidence.GetValueOrDefault(MatchConfidence.High),
                byConfidence.GetValueOrDefault(MatchConfidence.Medium));

            return new SettlementRunSummary(runId, periodFrom, periodTo, payments.Count, saved, byConfidence);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Run {RunId} failed.", runId);
            await store.CompleteRunAsync(runId, 0, 0, exception.Message, CancellationToken.None);
            throw;
        }
    }

    private static string NoteFor(BankPayment payment, string category) => category switch
    {
        PaymentCategory.Card => "Operacja z rachunku karty – zapis kasowy na „Innego”, bez rozliczenia.",
        PaymentCategory.PostOnly => "Rejestr bez rozliczeń – zapis kasowy na „Innego”.",
        PaymentCategory.BankFee => "Prowizja bankowa – zapis kasowy bez rozliczenia.",
        PaymentCategory.SplitPayment => "Noga mechanizmu podzielonej płatności – zapis kasowy bez rozliczenia.",
        _ => payment.IsIncoming
            ? "Operacja nie podlega rozliczeniu z fakturami."
            : "Obciążenie rachunku – zapis kasowy bez rozliczenia.",
    };

    private static MatchResult EmptyResult(BankPayment payment, string note) => new()
    {
        Payment = payment,
        Confidence = MatchConfidence.None,
        Strategy = MatchStrategy.NoCandidates,
        Notes = [note],
    };

    /// <summary>
    /// Builds two indexes: receivables for credits and liabilities for debits.
    /// </summary>
    /// <remarks>
    /// Separating the sets instead of branching inside the engine - matching works the same in
    /// both directions, and the only difference is what it searches. Order references go into
    /// both indexes, because a transfer carrying one may run either way.
    /// </remarks>
    private async Task<(DocumentIndex Receivables, DocumentIndex Liabilities)> BuildIndexesAsync(
        CancellationToken cancellationToken)
    {
        var issuedSince = DateTime.Today.AddMonths(-_options.ReceivablesLookbackMonths);

        using var step = TimedOperation.Start(logger, "Building the document indexes");

        var receivables = await erp.GetOpenReceivablesAsync(issuedSince, cancellationToken: cancellationToken);
        var liabilities = await erp.GetOpenLiabilitiesAsync(cancellationToken);
        var byReference = await erp.GetPaymentsByBankReferenceAsync(cancellationToken);

        var receivableIndex = new DocumentIndex(receivables);
        var liabilityIndex = new DocumentIndex(liabilities);

        await erp.LoadContractorLookupsAsync(receivableIndex, cancellationToken);
        await erp.LoadContractorLookupsAsync(liabilityIndex, cancellationToken);

        receivableIndex.AddBankReferences(byReference);
        liabilityIndex.AddBankReferences(byReference);

        step.Result(
            $"{receivables.Count} receivables issued since {issuedSince:yyyy-MM-dd}, "
            + $"{liabilities.Count} liabilities, {byReference.Count} payments with an order reference");

        return (receivableIndex, liabilityIndex);
    }
}
