namespace Gaska.Payments.Domain.Model;

/// <summary>
/// The settlement state of a document payment in ERP (<c>CDN.TraPlat.TrP_Rozliczona</c>).
/// </summary>
/// <remarks>
/// Three states, not two, and the third is the one that matters here:
///
/// <list type="bullet">
///   <item>0 - open. Waiting for money and settled when it arrives.</item>
///   <item>1 - settled. Nothing left on it.</item>
///   <item>
///     2 - not to be settled. The "nie rozliczaj" box on the payment in XL. The amount stays
///     outstanding for good: on this register 14 362 payments carry it, and 14 308 of them still
///     show their full amount, the oldest due in 2015.
///   </item>
/// </list>
///
/// The same convention runs through the cash entries, where <c>KAZ_Rozliczony = 2</c> already
/// means an entry excluded from settlement - it is what the service sets on a courier's payout
/// once its parcels are booked.
///
/// Asking for "not settled" as <c>&lt;&gt; 1</c> therefore lets the third state through, and it is
/// not a small leak: of the payments that reach the accountant's list on settleable document
/// types, 3 035 carry the flag against 1 247 genuinely open ones - 111.7 million against 15.7.
/// The list was mostly documents the accountants had already decided not to pursue, and the
/// matching engine was free to allocate incoming money against them.
/// </remarks>
public static class PaymentSettlement
{
    /// <summary>Payments that are actually open, for a <c>WHERE</c> clause.</summary>
    /// <remarks>
    /// Written as an equality rather than as "not settled and not excluded" on purpose: an ERP
    /// upgrade that adds a fourth state would then leave it out of settlement until somebody
    /// decides what it means, which is the safe direction when the subject is money.
    /// </remarks>
    public const string OpenSql = "TrP_Rozliczona = 0";

    /// <summary>
    /// Payments that belong to a contractor - the only ones the automat may settle.
    /// </summary>
    /// <remarks>
    /// A payment in <c>CDN.TraPlat</c> hangs on a party of any kind: 32 is a contractor, 4304 a
    /// tax office (VAT and CIT returns), 944 an employee (payroll). The numbers are drawn from
    /// separate sequences, so an office's number is also somebody's <c>Knt_GIDNumer</c> - on the
    /// live register all 26 open non-contractor payments collide with a real contractor card,
    /// including the II Urząd Skarbowy, whose number 4 belongs to the contractor with acronym
    /// "0002". Joining on the number alone put a tax return on that contractor's list of debts.
    ///
    /// Offices and employees are excluded rather than supported: settling them means an entry
    /// posted against a party of another type, which <c>XLRozliczaj</c> and the party update both
    /// take as a contractor. Left out, such a transfer simply waits for a human.
    /// </remarks>
    public const string ContractorSql = "TrP_KntTyp = 32";

    /// <summary>The party types a cash entry can be booked against, for reading them back.</summary>
    public const int ContractorParty = 32;
    public const int OfficeParty = 4304;
    public const int EmployeeParty = 944;
}
