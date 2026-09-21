namespace Gaska.Payments.Domain.Cards;

/// <summary>What a card transaction did to the money.</summary>
public enum CardTransactionKind
{
    /// <summary>A sale paid by card - type 00. The customer pays us.</summary>
    Sale,

    /// <summary>A credit to the card - type 20, "uznanie". We pay the customer back.</summary>
    Refund,

    /// <summary>
    /// Cash withdrawn at the till or cashback - types 01 and 09. Neither is a sale, and neither
    /// has ever turned up on KARTA, so the service books nothing for them and says so.
    /// </summary>
    Other,
}

/// <summary>
/// One transaction from Fiserv's report, as the terminal recorded it.
/// </summary>
/// <param name="Point">
/// Fiserv's number for the point of sale. Gąska has two, 73346135 at the shop counter and
/// 76381820, and their transaction numbers run separately - 3907 on one and 414 on the other in
/// the same week - so a transaction is only identified by the pair.
/// </param>
/// <param name="Number">
/// The transaction number the terminal printed. The cashier types it into the payment notes of
/// the receipt or invoice (<c>TrP_Notatki</c>), and that is how the document is found.
/// </param>
/// <param name="Batch">
/// The batch the terminal sent the transaction in. Fiserv pays batch by batch, and its transfer
/// names the batches it pays - which is how the commission is worked out.
/// </param>
public sealed record CardTransaction(
    string Point,
    string Terminal,
    string Number,
    string Batch,
    DateTime Date,
    TimeSpan Time,
    string TypeCode,
    decimal Amount,
    string Card,
    string CardSystem)
{
    public CardTransactionKind Kind => TypeCode switch
    {
        "00" or "0" => CardTransactionKind.Sale,
        "20" => CardTransactionKind.Refund,
        _ => CardTransactionKind.Other,
    };

    /// <summary>What the transaction does to the balance of the register: a refund takes away.</summary>
    public decimal SignedAmount => Kind == CardTransactionKind.Refund ? -Amount : Amount;

    /// <summary>The point and the batch together - batch numbers are the point's own.</summary>
    public string BatchKey => $"{Point}/{Batch}";
}

/// <summary>One Fiserv report: a list of transactions over a period, usually a day.</summary>
public sealed record CardReport(
    string Merchant,
    DateTime PeriodFrom,
    DateTime PeriodTo,
    IReadOnlyList<CardTransaction> Transactions);
