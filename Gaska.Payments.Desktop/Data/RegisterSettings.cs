namespace Gaska.Payments.Desktop.Data;

/// <summary>
/// The bank registers the application shows, read from its own <c>appsettings.json</c>.
/// </summary>
/// <remarks>
/// The list used to be derived from whatever happened to be in the queue, which meant a register
/// with no rows simply did not exist as far as the filter was concerned - and the accountant could
/// not tell "nothing here today" from "not handled at all".
///
/// It also decides which registers are shown without settlement (cards and auxiliary accounts).
/// The service records that in <c>PostingCategory</c>, but only for operations it downloaded
/// itself; entries that reached ERP through its own statement import carry no category, and those
/// are exactly the ones on the card registers.
/// </remarks>
public sealed class RegisterSettings
{
    public const string SectionName = "Settlement";

    /// <summary>Registers whose entries are matched and settled.</summary>
    public List<string> Registers { get; set; } = [];

    /// <summary>Credit card accounts - shown with a card icon and unticked by default.</summary>
    public List<string> CardRegisters { get; set; } = [];

    /// <summary>The social fund and auxiliary accounts - unticked by default, like the cards.</summary>
    public List<string> PostOnlyRegisters { get; set; } = [];

    /// <summary>
    /// Registers whose filter entry starts unticked. Everything else starts ticked.
    /// </summary>
    /// <remarks>
    /// A list of its own, because "the service does not settle here" and "do not show me this by
    /// default" are two different statements and were being made with one setting. The COD register
    /// is the case that proved it: its entries are settled, so it belongs in
    /// <see cref="Registers"/>, but there are thousands of them and an accountant opening the
    /// window does not want to wade through them.
    ///
    /// Omitting a register from this list means it is shown, which is the safer way round: a new
    /// register that nobody has thought about appears rather than hides.
    /// </remarks>
    public List<string> UncheckedRegisters { get; set; } = [];

    /// <summary>Every register the application shows, in the order the filter lists them.</summary>
    public IReadOnlyList<string> All => [.. Registers, .. CardRegisters, .. PostOnlyRegisters];

    /// <summary>Whether this register's filter entry starts unticked.</summary>
    public bool StartsUnchecked(string series) => Contains(UncheckedRegisters, series);

    public bool IsCard(string series) => Contains(CardRegisters, series);

    /// <summary>Whether the service leaves this register alone - cards and auxiliary accounts.</summary>
    public bool WithoutSettlement(string series) =>
        Contains(CardRegisters, series) || Contains(PostOnlyRegisters, series);

    private static bool Contains(List<string> registers, string series) =>
        registers.Contains(series.Trim(), StringComparer.OrdinalIgnoreCase);
}
