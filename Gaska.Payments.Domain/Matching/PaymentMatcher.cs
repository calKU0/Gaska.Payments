using Gaska.Payments.Domain.Model;
using Gaska.Payments.Domain.Parsing;

namespace Gaska.Payments.Domain.Matching;

/// <summary>
/// Ties an operation from a bank statement to the open items in ERP.
/// </summary>
/// <remarks>
/// The order of work:
/// <list type="number">
///   <item>establish the contractor (ERP, then payer account, then the tax id from a split
///         payment message, then a document number from the title),</item>
///   <item>extract document references from the payment title and resolve them in the index,</item>
///   <item>balance the amount: the named documents, topped up if need be by a search for a
///         subset summing to the amount (bulk payments),</item>
///   <item>assign the confidence level that decides whether settlement may go through
///         automatically.</item>
/// </list>
/// </remarks>
public sealed class PaymentMatcher(MatchingOptions? options = null)
{
    /// <summary>
    /// Lowest score a document reference may have on a part payment. It corresponds to a hit
    /// through a related document number - anything weaker is a bare number with no context.
    /// </summary>
    private const double PartialPaymentMinimumScore = 0.80;

    private readonly MatchingOptions _options = options ?? new MatchingOptions();
    private readonly DescriptionParser _parser = new(options);

    public MatchResult Match(BankPayment payment, DocumentIndex index)
    {
        var notes = new List<string>();

        // The order reference beats everything else: it is the identifier XL assigned to the
        // payment when sending it to the bank, and the bank returned it in the statement. It
        // names the document directly, so there is no point reading the title or guessing the
        // contractor.
        var byReference = MatchByBankReference(payment, index);
        if (byReference is not null) return byReference;


        var parsed = _parser.Parse(payment.Description);

        var contractor = ResolveContractor(payment, parsed, index, notes);
        var contractorId = contractor.Id;
        var contractorSource = contractor.Source;
        var hits = ResolveHits(parsed, contractorId, index, payment.Currency);
        AddForeignNumberHits(payment, index, hits);

        if (contractorId == 0 && hits.Count > 0)
        {
            var inferred = InferContractorFromHits(hits);
            if (inferred is not null)
            {
                contractorId = inferred.Value.ContractorId;
                contractorSource = inferred.Value.Source;
                hits = ResolveHits(parsed, contractorId, index, payment.Currency);
                AddForeignNumberHits(payment, index, hits);
            }
        }

        // The payer name is the weakest piece of evidence - we consult it only once the
        // account, the tax id and the document numbers have all failed. Otherwise a typo in the
        // name could bury an unambiguous invoice number given in the title.
        if (contractorId == 0 && _options.EnablePayerNameLookup)
        {
            var byName = index.FindContractorsByName(payment.PayerName);
            if (byName.Count == 1)
            {
                contractorId = byName[0];
                contractorSource = "nazwa płatnika";
                hits = ResolveHits(parsed, contractorId, index, payment.Currency);
            }
        }

        var explicitHits = hits
            .Where(h => h.Score >= _options.ExplicitReferenceScoreThreshold)
            .OrderByDescending(h => h.Score)
            .ToList();

        // The customer named a specific document and it is not among the open items. That
        // changes what we do: guessing from the contractor's balance would be harmful here,
        // because the transfer says outright what it is for.
        // A "named document" is any reference carrying a year - including an element of a list
        // ("FS-28968,28969,28907/26/SPR"), because the customer listed it just as deliberately
        // as the first number.
        var namedDocuments = parsed.References
            .Where(r => r.Year is not null || r.Strength >= ReferenceStrength.KindNumberYear)
            .Select(r => r.ToString())
            .Distinct()
            .ToList();

        var namedButNotOpen = explicitHits.Count == 0 && namedDocuments.Count > 0;

        if (namedButNotOpen)
        {
            notes.Add($"W tytule wskazano {string.Join(", ", namedDocuments.Take(5))}, ale wśród " +
                      $"nierozliczonych {SideOf(payment.IsIncoming)} nie ma takiego dokumentu – " +
                      "prawdopodobnie został już rozliczony albo numer jest błędny.");
        }

        var result = Allocate(payment, index, contractorId, explicitHits, namedButNotOpen, notes);
        DescribeOutcome(result, payment.IsIncoming, notes);

        var confidence = ApplyBankAccountRequirement(result.Confidence, contractor.FromBankAccount, notes);

        return new MatchResult
        {
            Payment = payment,
            Confidence = confidence,
            Strategy = result.Strategy,
            Allocations = result.Allocations,
            ContractorId = contractorId,
            ContractorSource = contractorSource,
            ContractorFromBankAccount = contractor.FromBankAccount,
            References = parsed.References,
            Unallocated = payment.AmountToAllocate - result.Allocations.Sum(a => a.Amount),
            Notes = notes,
        };
    }

    /// <summary>
    /// Matching by the order reference the bank returned.
    /// </summary>
    /// <remarks>
    /// It answers only when the reference names payments of a single contractor - otherwise
    /// there is no telling whose transfer this is. The confidence level depends on whether the
    /// named payments add up to the amount transferred.
    /// </remarks>
    private MatchResult? MatchByBankReference(BankPayment payment, DocumentIndex index)
    {
        if (payment.EndToEndId.Length == 0) return null;

        var hits = index.FindByBankReference(payment.EndToEndId);
        if (hits.Count == 0) return null;

        var contractors = hits.Select(h => h.ContractorId).Distinct().ToList();
        if (contractors.Count != 1) return null;

        var amount = payment.AmountToAllocate;
        var allocations = new List<MatchAllocation>();
        var left = amount;

        foreach (var hit in hits.OrderByDescending(h => h.Remaining))
        {
            if (left <= 0m) break;

            var take = Math.Min(left, hit.Remaining);
            allocations.Add(new MatchAllocation(hit, take, 1.0,
                $"referencja zlecenia {payment.EndToEndId}"));
            left -= take;
        }

        var covered = allocations.Sum(a => a.Amount);
        var exact = Math.Abs(covered - amount) <= _options.AbsoluteAmountTolerance;

        // The reference names the document, but the counterparty account still decides the
        // confidence level - the same rule as for incoming payments, with no exceptions.
        var byAccount = index.FindContractorsByAccount(payment.PayerAccount);
        var fromAccount = byAccount.Count == 1 && byAccount[0] == contractors[0];

        var notes = new List<string>
        {
            $"Bank zwrócił referencję zlecenia {payment.EndToEndId} – przelew wskazuje dokument wprost.",
        };

        if (!exact)
        {
            notes.Add($"Kwota przelewu ({amount:N2}) nie pokrywa się z płatnościami dokumentów " +
                      $"({covered:N2}) – wymaga potwierdzenia.");
        }

        if (!fromAccount)
        {
            notes.Add("Rachunek bankowy nie jest przypisany do tego kontrahenta w ERP – " +
                      "propozycja wymaga potwierdzenia przez operatora.");
        }

        return new MatchResult
        {
            Payment = payment,
            Confidence = exact && fromAccount ? MatchConfidence.High : MatchConfidence.Medium,
            Strategy = MatchStrategy.BankOrderReference,
            Allocations = allocations,
            ContractorId = contractors[0],
            ContractorSource = fromAccount ? "rachunek bankowy" : "referencja zlecenia z banku",
            ContractorFromBankAccount = fromAccount,
            Unallocated = amount - covered,
            Notes = notes,
        };
    }

    /// <summary>The operation's name in the nominative, capitalised.</summary>
    private static string Kind(bool isIncoming) => isIncoming ? "Wpłata" : "Wypłata";

    /// <summary>The operation's name in the genitive.</summary>
    private static string KindOf(bool isIncoming) => isIncoming ? "wpłaty" : "wypłaty";

    /// <summary>
    /// The side of the ledger this operation closes, in the genitive: receivables or liabilities.
    /// </summary>
    /// <remarks>
    /// One algorithm serves both directions, so the wording has to as well. Text written for
    /// incoming payments misled on outgoing ones - it spoke of receivables where liabilities
    /// were meant.
    /// </remarks>
    private static string SideOf(bool isIncoming) => isIncoming ? "należności" : "zobowiązań";

    /// <summary>
    /// Adds a justification for the match when no branch has left one.
    /// </summary>
    /// <remarks>
    /// Notes are produced along the way only where something went wrong. A successful match
    /// sometimes left none, and the accountant has to know on what grounds the service proposed
    /// something - especially when they are the one to approve it.
    /// </remarks>
    private static void DescribeOutcome(AllocationOutcome result, bool isIncoming, List<string> notes)
    {
        if (result.Allocations.Count == 0 || notes.Count > 0) return;

        var documents = string.Join(", ", result.Allocations.Take(5).Select(a => a.Receivable.DocumentNumber));
        if (result.Allocations.Count > 5) documents += $" i {result.Allocations.Count - 5} więcej";

        notes.Add(result.Strategy switch
        {
            MatchStrategy.ExplicitReferencesExactSum =>
                $"Dokumenty wskazane w tytule ({documents}) sumują się dokładnie do kwoty operacji.",
            MatchStrategy.ExplicitReferencesRounding =>
                $"Wszystkie dokumenty wskazane w tytule ({documents}); ich suma różni się od kwoty "
                + "operacji o grosze – to różnica zaokrągleń.",
            MatchStrategy.ExplicitReferencesPartial =>
                $"Dokumenty wskazane w tytule ({documents}), ale ich suma różni się od kwoty operacji.",
            MatchStrategy.SingleDocumentPartialPayment =>
                $"Wskazano jeden dokument ({documents}); operacja pokrywa jego część.",
            MatchStrategy.SubsetSumOnContractor =>
                $"Z tytułu nic nie wynikło, ale dokładnie jeden zestaw dokumentów kontrahenta " +
                $"({documents}) sumuje się do kwoty operacji.",
            MatchStrategy.ReferencesExtendedBySubsetSum =>
                $"Dokumenty z tytułu uzupełniono o dalsze pozycje kontrahenta, żeby suma się zgodziła ({documents}).",
            MatchStrategy.OldestFirstFallback =>
                $"Rozksięgowano od najstarszych {SideOf(isIncoming)} kontrahenta ({documents}).",
            _ => $"Dopasowano dokumenty: {documents}.",
        });
    }

    /// <summary>
    /// Adds hits found by the document number at the contractor's end.
    /// </summary>
    /// <remarks>
    /// On outgoing transfers this is the strongest evidence the title offers: we describe them
    /// with the supplier's invoice number, not our own. For receivables the foreign number is
    /// empty, so the same method changes nothing on incoming payments.
    /// </remarks>
    private static void AddForeignNumberHits(BankPayment payment, DocumentIndex index, List<ReferenceHit> hits)
    {
        var known = hits
            .Select(h => (h.Receivable.PaymentDocType, h.Receivable.PaymentDocId, h.Receivable.PaymentLp))
            .ToHashSet();

        foreach (var document in index.FindByForeignNumber(payment.Description))
        {
            var key = (document.PaymentDocType, document.PaymentDocId, document.PaymentLp);
            if (!known.Add(key)) continue;

            hits.Add(new ReferenceHit(
                document,
                new DocumentReference(
                    DocumentKind.Unknown, 0, null, null,
                    ReferenceStrength.FullNumber, document.ForeignNumber),
                1.0,
                $"numer dokumentu u kontrahenta {document.ForeignNumber}",
                Reliable: true));
        }
    }

    // ------------------------------------------------------------ contractor ---

    private ContractorMatch ResolveContractor(
        BankPayment payment, ParsedDescription parsed, DocumentIndex index, List<string> notes)
    {
        var byAccount = index.FindContractorsByAccount(payment.PayerAccount);

        // An account pointing at several cards does not say who paid - and that is the only
        // evidence that allows automatic settlement. The reason has to be named, because the fix
        // lies in ERP: the same number hangs on several contractors.
        if (byAccount.Count > 1)
        {
            notes.Add($"Rachunek {payment.PayerAccount} jest w ERP przypisany do " +
                      $"{byAccount.Count} kartotek ({string.Join(", ", byAccount.Take(4).Select(index.AcronymOf))}) – " +
                      "nie da się po nim ustalić kontrahenta.");
        }

        // The payer account is the strongest evidence: it comes from the bank rather than the
        // title, and it ties the payment to one contractor card unambiguously.
        if (byAccount.Count == 1) return new ContractorMatch(byAccount[0], "rachunek bankowy", true);

        if (payment.KnownContractorId != 0)
        {
            return new ContractorMatch(payment.KnownContractorId, "kontrahent z zapisu ERP", false);
        }

        if (!string.IsNullOrEmpty(parsed.SplitPaymentNip) &&
            !string.Equals(parsed.SplitPaymentNip, TextNormalizer.DigitsOnly(_options.OwnNip), StringComparison.Ordinal))
        {
            var byNip = index.FindContractorsByNip(parsed.SplitPaymentNip);
            if (byNip.Count == 1)
            {
                return new ContractorMatch(byNip[0], "NIP z komunikatu podzielonej płatności", false);
            }
        }

        // The payer name is consulted only after the numbers from the title - see Match().
        var source = byAccount.Count > 1 ? "rachunek przypisany do wielu kontrahentów" : "nieustalony";
        return new ContractorMatch(0, source, false);
    }

    /// <summary>The contractor established for a payment, together with how we know.</summary>
    /// <param name="FromBankAccount">
    /// The payer account is attached to this contractor in ERP. It is the only evidence that does
    /// not rest on what the customer typed into the title, which is why nothing reaches the
    /// automat without it.
    /// </param>
    private sealed record ContractorMatch(int Id, string Source, bool FromBankAccount);

    /// <summary>
    /// Only payments whose sender was recognised by account number reach the automat. A matching
    /// amount and an invoice number in the title confirm WHAT to settle, but they do not confirm
    /// WHO paid - and without that nothing may be posted without a human.
    /// </summary>
    private MatchConfidence ApplyBankAccountRequirement(
        MatchConfidence confidence, bool fromBankAccount, List<string> notes)
    {
        if (confidence != MatchConfidence.High) return confidence;
        if (fromBankAccount || !_options.RequireBankAccountForHighConfidence) return confidence;

        notes.Add("Rachunek bankowy nie jest przypisany do kontrahenta w ERP – " +
                  "propozycja wymaga potwierdzenia przez operatora.");

        return MatchConfidence.Medium;
    }

    /// <summary>
    /// Tries to establish the contractor from the numbers in the title alone - needed for
    /// customers who have no account recorded in ERP (mostly foreign ones).
    /// </summary>
    /// <remarks>
    /// One unambiguous full number is enough on its own. Bare numbers ("4307+3952+4036+4110")
    /// mean nothing individually, but if several of them point at the same contractor and no
    /// other collects as many hits, that is enough to propose a settlement. It will not reach the
    /// automat anyway - it is still only what the customer typed into the title.
    /// </remarks>
    private static (int ContractorId, string Source)? InferContractorFromHits(List<ReferenceHit> hits)
    {
        var strong = hits
            .Where(h => h.Score >= 0.80)
            .Select(h => h.Receivable.ContractorId)
            .Distinct()
            .ToList();

        if (strong.Count == 1) return (strong[0], "numer dokumentu z tytułu przelewu");

        var votes = hits
            .GroupBy(h => h.Receivable.ContractorId)
            .Select(g => (ContractorId: g.Key, References: g.Select(h => h.Reference).Distinct().Count()))
            .OrderByDescending(v => v.References)
            .ToList();

        if (votes.Count == 0 || votes[0].References < 2) return null;
        if (votes.Count > 1 && votes[1].References == votes[0].References) return null;

        return (votes[0].ContractorId, "kilka numerów z tytułu wskazuje tego samego kontrahenta");
    }

    private List<ReferenceHit> ResolveHits(
        ParsedDescription parsed, int contractorId, DocumentIndex index, string currency)
    {
        var best = new Dictionary<(int, int, int), ReferenceHit>();

        void Register(IEnumerable<ReferenceHit> hits)
        {
            foreach (var hit in hits)
            {
                if (!string.Equals(hit.Receivable.Currency, currency, StringComparison.OrdinalIgnoreCase)) continue;

                var key = (hit.Receivable.PaymentDocType, hit.Receivable.PaymentDocId, hit.Receivable.PaymentLp);
                if (!best.TryGetValue(key, out var existing) || hit.Score > existing.Score)
                {
                    best[key] = hit;
                }
            }
        }

        // A KSeF number is unambiguous, so it goes first.
        foreach (var ksef in parsed.KsefNumbers)
        {
            Register(index.ResolveKsef(ksef, contractorId));
        }

        foreach (var reference in parsed.References)
        {
            Register(index.Resolve(reference, contractorId));
        }

        return [.. best.Values];
    }

    // ------------------------------------------------------------ allocation ---

    private sealed record AllocationOutcome(
        MatchConfidence Confidence,
        MatchStrategy Strategy,
        IReadOnlyList<MatchAllocation> Allocations);

    /// <param name="namedButNotOpen">
    /// The customer gave a specific document number in the title and it is not among the open
    /// items. This switches guessing off: since we know what the transfer is for, substituting
    /// arbitrary invoices only misleads.
    /// </param>
    private AllocationOutcome Allocate(
        BankPayment payment,
        DocumentIndex index,
        int contractorId,
        List<ReferenceHit> explicitHits,
        bool namedButNotOpen,
        List<string> notes)
    {
        var target = payment.AmountToAllocate;
        var tolerance = _options.AbsoluteAmountTolerance;

        if (target <= tolerance)
        {
            notes.Add("Kwota do rozliczenia jest zerowa – nie ma czego przypisywać.");
            return new AllocationOutcome(MatchConfidence.None, MatchStrategy.NoCandidates, []);
        }

        if (explicitHits.Count > 0)
        {
            var outcome = AllocateFromReferences(payment, index, contractorId, explicitHits, target, tolerance, notes);
            if (outcome is not null) return outcome;
        }

        if (contractorId != 0)
        {
            var outcome = AllocateFromContractorPool(
                payment, index, contractorId, explicitHits, target, tolerance, namedButNotOpen, notes);
            if (outcome is not null) return outcome;
        }

        if (explicitHits.Count == 0 && !namedButNotOpen)
        {
            notes.Add(contractorId == 0
                ? "Nie ustalono kontrahenta ani numeru dokumentu – zapis do wyjaśnienia."
                : $"Ustalono kontrahenta, ale żadna kombinacja jego {SideOf(payment.IsIncoming)} " +
                  $"nie odpowiada kwocie {KindOf(payment.IsIncoming)}.");
        }

        return new AllocationOutcome(MatchConfidence.None, MatchStrategy.NoCandidates, []);
    }

    private AllocationOutcome? AllocateFromReferences(
        BankPayment payment,
        DocumentIndex index,
        int contractorId,
        List<ReferenceHit> explicitHits,
        decimal target,
        decimal tolerance,
        List<string> notes)
    {
        var signedSum = explicitHits.Sum(h => h.Receivable.SignedRemaining);

        // 1. The named documents balance against the amount - the textbook case, and it also
        //    covers bulk payments that list their invoices.
        if (Math.Abs(signedSum - target) <= tolerance)
        {
            var allocations = explicitHits
                .Select(h => new MatchAllocation(h.Receivable, h.Receivable.SignedRemaining, h.Score, h.Reason))
                .ToList();

            // An amount agreeing to the penny is independent confirmation and weighs more
            // than the format the customer used for the number. "F.32502" with a perfectly
            // matching amount is as certain as the full "(S)FS-32502/26/SPR". What we do not let
            // through to the automat are hits with a caveat: another contractor, another series,
            // several candidates.
            var confidence = contractorId != 0 && explicitHits.All(h => h.Reliable)
                ? MatchConfidence.High
                : MatchConfidence.Medium;

            return new AllocationOutcome(confidence, MatchStrategy.ExplicitReferencesExactSum, allocations);
        }

        // 2. The customer gave per-document amounts in the title - we trust them if they add up.
        if (explicitHits.All(h => h.Reference.DeclaredAmount is not null))
        {
            var declaredSum = explicitHits.Sum(h => h.Reference.DeclaredAmount!.Value);
            if (Math.Abs(declaredSum - target) <= tolerance)
            {
                var allocations = explicitHits
                    .Select(h => new MatchAllocation(
                        h.Receivable,
                        Math.Min(h.Reference.DeclaredAmount!.Value, h.Receivable.Remaining),
                        h.Score,
                        h.Reason + " (kwota z tytułu przelewu)"))
                    .ToList();

                return new AllocationOutcome(MatchConfidence.High, MatchStrategy.ExplicitReferencesExactSum, allocations);
            }
        }

        // 2a. The named documents come to within a few groszy of the amount. That is a rounding
        //     difference, not a different set of documents: every number in the title was found,
        //     and no other reading of the title is available. Proposing them and leaving the few
        //     groszy unallocated beats every fallback below, all of which answer a question the
        //     customer did not ask.
        var difference = target - signedSum;

        if (Math.Abs(difference) <= _options.ReferenceSumTolerance && explicitHits.Count > 1)
        {
            var allocations = explicitHits
                .Select(h => new MatchAllocation(h.Receivable, h.Receivable.SignedRemaining, h.Score, h.Reason))
                .ToList();

            notes.Add(
                $"Suma dokumentów z tytułu ({signedSum:N2}) różni się od kwoty " +
                $"{KindOf(payment.IsIncoming)} ({target:N2}) o {Math.Abs(difference):N2} – " +
                "różnica groszowa, przypisano wszystkie wskazane dokumenty.");

            // Never certain enough for the automat: the amount no longer confirms the reading of
            // the title, so a human looks at it. But it is a proposal to accept, not a hint.
            return new AllocationOutcome(
                MatchConfidence.Medium, MatchStrategy.ExplicitReferencesRounding, allocations);
        }

        // 3. One named document, the payment covering part of it - common with instalments.
        if (explicitHits.Count == 1)
        {
            var only = explicitHits[0];
            if (!only.Receivable.IsCorrection && target < only.Receivable.Remaining - tolerance)
            {
                notes.Add($"{Kind(payment.IsIncoming)} częściowa: {target:N2} " +
                          $"z {only.Receivable.Remaining:N2} {only.Receivable.Currency}.");

                // On a part payment the amount confirms nothing, so all that counts is
                // whether the document was named unambiguously. It makes no difference whether
                // the customer quoted the invoice number or that of the order it came from - in
                // both cases the reference leads to exactly one open document of that contractor.
                // What does drop out is a bare number with no context at all (0.75).
                var confidence = only.Reliable && only.Score >= PartialPaymentMinimumScore
                    ? MatchConfidence.High
                    : MatchConfidence.Medium;
                return new AllocationOutcome(
                    confidence,
                    MatchStrategy.SingleDocumentPartialPayment,
                    [new MatchAllocation(
                        only.Receivable, target, only.Score,
                        $"{only.Reason} ({Kind(payment.IsIncoming).ToLowerInvariant()} częściowa)")]);
            }
        }

        // 4. The customer listed more documents than they paid for - look for a subset of them.
        if (explicitHits.Count > 1)
        {
            var subset = SelectSubset(
                explicitHits.Select(h => h.Receivable).ToList(), target, tolerance, out var solutionCount);

            if (subset is not null)
            {
                var ambiguous = solutionCount > 1;
                var allocations = subset
                    .Select(r => explicitHits.First(h => ReferenceEquals(h.Receivable, r)))
                    .Select(h => new MatchAllocation(h.Receivable, h.Receivable.SignedRemaining, h.Score, h.Reason))
                    .ToList();

                notes.Add("Z dokumentów wymienionych w tytule wybrano podzbiór zgodny z kwotą " +
                          KindOf(payment.IsIncoming) + ".");
                return new AllocationOutcome(
                    ambiguous ? MatchConfidence.Low : MatchConfidence.Medium,
                    MatchStrategy.ExplicitReferencesPartial,
                    allocations);
            }
        }

        // 5. The named documents plus further open items of the contractor to make up the rest.
        if (contractorId != 0)
        {
            var missing = target - signedSum;
            var pool = BuildPool(index, contractorId, payment.Currency)
                .Where(r => explicitHits.All(h => !ReferenceEquals(h.Receivable, r)))
                .ToList();

            if (Math.Abs(missing) > tolerance && pool.Count > 0)
            {
                var extra = SelectSubset(pool, missing, tolerance, out var solutionCount);

                // We top up with missing documents only when exactly one set fits. With
                // several possible sets the choice is a coin toss - better to propose just the
                // documents named in the title (point 6) than to bolt guessed ones onto them.
                if (extra is not null && solutionCount == 1)
                {
                    var allocations = explicitHits
                        .Select(h => new MatchAllocation(h.Receivable, h.Receivable.SignedRemaining, h.Score, h.Reason))
                        .Concat(extra.Select(r => new MatchAllocation(
                            r, r.SignedRemaining, 0.50,
                            $"dobrane, żeby zbilansować kwotę {KindOf(payment.IsIncoming)}")))
                        .ToList();

                    notes.Add($"Dokumenty z tytułu nie pokrywały całej {KindOf(payment.IsIncoming)} – " +
                              $"resztę dobrano z {SideOf(payment.IsIncoming)} kontrahenta.");
                    return new AllocationOutcome(
                        MatchConfidence.Medium,
                        MatchStrategy.ReferencesExtendedBySubsetSum,
                        allocations);
                }

                if (extra is not null)
                {
                    notes.Add($"Brakującą część {KindOf(payment.IsIncoming)} dałoby się pokryć " +
                              $"na {solutionCount} różnych sposobów – nie zgaduję, które dokumenty dobrać.");
                }
            }
        }

        // 6. Nothing balances - spread the amount over the named documents, oldest first, with the
        //    corrections named alongside them netted off first. A correction the customer quoted
        //    is part of what they are settling; dropping it and part-paying an invoice instead
        //    invents a debt they never claimed to be leaving unpaid.
        var namedCorrections = explicitHits
            .Where(h => h.Receivable.IsCorrection)
            .Select(h => new MatchAllocation(h.Receivable, h.Receivable.SignedRemaining, h.Score, h.Reason))
            .ToList();

        var toSpread = target - namedCorrections.Sum(a => a.Amount);

        var greedy = AllocateGreedy(
            explicitHits.Where(h => !h.Receivable.IsCorrection)
                .Select(h => (h.Receivable, h.Score, h.Reason)).ToList(),
            toSpread);

        if (greedy.Count > 0)
        {
            notes.Add($"Kwota {KindOf(payment.IsIncoming)} ({target:N2}) nie zgadza się z sumą " +
                      $"wskazanych dokumentów ({signedSum:N2}) – propozycja rozksięgowania od najstarszego.");

            if (namedCorrections.Count > 0)
            {
                notes.Add($"Korekty z tytułu przelewu ({namedCorrections.Sum(a => -a.Amount):N2}) " +
                          "odjęto przed rozksięgowaniem.");
            }

            return new AllocationOutcome(
                MatchConfidence.Low, MatchStrategy.ExplicitReferencesPartial,
                [.. namedCorrections, .. greedy]);
        }

        return null;
    }

    private AllocationOutcome? AllocateFromContractorPool(
        BankPayment payment,
        DocumentIndex index,
        int contractorId,
        List<ReferenceHit> explicitHits,
        decimal target,
        decimal tolerance,
        bool namedButNotOpen,
        List<string> notes)
    {
        if (explicitHits.Count > 0) return null;

        var pool = BuildPool(index, contractorId, payment.Currency);
        if (pool.Count == 0) return null;

        var subset = SelectSubset(pool, target, tolerance, out var solutionCount);

        // Matching on the amount alone makes sense only when the answer is unambiguous. When
        // several different sets of invoices give the same amount, naming any one of them is a
        // coin toss (18% accuracy on historical data) and only misleads the operator.
        if (subset is not null && solutionCount > 1)
        {
            notes.Add($"Kwota {KindOf(payment.IsIncoming)} pasuje do {solutionCount} różnych zestawów " +
                      $"{SideOf(payment.IsIncoming)} tego kontrahenta – bez wskazówki w tytule nie da się " +
                      "rozstrzygnąć, o który chodzi.");
            return null;
        }

        if (subset is not null)
        {
            var allocations = subset
                .Select(r => new MatchAllocation(r, r.SignedRemaining, 0.65,
                    $"kwota {KindOf(payment.IsIncoming)} odpowiada sumie {SideOf(payment.IsIncoming)} kontrahenta"))
                .ToList();

            if (namedButNotOpen)
            {
                notes.Add("Wskazane dokumenty są inne niż te z tytułu przelewu – zgadza się tylko kwota.");
            }

            return new AllocationOutcome(
                namedButNotOpen ? MatchConfidence.Low : MatchConfidence.Medium,
                MatchStrategy.SubsetSumOnContractor,
                allocations);
        }

        // Spreading over the oldest items makes sense only when the title says nothing. When
        // the customer wrote plainly which invoice they are paying, substituting another is worse
        // than no proposal - the operator must see that this needs a human decision.
        if (namedButNotOpen)
        {
            notes.Add("Nie proponuję rozksięgowania od najstarszych – przelew wskazuje konkretny dokument.");
            return null;
        }

        if (!_options.EnableOldestFirstFallback) return null;

        var greedy = AllocateGreedy(
            pool.Where(r => !r.IsCorrection).Select(r => (r, 0.30, "rozksięgowanie od najstarszej należności")).ToList(),
            target);

        if (greedy.Count == 0) return null;

        notes.Add("Brak wskazówek w tytule – zaproponowano rozksięgowanie od najstarszych " +
                  SideOf(payment.IsIncoming) + ".");
        return new AllocationOutcome(MatchConfidence.Low, MatchStrategy.OldestFirstFallback, greedy);
    }

    // --------------------------------------------------------------- helpers ---

    private List<OpenReceivable> BuildPool(DocumentIndex index, int contractorId, string currency) =>
        index.ForContractor(contractorId)
            .Where(r => string.Equals(r.Currency, currency, StringComparison.OrdinalIgnoreCase))
            .Where(r => r.Remaining > 0m)
            .OrderBy(r => r.DueDate)
            .Take(_options.SubsetSearchPoolLimit)
            .ToList();

    /// <summary>
    /// Picks a subset of open items summing to the amount. With several solutions it prefers the
    /// smallest and the oldest, and reports how many there were in
    /// <paramref name="solutionCount"/> (counted up to the solver's limit, so "20" means "at
    /// least 20").
    /// </summary>
    private List<OpenReceivable>? SelectSubset(
        IReadOnlyList<OpenReceivable> pool, decimal target, decimal tolerance, out int solutionCount)
    {
        solutionCount = 0;
        if (pool.Count == 0) return null;

        var values = pool.Select(r => SubsetSumSolver.ToCents(r.SignedRemaining)).ToList();
        var solutions = SubsetSumSolver.Solve(
            values,
            SubsetSumSolver.ToCents(target),
            _options.MaxDocumentsPerPayment,
            SubsetSumSolver.ToCents(tolerance));

        if (solutions.Count == 0) return null;

        var ranked = solutions
            .Select(s => new
            {
                Solution = s,
                Items = s.Indices.Select(i => pool[i]).ToList(),
            })
            .OrderBy(x => x.Items.Count)
            .ThenBy(x => x.Items.Min(r => r.DueDate))
            .ThenBy(x => Math.Abs(x.Solution.Difference))
            .ToList();

        solutionCount = ranked.Count;
        return ranked[0].Items;
    }

    /// <summary>Greedy spreading: from the oldest open item until the amount runs out.</summary>
    private static List<MatchAllocation> AllocateGreedy(
        List<(OpenReceivable Receivable, double Score, string Reason)> candidates, decimal target)
    {
        var allocations = new List<MatchAllocation>();
        var left = target;

        foreach (var (receivable, score, reason) in candidates.OrderBy(c => c.Receivable.DueDate))
        {
            if (left <= 0m) break;
            if (receivable.IsCorrection) continue;

            var amount = Math.Min(left, receivable.Remaining);
            if (amount <= 0m) continue;

            allocations.Add(new MatchAllocation(receivable, amount, score, reason));
            left -= amount;
        }

        return allocations;
    }
}
