namespace Gaska.Payments.Domain;

public sealed class MatchingOptions
{
    /// <summary>
    /// Document series used in the company (CDN.TraNag.TrN_TrNSeria). They tell a real series
    /// apart from stray letters trailing a number ("...26/SPR Z DNIA" gives series SPR).
    /// </summary>
    public IReadOnlyCollection<string> KnownSeries { get; init; } =
        ["SPRK", "SPR", "WDTK", "WDT", "DETK", "DET", "SK", "S", "K", "ZT"];

    /// <summary>Longest bare run of digits still accepted as a possible document number.</summary>
    public int MaxBareNumberLength { get; init; } = 6;

    /// <summary>Absolute tolerance for deciding that a set of documents adds up to the amount.</summary>
    /// <remarks>
    /// Zero on purpose. This one governs the subset search, which tries every combination of a
    /// contractor's open items: any slack there multiplies into false matches, because among
    /// dozens of documents some combination is always within a few groszy of anything.
    /// </remarks>
    public decimal AbsoluteAmountTolerance { get; init; } = 0.00m;

    /// <summary>
    /// Largest difference between the amount and the documents named in the title that is still
    /// taken for a rounding difference rather than a different set of documents.
    /// </summary>
    /// <remarks>
    /// Slack is safe here in a way it is not in the subset search: there is nothing to choose
    /// between, the customer has said which documents they are paying and every one was found.
    /// The question is only whether the few groszy between their sum and the transfer mean the
    /// customer meant something else, and at five groszy they do not - the smallest document in
    /// such a set is worth tens of zloty.
    ///
    /// Without it a payment naming two invoices and three corrections
    /// ("PRZELEW 41705,41913 KOREKTY 2986,2989,2991", 2 500,97 against 2 500,93) was thrown back
    /// to spreading the amount over the invoices oldest-first, which part-paid the second one by
    /// 468,50 and dropped all three corrections - a worse answer than the one rejected for being
    /// four groszy out.
    /// </remarks>
    public decimal ReferenceSumTolerance { get; init; } = 0.05m;

    /// <summary>How many of a contractor's receivables are considered when searching for a subset summing to the payment.</summary>
    public int SubsetSearchPoolLimit { get; init; } = 40;

    /// <summary>Largest number of documents in a single settlement proposal.</summary>
    public int MaxDocumentsPerPayment { get; init; } = 25;

    /// <summary>
    /// Whether to fall back to spreading a payment over the oldest receivables when there is no
    /// evidence at all. Off by default: on historical data the strategy never once hit
    /// (0 out of 25 payments) and it filled the operator's queue with proposals that only looked
    /// ready.
    /// </summary>
    public bool EnableOldestFirstFallback { get; init; }

    /// <summary>Lowest reference score at which a reference counts as named explicitly.</summary>
    public double ExplicitReferenceScoreThreshold { get; init; } = 0.70;

    /// <summary>Our own company's tax id - payments from ourselves are skipped.</summary>
    public string OwnNip { get; init; } = string.Empty;

    /// <summary>
    /// Whether to identify the contractor by the payer name once the account and the tax id have
    /// both failed. Recognition by name is inherently less certain, so it can be switched off.
    /// </summary>
    public bool EnablePayerNameLookup { get; init; } = true;

    /// <summary>
    /// Whether <c>High</c> confidence requires the payer to have been recognised by a bank
    /// account attached to the contractor in ERP. Every other piece of evidence rests on the
    /// payment title, that is on what the payer typed - too little to post without a human.
    /// </summary>
    public bool RequireBankAccountForHighConfidence { get; init; } = true;
}
