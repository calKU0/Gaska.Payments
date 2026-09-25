using Gaska.Payments.Desktop.Data;
using Gaska.Payments.Desktop.Mvvm;

namespace Gaska.Payments.Desktop.ViewModels;

/// <summary>
/// An entry in a multiple-choice filter: a label and a selection flag.
/// </summary>
/// <remarks>
/// Every filter in the bar behaves the same way - any subset of the entries may be ticked, rather
/// than one value chosen. A shared base class lets their appearance and their summary be
/// described once.
/// </remarks>
public abstract class FilterOption(string label, bool selected) : ObservableObject
{
    private bool _isSelected = selected;

    public string Label { get; } = label;

    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }

    /// <summary>
    /// The text on the collapsed filter: the name while exactly one entry is ticked, the count
    /// from two on.
    /// </summary>
    /// <remarks>
    /// Two names already make the button wider than the filters beside it - "nierozliczone,
    /// rozliczone częściowo" is the settlement filter's own default - and the bar has to fit six
    /// of these. One name is worth spelling out because it says what the list is narrowed to; past
    /// that the ticks in the open list say it better than a caption can.
    /// </remarks>
    public static string Summarize(IReadOnlyCollection<FilterOption> options)
    {
        var chosen = options.Where(o => o.IsSelected).ToList();

        return chosen.Count switch
        {
            0 => "nic nie wybrano",
            var n when n == options.Count => "wszystkie",
            1 => chosen[0].Label,
            var n => $"wybrano {n} z {options.Count}",
        };
    }
}

/// <summary>
/// An entry in the register filter: the series symbol and the icon drawn beside it.
/// </summary>
/// <remarks>
/// Which registers start unticked is a setting of its own (<c>Settlement:UncheckedRegisters</c>),
/// not a consequence of whether the service settles there. They are one click away either way.
/// </remarks>
public sealed class RegisterOption(string series, bool isCard, bool startsUnchecked)
    : FilterOption(series.Length == 0 ? "bez rejestru" : series, selected: !startsUnchecked)
{
    public string Series { get; } = series;

    /// <summary>Icon code for the <c>RegisterIcon</c> style - a country code or the card marker.</summary>
    public string Icon { get; } = RegisterIcons.For(series, isCard);
}

/// <summary>
/// An entry in the confidence filter: a Polish label and the code the style picks a badge colour
/// from.
/// </summary>
/// <remarks>
/// The code is here so that the filter looks exactly like the column on the list - the same badge
/// styles react to <c>Confidence</c>, so the colours are described in one place.
/// </remarks>
public sealed class ConfidenceOption(string label, string confidence)
    : FilterOption(label, selected: true)
{
    public string Confidence { get; } = confidence;
}

/// <summary>An entry in the direction filter - money in or money out.</summary>
public sealed class DirectionOption(string label, bool isIncoming, bool selected)
    : FilterOption(label, selected)
{
    public bool IsIncoming { get; } = isIncoming;
}

/// <summary>
/// An entry in the settlement state filter - ticked independently of the others.
/// </summary>
/// <remarks>
/// The default view combines two states: unsettled and partly settled. Ticking all three means
/// everything.
/// </remarks>
public sealed class SettlementFilterOption(SettlementState state, string label, bool selected)
    : FilterOption(label, selected)
{
    public SettlementState State { get; } = state;

    /// <summary>
    /// The states a transfer in the queue can be in.
    /// </summary>
    /// <remarks>
    /// Fully settled ones and those flagged "nie rozliczaj" start unticked: both are finished
    /// business, and the queue is a list of work. They are one tick away, which is what makes it
    /// possible to find a transfer somebody flagged by mistake and take the flag off again.
    /// </remarks>
    public static IReadOnlyList<SettlementFilterOption> ForQueue() =>
    [
        new(SettlementState.Unsettled, "nierozliczone", selected: true),
        new(SettlementState.Partial, "rozliczone częściowo", selected: true),
        new(SettlementState.Settled, "rozliczone w pełni", selected: false),
        new(SettlementState.DoNotSettle, "nie rozliczaj", selected: false),
    ];

    /// <summary>
    /// The same filter over a contractor's documents.
    /// </summary>
    /// <remarks>
    /// "Rozliczone" means the documents this very transfer closed - not the contractor's settled
    /// history, which is ERP's to show and would bury the few rows that answer the question. They
    /// start hidden, because on a transfer there is still work on they are beside the point; on a
    /// transfer that is settled they are the whole answer and appear without being asked for.
    /// </remarks>
    public static IReadOnlyList<SettlementFilterOption> ForDocuments() =>
    [
        new(SettlementState.Unsettled, "nierozliczone", selected: true),
        new(SettlementState.Partial, "rozliczone częściowo", selected: true),
        new(SettlementState.Settled, "rozliczone", selected: false),
        new(SettlementState.DoNotSettle, "nie rozliczaj", selected: false),
    ];
}

/// <summary>An entry in the document side filter - a receivable or a liability.</summary>
/// <remarks>
/// Both sides ticked by default. A contractor who both buys and sells is settled across the two -
/// a purchase invoice nets off against a receivable - so a list showing one side is a list that
/// does not add up. The filter is there to narrow it down when somebody wants that, not to decide
/// for them.
///
/// A ticked document is shown whatever the filter says: hiding one would hide money that is about
/// to be settled while its amount still stands in the totals underneath.
/// </remarks>
public sealed class DocumentSideOption(string label, bool isLiability, bool selected)
    : FilterOption(label, selected)
{
    public bool IsLiability { get; } = isLiability;
}


/// <summary>
/// The icon shown beside a bank register's symbol.
/// </summary>
/// <remarks>
/// The name deliberately is not "Registers": that is what the view model's property holding the
/// register collection is called, and inside it an instance member would shadow this class.
/// </remarks>
public static class RegisterIcons
{
    /// <summary>A credit card account - a card is drawn instead of a flag.</summary>
    public const string Card = "CARD";

    /// <summary>Cash on delivery - a parcel, because that register holds no bank account at all.</summary>
    public const string Parcel = "PARCEL";

    /// <summary>
    /// The icon for a register. Empty when we have nothing truthful to draw, and then no icon is
    /// shown at all.
    /// </summary>
    /// <remarks>
    /// One rule in one place, because both the queue's column and the register filter ask the same
    /// question and used to answer it separately.
    ///
    /// Registers are divided by currency rather than by country: FORUE is the euro account, FORUS
    /// the dollar one. The series symbol does not carry a country code outright - "UE" is no ISO
    /// code, FORVA is the VAT account, ZFŚS the social fund - so they are listed by name. A symbol
    /// outside the list is not guessed at from its last letters: better to show no flag than
    /// somebody else's.
    ///
    /// The card registers are not in the list. What says an account belongs to a card is the
    /// register's place in configuration, not the shape of its symbol - KART1 and FORKG are both
    /// cards, and nothing about the second says so.
    /// </remarks>
    public static string For(string series, bool isCard) =>
        isCard ? Card : CodeOf(series);

    private static string CodeOf(string series) => series.Trim().ToUpperInvariant() switch
    {
        // The cash register the courier payouts land on. Not a bank account, so no flag.
        "K_GLS" => Parcel,
        // Every złoty account: the current one, the VAT one, the social fund, the auxiliary
        // social insurance account and the second grant account.
        "FORPL" or "FORVA" or "ZFŚS" or "FORZU" or "FORD2" => "PL",
        "FORUE" => "EU",
        "FORUS" => "US",
        "FORRO" => "RO",
        _ => string.Empty,
    };
}
