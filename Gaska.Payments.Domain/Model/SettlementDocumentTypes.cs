namespace Gaska.Payments.Domain.Model;

/// <summary>
/// Document types Comarch ERP XL will not settle against a cash entry.
/// </summary>
/// <remarks>
/// They have rows in <c>CDN.TraPlat</c> and look like open items, but <c>XLRozliczaj</c> rejects
/// such a pair with "Blad wewnetrzny procedury Rozrachunek. Bledne parametry dla trybu = 4".
///
/// The list comes from the data, not from guesswork: these are the types that have open rows in
/// <c>CDN.TraPlat</c> and never once appear in <c>CDN.Rozliczenia</c> paired with a cash entry
/// (GIDTyp 784) - across more than a million historical settlements.
///
/// We show them neither to the automat nor to the accountant: a proposal that cannot be carried
/// out is worse than no proposal at all.
/// </remarks>
public static class SettlementDocumentTypes
{
    /// <summary>Payment schedule - a plan of outflows, not an open item (11,287 open rows).</summary>
    public const int PaymentSchedule = 7684;

    /// <summary>Dunning letter - a collection document with nothing to settle (1,936 open rows).</summary>
    public const int DunningLetter = 2833;

    /// <summary>Opening balance - closed when the accounting period is closed, not by a transfer.</summary>
    public const int OpeningBalance = 7680;

    /// <summary>Types excluded from settlement, ready to drop into a <c>NOT IN</c> clause.</summary>
    public static readonly IReadOnlyList<int> NotSettleable =
        [PaymentSchedule, DunningLetter, OpeningBalance];

    /// <summary>The list of types in a form that can be pasted into a SQL query.</summary>
    public static string NotSettleableSql { get; } = string.Join(", ", NotSettleable);
}
