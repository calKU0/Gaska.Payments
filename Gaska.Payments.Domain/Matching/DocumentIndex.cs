using Gaska.Payments.Domain.Model;
using Gaska.Payments.Domain.Parsing;

namespace Gaska.Payments.Domain.Matching;

/// <summary>A reference from a payment title hitting one specific receivable.</summary>
/// <param name="Reliable">
/// A hit with no caveats: the number pointed at exactly one document, that document belongs to
/// the contractor the transfer points at, and the series agrees. The distinction matters because
/// with a matching amount such a hit is enough for the automat, even when the customer typed a
/// bare number with no symbol and no year.
/// </param>
public sealed record ReferenceHit(
    OpenReceivable Receivable,
    DocumentReference Reference,
    double Score,
    string Reason,
    bool Reliable);

/// <summary>
/// An inverted index of open receivables - it finds a document in O(1) from the number the
/// customer typed, at several levels of precision.
/// </summary>
public sealed class DocumentIndex
{
    private readonly List<OpenReceivable> _all;
    private readonly Dictionary<int, List<OpenReceivable>> _byContractor = [];
    private readonly Dictionary<(DocumentKind, int, int), List<OpenReceivable>> _byKindNumberYear = [];
    private readonly Dictionary<(DocumentKind, int), List<OpenReceivable>> _byKindNumber = [];
    private readonly Dictionary<(int, int), List<OpenReceivable>> _byNumberYear = [];
    private readonly Dictionary<int, List<OpenReceivable>> _byNumber = [];
    private readonly Dictionary<string, List<int>> _contractorByAccount = [];
    private readonly Dictionary<string, List<int>> _contractorByNip = [];
    private readonly Dictionary<string, List<OpenReceivable>> _byKsef = [];
    private readonly Dictionary<string, List<int>> _contractorByNameKey = [];
    private readonly Dictionary<string, List<int>> _contractorByNameToken = [];
    private readonly Dictionary<int, string[]> _contractorTokens = [];

    /// <summary>Past this many contractors a name token stops narrowing anything down.</summary>
    private const int MaxContractorsPerNameToken = 150;

    /// <summary>Smallest share of shared name tokens required to accept a match.</summary>
    private const double NameMatchThreshold = 0.67;

    /// <summary>
    /// From this score upwards a hit counts as reliable. Below it lie the guesses: a number with
    /// its leading digit lost (0.45) and a bare number with no contractor established (0.18).
    /// </summary>
    private const double ReliableBaseScore = 0.70;

    /// <summary>Auxiliary numbers (goods issues, orders) - they lead to the invoice's receivable.</summary>
    private readonly Dictionary<(DocumentKind, int, int), List<OpenReceivable>> _byRelated = [];

    /// <summary>Years of related documents carrying a given number - so the whole index need not be scanned.</summary>
    private readonly Dictionary<(DocumentKind, int), List<int>> _relatedYears = [];

    /// <summary>
    /// Documents keyed by their number at the contractor's end (<c>TrN_DokumentObcy</c>). Filled
    /// in for liabilities only - a transfer to a supplier quotes the supplier's invoice number,
    /// not ours.
    /// </summary>
    private readonly Dictionary<string, List<OpenReceivable>> _byForeignNumber =
        new(StringComparer.Ordinal);

    /// <summary>Below this many characters a contractor's document number decides nothing.</summary>
    private const int MinForeignNumberLength = 6;

    /// <summary>
    /// Documents whose number at the contractor's end appears in the given payment title.
    /// </summary>
    /// <remarks>
    /// Foreign numbers share no common format, so a parser cannot pick them out the way it picks
    /// out ours. We test for containment instead - the set holds a few thousand entries, so it is
    /// cheap.
    /// </remarks>
    public IReadOnlyList<OpenReceivable> FindByForeignNumber(string description)
    {
        var haystack = TextNormalizer.NormalizeCompact(description);
        if (haystack.Length == 0 || _byForeignNumber.Count == 0) return [];

        var hits = new List<OpenReceivable>();

        foreach (var (needle, items) in _byForeignNumber)
        {
            if (haystack.Contains(needle, StringComparison.Ordinal)) hits.AddRange(items);
        }

        return hits;
    }

    /// <summary>Payments sent to the bank, keyed by their order reference (<c>TrP_EndToEndId</c>).</summary>
    private readonly Dictionary<string, List<OpenReceivable>> _byBankReference =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Adds the payments XL sent to the bank, indexed by their order reference.
    /// </summary>
    /// <remarks>
    /// This is a set apart from the receivables: it covers mainly liabilities, which the sales
    /// index does not hold at all. An entry lands here for one purpose only - so that a document
    /// can be found from the identifier the bank returns.
    /// </remarks>
    public void AddBankReferences(IEnumerable<OpenReceivable> payments)
    {
        foreach (var payment in payments)
        {
            if (payment.BankReference.Length == 0) continue;
            Add(_byBankReference, payment.BankReference.Trim(), payment);
        }
    }

    /// <summary>Document payments named by an order reference taken from the statement.</summary>
    public IReadOnlyList<OpenReceivable> FindByBankReference(string reference) =>
        reference.Length > 0 && _byBankReference.TryGetValue(reference.Trim(), out var hits)
            ? hits
            : [];

    public DocumentIndex(IEnumerable<OpenReceivable> receivables)
    {
        _all = [.. receivables];

        foreach (var item in _all)
        {
            Add(_byContractor, item.ContractorId, item);
            Add(_byKindNumberYear, (item.Kind, item.Number, item.Year % 100), item);
            Add(_byKindNumber, (item.Kind, item.Number), item);
            Add(_byNumberYear, (item.Number, item.Year % 100), item);
            Add(_byNumber, item.Number, item);

            var foreign = TextNormalizer.NormalizeCompact(item.ForeignNumber);
            if (foreign.Length >= MinForeignNumberLength) Add(_byForeignNumber, foreign, item);

            foreach (var related in item.RelatedNumbers)
            {
                var relatedYear = (related.Year ?? item.Year) % 100;
                Add(_byRelated, (related.Kind, related.Number, relatedYear), item);

                if (!_relatedYears.TryGetValue((related.Kind, related.Number), out var years))
                {
                    years = [];
                    _relatedYears[(related.Kind, related.Number)] = years;
                }
                if (!years.Contains(relatedYear)) years.Add(relatedYear);
            }

            var ksef = NormalizeKsef(item.KsefNumber);
            if (ksef.Length > 0) Add(_byKsef, ksef, item);
        }
    }

    /// <summary>
    /// A KSeF number reduced for comparison: alphanumeric characters only. The bank wraps the
    /// title every 35 characters and customers drop the hyphens, so comparing it as-is virtually
    /// never hits.
    /// </summary>
    public static string NormalizeKsef(string? ksef)
    {
        if (string.IsNullOrWhiteSpace(ksef)) return string.Empty;

        var buffer = new char[ksef.Length];
        var length = 0;
        foreach (var ch in ksef)
        {
            if (char.IsAsciiLetterOrDigit(ch)) buffer[length++] = char.ToUpperInvariant(ch);
        }

        return length >= 20 ? new string(buffer, 0, length) : string.Empty;
    }

    /// <summary>Finds a receivable by the KSeF number typed into the payment title.</summary>
    public IReadOnlyList<ReferenceHit> ResolveKsef(string ksefNumber, int contractorId)
    {
        var key = NormalizeKsef(ksefNumber);
        if (key.Length == 0 || !_byKsef.TryGetValue(key, out var candidates)) return [];

        var reference = new DocumentReference(
            DocumentKind.AnyInvoice, 0, null, null, ReferenceStrength.FullNumber, ksefNumber);

        return candidates
            .Select(c =>
            {
                var mismatch = contractorId != 0 && c.ContractorId != contractorId;
                return new ReferenceHit(
                    c,
                    reference,
                    mismatch ? 0.60 : 1.00,
                    "numer KSeF z tytułu przelewu",
                    !mismatch && candidates.Count == 1);
            })
            .ToList();
    }

    public IReadOnlyList<OpenReceivable> All => _all;

    /// <summary>Records a bank account to contractor link (CDN.RachunkiBankowe).</summary>
    /// <remarks>
    /// The same contractor is registered only once per account, however many times the register
    /// spells it. ERP keeps one account in two places and in two forms - <c>RachunkiBankowe</c>
    /// without the country code, <c>NumeryRachunkow</c> with it - and both normalise to the same
    /// key here. Added twice, the account looked as though it hung on two cards, and an account
    /// on two cards names nobody: the payment lost its contractor and the note said, absurdly,
    /// that it belonged to "2 kartotek (KOM-BELT, KOM-BELT)". It affects 149 accounts in this
    /// register.
    /// </remarks>
    public void RegisterContractorAccount(string account, int contractorId)
    {
        var key = TextNormalizer.NormalizeAccount(account);
        if (key.Length < 10) return;
        AddDistinct(_contractorByAccount, key, contractorId);
    }

    /// <summary>Records a tax id to contractor link (CDN.KntKarty).</summary>
    public void RegisterContractorNip(string nip, int contractorId)
    {
        var key = TextNormalizer.DigitsOnly(nip);
        if (key.Length < 8) return;
        AddDistinct(_contractorByNip, key, contractorId);
    }

    /// <summary>Card acronyms - so that a justification can name specific contractors.</summary>
    private readonly Dictionary<int, string> _contractorAcronyms = [];

    public void RegisterContractorAcronym(int contractorId, string acronym)
    {
        if (acronym.Length > 0) _contractorAcronyms[contractorId] = acronym;
    }

    /// <summary>The card's acronym, or the bare identifier when we do not know it.</summary>
    public string AcronymOf(int contractorId) =>
        _contractorAcronyms.TryGetValue(contractorId, out var acronym) ? acronym : contractorId.ToString();

    /// <summary>
    /// Records a contractor name (Knt_Nazwa1, Knt_Nazwa2, Knt_Akronim) for recognition by payer
    /// name. Only contractors that have documents in the index are recorded - otherwise a surname
    /// such as "Kowalski" would produce hundreds of hits.
    /// </summary>
    public void RegisterContractorName(string? name, int contractorId)
    {
        if (!_byContractor.ContainsKey(contractorId)) return;

        var tokens = TextNormalizer.CompanyTokens(name);
        if (tokens.Length == 0) return;

        Add(_contractorByNameKey, string.Join(' ', tokens), contractorId);

        foreach (var token in tokens) Add(_contractorByNameToken, token, contractorId);

        if (_contractorTokens.TryGetValue(contractorId, out var existing))
        {
            _contractorTokens[contractorId] = [.. existing.Union(tokens, StringComparer.Ordinal)];
        }
        else
        {
            _contractorTokens[contractorId] = tokens;
        }
    }

    /// <summary>
    /// Looks a contractor up by the payer name supplied by the bank. It answers only when the
    /// answer is unambiguous - on a tie we would rather name nobody.
    /// </summary>
    public IReadOnlyList<int> FindContractorsByName(string? payerName)
    {
        var tokens = TextNormalizer.CompanyTokens(payerName);
        if (tokens.Length == 0) return [];

        if (_contractorByNameKey.TryGetValue(string.Join(' ', tokens), out var exact))
        {
            return exact.Distinct().ToList();
        }

        // Candidates are gathered only from tokens that actually narrow things down - a token
        // shared by hundreds of contractors ("AGRO", "TRANS") adds nothing and costs time.
        var candidates = new Dictionary<int, int>();
        var tokenSet = tokens.ToHashSet(StringComparer.Ordinal);

        foreach (var token in tokens)
        {
            if (!_contractorByNameToken.TryGetValue(token, out var bucket)) continue;
            if (bucket.Count > MaxContractorsPerNameToken) continue;

            foreach (var contractorId in bucket.Distinct())
            {
                candidates[contractorId] = candidates.GetValueOrDefault(contractorId) + 1;
            }
        }

        if (candidates.Count == 0) return [];

        var ranked = candidates
            .Select(pair =>
            {
                var contractorTokens = _contractorTokens[pair.Key];
                var matched = contractorTokens.Count(tokenSet.Contains);
                var denominator = Math.Min(tokens.Length, contractorTokens.Length);
                return (ContractorId: pair.Key, Matched: matched, Score: denominator == 0 ? 0d : matched / (double)denominator);
            })
            .Where(x => x.Score >= NameMatchThreshold)
            .Where(x => x.Matched >= 2 || IsDistinctiveSingleToken(tokenSet, _contractorTokens[x.ContractorId]))
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.Matched)
            .ToList();

        if (ranked.Count == 0) return [];

        // A tie means the name does not decide - two farms under the same surname, say.
        if (ranked.Count > 1 && ranked[1].Score >= ranked[0].Score && ranked[1].Matched >= ranked[0].Matched)
        {
            return [];
        }

        return [ranked[0].ContractorId];
    }

    /// <summary>
    /// A single-token name ("AGROLATGALE") decides only when that token is rare and is the sole
    /// carrier of information on both sides.
    /// </summary>
    private bool IsDistinctiveSingleToken(HashSet<string> payerTokens, string[] contractorTokens)
    {
        if (contractorTokens.Length != 1) return false;

        var token = contractorTokens[0];
        if (!payerTokens.Contains(token)) return false;
        if (token.Length < 5) return false;

        return _contractorByNameToken.TryGetValue(token, out var bucket) &&
               bucket.Distinct().Count() == 1;
    }

    /// <summary>Contractor cards ERP marks as archived - retired, kept for the old documents.</summary>
    private readonly HashSet<int> _archivedContractors = [];

    public void RegisterContractorArchived(int contractorId) => _archivedContractors.Add(contractorId);

    /// <summary>
    /// The contractors this account belongs to - the live ones where there are any.
    /// </summary>
    /// <remarks>
    /// An account left on a retired card does not make the payer ambiguous. A transfer to
    /// 86114011240000345094001001 named nobody because the number sits on SCHENKER and also on
    /// SPEDPOL, a card archived years ago when the one company absorbed the other; two cards means
    /// "cannot tell", so the payee was lost.
    ///
    /// Preferred rather than filtered out, and that distinction is the whole point: on this
    /// register 117 accounts hang on an archived card and on no other, and dropping archived cards
    /// outright would lose every one of them to gain the 27 collisions this resolves. Falling back
    /// to them when nothing live holds the account keeps both.
    /// </remarks>
    public IReadOnlyList<int> FindContractorsByAccount(string? account)
    {
        var key = TextNormalizer.NormalizeAccount(account);

        if (key.Length < 10 || !_contractorByAccount.TryGetValue(key, out var list)) return [];
        if (list.Count < 2 || _archivedContractors.Count == 0) return list;

        var live = list.Where(id => !_archivedContractors.Contains(id)).ToList();
        return live.Count > 0 ? live : list;
    }

    public IReadOnlyList<int> FindContractorsByNip(string? nip)
    {
        var key = TextNormalizer.DigitsOnly(nip);
        return key.Length >= 8 && _contractorByNip.TryGetValue(key, out var list) ? list : [];
    }

    public IReadOnlyList<OpenReceivable> ForContractor(int contractorId) =>
        _byContractor.TryGetValue(contractorId, out var list) ? list : [];

    /// <summary>
    /// Finds the receivables matching one reference and scores how good each match is.
    /// </summary>
    /// <param name="reference">The reference read out of the payment title.</param>
    /// <param name="contractorId">Contractor established for the payment (0 = unknown).</param>
    public IReadOnlyList<ReferenceHit> Resolve(DocumentReference reference, int contractorId)
    {
        var hits = new List<ReferenceHit>();

        // viaRelated is set when we reach an invoice through the number of a related document
        // (a goods issue or an order). The reference from the title then describes THAT document,
        // not the invoice: "ZS-57656/26/S" has by design a different type and series than the
        // invoice it produced, so the type and series comparisons must be made against the order.
        void Consider(
            IReadOnlyList<OpenReceivable> candidates,
            double baseScore,
            string reason,
            (DocumentKind Kind, int Number)? viaRelated = null)
        {
            if (candidates.Count == 0) return;

            // The contractor narrows the result: if any item belongs to them, the rest is dropped.
            var scoped = contractorId != 0
                ? candidates.Where(c => c.ContractorId == contractorId).ToList()
                : [];

            var effective = scoped.Count > 0 ? scoped : candidates;
            var contractorMismatch = scoped.Count == 0 && contractorId != 0;

            // The more documents share the same number, the less certain we are.
            var ambiguityPenalty = effective.Count == 1 ? 1.0 : 1.0 / effective.Count;

            foreach (var candidate in effective)
            {
                if (viaRelated is null && !reference.Kind.IsCompatibleWith(candidate.Kind)) continue;

                var score = baseScore * ambiguityPenalty;
                var note = reason;

                if (contractorMismatch)
                {
                    // The number hit, but under a contractor other than the payer account names.
                    score *= 0.30;
                    note += " (inny kontrahent niż nadawca przelewu)";
                }

                var candidateSeries = viaRelated is null
                    ? candidate.Series
                    : SeriesOfRelated(candidate, viaRelated.Value.Kind, viaRelated.Value.Number);
                var seriesMismatch = reference.Series is not null && candidateSeries.Length > 0 &&
                                     !string.Equals(reference.Series, candidateSeries, StringComparison.OrdinalIgnoreCase);

                if (seriesMismatch)
                {
                    score *= 0.75;
                    note += " (inna seria)";
                }

                var reliable = !contractorMismatch
                               && !seriesMismatch
                               && effective.Count == 1
                               && baseScore >= ReliableBaseScore;

                hits.Add(new ReferenceHit(candidate, reference, score, note, reliable));
            }
        }

        var year = reference.Year;

        if (reference.Kind != DocumentKind.Unknown && reference.Kind != DocumentKind.AnyInvoice && year is not null)
        {
            Consider(Get(_byKindNumberYear, (reference.Kind, reference.Number, year.Value % 100)),
                reference.Series is not null ? 1.00 : 0.95, "numer + rok + typ dokumentu");
        }

        if (hits.Count == 0 && reference.Kind != DocumentKind.Unknown && reference.Kind != DocumentKind.AnyInvoice)
        {
            Consider(Get(_byKindNumber, (reference.Kind, reference.Number)), 0.88, "numer + typ dokumentu");
        }

        if (hits.Count == 0 && year is not null)
        {
            Consider(Get(_byNumberYear, (reference.Number, year.Value % 100)),
                reference.Series is not null ? 0.92 : 0.84, "numer + rok");
        }

        if (hits.Count == 0)
        {
            var baseScore = reference.Strength switch
            {
                ReferenceStrength.FullNumber or ReferenceStrength.KindNumberYear => 0.80,
                ReferenceStrength.KindAndNumber => 0.78,
                ReferenceStrength.ListContinuation => 0.74,
                ReferenceStrength.InvoiceKeyword => 0.75,
                // A bare run of digits only means something within a known contractor -
                // globally such a number matches dozens of documents.
                _ => contractorId != 0 ? 0.75 : 0.18,
            };

            Consider(Get(_byNumber, reference.Number), baseScore, "sam numer");
        }

        // Related document numbers - the customer typed a goods issue or an order, not the invoice.
        if (hits.Count == 0)
        {
            foreach (var kind in new[] { DocumentKind.Wz, DocumentKind.Zs })
            {
                // "FAKTURA NR 57134" with no year yields an AnyInvoice reference, and the number
                // is sometimes copied off a proforma, that is off an order - so it is let in here too.
                var kindAllowsRelated = reference.Kind == DocumentKind.Unknown
                                        || reference.Kind == DocumentKind.AnyInvoice
                                        || reference.Kind == kind;

                if (!kindAllowsRelated) continue;

                var years = year is null ? RelatedYears(kind, reference.Number) : [year.Value % 100];
                foreach (var candidateYear in years)
                {
                    Consider(
                        Get(_byRelated, (kind, reference.Number, candidateYear)),
                        0.80,
                        kind == DocumentKind.Wz ? "numer WZ, z której powstała faktura" : "numer zamówienia, z którego powstała faktura",
                        (kind, reference.Number));
                }
            }
        }

        // The customer lost the leading digit ("3633" instead of "23633") - we search by the
        // ending, but only within a known contractor, or there would be hundreds of hits.
        if (hits.Count == 0 && contractorId != 0 && reference.Number is > 0 and < 100_000)
        {
            var suffix = reference.Number.ToString();
            var matches = ForContractor(contractorId)
                .Where(r => r.Number.ToString().EndsWith(suffix, StringComparison.Ordinal) &&
                            r.Number.ToString().Length == suffix.Length + 1)
                .ToList();

            if (matches.Count is > 0 and <= 3)
            {
                Consider(matches, 0.45, "numer z urwaną pierwszą cyfrą");
            }
        }

        return hits;
    }

    /// <summary>Years in which a related document with the given number exists - used when the customer gave no year.</summary>
    private IReadOnlyList<int> RelatedYears(DocumentKind kind, int number) =>
        _relatedYears.TryGetValue((kind, number), out var years) ? years : [];

    /// <summary>Series of the related document through which we reached this receivable.</summary>
    private static string SeriesOfRelated(OpenReceivable candidate, DocumentKind kind, int number) =>
        candidate.RelatedNumbers
            .FirstOrDefault(r => r.Kind == kind && r.Number == number)?.Series ?? string.Empty;

    private static IReadOnlyList<T> Get<TKey, T>(Dictionary<TKey, List<T>> map, TKey key) where TKey : notnull =>
        map.TryGetValue(key, out var list) ? list : [];

    private static void Add<TKey, T>(Dictionary<TKey, List<T>> map, TKey key, T value) where TKey : notnull
    {
        if (!map.TryGetValue(key, out var list))
        {
            list = [];
            map[key] = list;
        }
        list.Add(value);
    }

    /// <summary>
    /// As <see cref="Add{TKey,T}"/>, but a value already under that key is not repeated.
    /// </summary>
    /// <remarks>
    /// For the maps whose length is read as evidence - one contractor under an account means the
    /// payer is known, two mean nobody is - a repeat is not a second contractor and must not count
    /// as one. The lists are short (an account or a tax id belongs to one card, rarely a handful),
    /// so scanning them costs nothing.
    /// </remarks>
    private static void AddDistinct<TKey, T>(Dictionary<TKey, List<T>> map, TKey key, T value)
        where TKey : notnull
    {
        if (!map.TryGetValue(key, out var list))
        {
            map[key] = [value];
            return;
        }

        if (!list.Contains(value)) list.Add(value);
    }
}
