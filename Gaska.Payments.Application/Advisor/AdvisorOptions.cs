namespace Gaska.Payments.Application.Advisor;

/// <summary>
/// The language model asked about the transfers the engine could not settle on its own.
/// </summary>
public sealed class AdvisorOptions
{
    public const string SectionName = "Advisor";

    public bool Enabled { get; set; }

    /// <summary>The n8n webhook the question goes to.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>
    /// How long one answer is waited for. The model reads the contractor's documents one at a
    /// time and a busy contractor keeps it going for a quarter of an hour.
    /// </summary>
    public int TimeoutMinutes { get; set; } = 30;

    /// <summary>How often the queue is looked at for new transfers when nothing is running.</summary>
    public int PollSeconds { get; set; } = 60;

    /// <summary>
    /// How far back a transfer may be booked and still be asked about. What is older has been
    /// through the accountants' hands already, and asking about it only delays today's.
    /// </summary>
    public int LookbackDays { get; set; } = 14;

    /// <summary>How many times one transfer is tried before it is left alone.</summary>
    public int MaxAttempts { get; set; } = 3;

    /// <summary>How long to wait before trying a failed transfer again - multiplied by the attempt.</summary>
    public int RetryAfterMinutes { get; set; } = 30;

    /// <summary>
    /// The engine's verdicts that are worth a second opinion: nothing found, or only a guess.
    /// A Medium proposal already balances and waits for an accountant's approval.
    /// </summary>
    /// <remarks>
    /// An array, not a list: the configuration binder appends to a list it finds filled in, so the
    /// defaults and the configured values came out together, each twice.
    /// </remarks>
    public string[] Confidences { get; set; } = ["None", "Low"];

    /// <summary>
    /// Payers whose transfers have a settlement of their own and are not a question for the
    /// model: Fiserv's payouts, the couriers' cash on delivery transfers.
    /// </summary>
    public List<int> ExcludedContractorIds { get; set; } = [];
}
