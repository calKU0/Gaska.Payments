namespace Gaska.Payments.Domain.Model;

public enum MatchConfidence
{
    /// <summary>Nothing sensible was found - the entry needs a human.</summary>
    None = 0,

    /// <summary>A guess (FIFO, for instance) - only ever a hint.</summary>
    Low = 1,

    /// <summary>The set of documents balances, but the evidence is not conclusive - needs an operator's approval.</summary>
    Medium = 2,

    /// <summary>Documents are named unambiguously and the amount agrees - fit for automatic settlement.</summary>
    High = 3,
}

/// <summary>How the engine arrived at a proposal - for auditing and for tuning the rules.</summary>
public enum MatchStrategy
{
    NoCandidates,

    /// <summary>Documents named in the title, their sum equal to the amount received.</summary>
    ExplicitReferencesExactSum,

    /// <summary>
    /// Documents named in the title, their sum apart from the amount by a few groszy - a rounding
    /// difference, not a different set of documents.
    /// </summary>
    ExplicitReferencesRounding,

    /// <summary>Documents named in the title, but the sum differs from the amount (over-, under- or part payment).</summary>
    ExplicitReferencesPartial,

    /// <summary>A single document named, the payment covering part of it.</summary>
    SingleDocumentPartialPayment,

    /// <summary>The title yielded nothing, but exactly one subset of the contractor's receivables sums to the amount.</summary>
    SubsetSumOnContractor,

    /// <summary>Named documents topped up with further items of the contractor so that the sum agrees.</summary>
    ReferencesExtendedBySubsetSum,

    /// <summary>Last resort - spreading the payment over the oldest receivables first.</summary>
    OldestFirstFallback,

    /// <summary>
    /// The bank returned the order reference XL assigned to the payment when it was sent, so the
    /// transfer names a document directly, without reading the title.
    /// </summary>
    BankOrderReference,

}

/// <summary>A single settlement line: how much of the payment goes to one document.</summary>
public sealed record MatchAllocation(
    OpenReceivable Receivable,
    decimal Amount,
    double Score,
    string Reason);

public sealed record MatchResult
{
    public required BankPayment Payment { get; init; }

    public required MatchConfidence Confidence { get; init; }

    public required MatchStrategy Strategy { get; init; }

    public IReadOnlyList<MatchAllocation> Allocations { get; init; } = [];

    /// <summary>Contractor established for the payment (0 = unknown).</summary>
    public int ContractorId { get; init; }

    public string ContractorSource { get; init; } = string.Empty;

    /// <summary>
    /// Whether the payer's account is what identified the contractor. Recognition from the
    /// payment title or from the payer name is only circumstantial - enough for a proposal here,
    /// but not enough to put the contractor on an ERP cash entry.
    /// </summary>
    public bool ContractorFromBankAccount { get; init; }

    /// <summary>Document references read out of the payment title.</summary>
    public IReadOnlyList<DocumentReference> References { get; init; } = [];

    /// <summary>The part of the amount that could not be assigned to any document.</summary>
    public decimal Unallocated { get; init; }

    public IReadOnlyList<string> Notes { get; init; } = [];

    public decimal AllocatedTotal => Allocations.Sum(a => a.Amount);
}
