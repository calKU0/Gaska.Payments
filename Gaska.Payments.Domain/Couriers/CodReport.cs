namespace Gaska.Payments.Domain.Couriers;

/// <summary>One courier payout report: a collective transfer broken down into parcels.</summary>
/// <param name="Format">Which reader produced this - <c>GlsCsv</c>, <c>DpdXls</c>, <c>FedexReport</c>.</param>
/// <param name="PayoutDate">
/// The day the courier transfers the money. This is the date the cash entries carry, so that the
/// COD register shows the receipt on the day it actually happened.
/// </param>
/// <param name="PayoutTotal">
/// The collective transfer as the courier states it. Kept to be checked against the sum of the
/// parcels - in all three formats they agree to the penny, and a divergence means we have read
/// the file wrongly.
/// </param>
/// <param name="Reference">The courier's own reference for the payout, when it gives one.</param>
/// <param name="Account">
/// The account the courier is paying the money into, when the report names one. Not always us:
/// under FedEx's dropshipping service the customer is paid directly, and those reports name the
/// customer's own account. Fifteen of the nineteen reports in the mailbox are of that kind.
/// </param>
public sealed record CodReport(
    string Format,
    DateTime PayoutDate,
    decimal PayoutTotal,
    string Reference,
    string Account,
    IReadOnlyList<CodParcel> Parcels)
{
    public decimal ParcelTotal => Parcels.Sum(p => p.Amount);

    /// <summary>Whether the parcels add up to the payout the courier declares.</summary>
    public bool AddsUp => Math.Abs(ParcelTotal - PayoutTotal) <= 0.004m;

    /// <summary>
    /// Whether the courier pays each parcel with a transfer of its own.
    /// </summary>
    /// <remarks>
    /// True only for Hellmann. The others send one transfer covering the whole file, which is what
    /// lets the sum be checked against it: parcels that do not add up to the money received mean
    /// the file was read wrongly, and nothing from it is settled.
    ///
    /// Hellmann gives no such check - there is no collective transfer and no total in the file - so
    /// each parcel is paired with its own transfer instead, by the order number the title carries,
    /// and the amounts are compared one to one. That is a stronger check per parcel than the
    /// collective one, not a weaker one: the transfer has to be worth exactly what the row says.
    /// </remarks>
    public bool PaysPerParcel { get; init; }
}

/// <summary>
/// One parcel from a payout report - one customer's payment.
/// </summary>
/// <param name="Waybill">
/// The courier's waybill number, which is <c>CDN.Wysylki.WYS_NumerObcy</c> on our side. This is
/// what identifies the parcel: it is in the file, it is in ERP, and we write it onto the cash
/// entry, so a report read twice cannot produce the entry twice.
/// </param>
/// <param name="Amount">The amount collected from the customer.</param>
/// <param name="DocumentNumbers">
/// The documents the courier names on the parcel. Some parcels carry two invoices, and then both
/// are here - the entry stays one, and the settlement engine divides it between them.
/// </param>
/// <param name="Recipient">Who received the parcel, as the courier spells it.</param>
public sealed record CodParcel(
    string Waybill,
    decimal Amount,
    IReadOnlyList<string> DocumentNumbers,
    string Recipient);
