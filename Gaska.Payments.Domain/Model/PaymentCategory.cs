namespace Gaska.Payments.Domain.Model;

/// <summary>
/// What kind of money movement this is. It decides the cash operation symbol in ERP, whether a
/// party reaches the entry, and whether anything is settled against documents.
/// </summary>
/// <remarks>
/// It lives in the domain because both sides need the same words: the cycles that write the
/// category and the posting code that reads it sit in different projects, and the names were being
/// spelled out by hand on one side of that line.
/// </remarks>
public static class PaymentCategory
{
    /// <summary>An ordinary transfer from or to a contractor.</summary>
    public const string Standard = "Standard";

    /// <summary>A bank commission or charge - a separate operation in ERP (PRW).</summary>
    public const string BankFee = "BankFee";

    /// <summary>A leg of the split payment mechanism - operation SPLIU or SPLIO.</summary>
    public const string SplitPayment = "SplitPayment";

    /// <summary>
    /// An operation from a credit card account. It reaches ERP as a cash entry booked on the
    /// anonymous party and that is all - nothing here is matched or settled.
    /// </summary>
    /// <remarks>
    /// The register decides this, not the content of the operation, so
    /// the classifier does not assign this category - the cycle itself
    /// does, because it knows which account the statement came from.
    /// </remarks>
    public const string Card = "Card";

    /// <summary>
    /// An operation from a register we only post to: the social fund, the auxiliary social
    /// insurance account, the grant accounts. Treated exactly like <see cref="Card"/> - the
    /// separate name exists so that the application does not put a card icon next to the social
    /// fund account.
    /// </summary>
    public const string PostOnly = "PostOnly";

    /// <summary>Kinds that are neither matched against documents nor settled.</summary>
    public static bool WithoutSettlement(string category) =>
        category is Card or PostOnly;

    /// <summary>
    /// A parcel collected on delivery, paid to us by the courier. Settled like an ordinary
    /// receipt, but it reaches us from a report rather than from a bank statement.
    /// </summary>
    public const string Cod = "Cod";
}
