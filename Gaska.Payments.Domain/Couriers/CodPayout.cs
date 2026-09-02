namespace Gaska.Payments.Domain.Couriers;

/// <summary>
/// A courier's collective transfer as it sits in ERP - the money a payout report describes.
/// </summary>
/// <param name="EntryId">The bank cash entry (<c>CDN.Zapisy.KAZ_GIDNumer</c>).</param>
/// <param name="BookedOn">
/// The day the money reached the account. This, not the date printed in the report, is what every
/// entry made from that report carries: for the GLS report of 2026-05-25 the transfer arrived on
/// the 26th, and the books follow the money.
/// </param>
/// <param name="Title">
/// The payment title, as the bank sent it. It is what identifies the report: FedEx writes
/// <c>Ref.platnosc:5415026</c>, DPD and GLS list the waybill numbers.
/// </param>
public sealed record CodPayout(
    int EntryId,
    DateTime BookedOn,
    decimal Amount,
    string Title,
    string Register,
    bool AlreadyNotForSettlement)
{
    /// <summary>
    /// The title with its whitespace removed, in upper case.
    /// </summary>
    /// <remarks>
    /// The bank breaks a title into 35-character lines and ERP stores them run together, so a
    /// number is regularly cut in half by a space it never had: "fs-245 66/26/spr". Closing the
    /// gaps puts it back together while keeping the letters and punctuation that tell one kind of
    /// number from another.
    /// </remarks>
    public string Compact { get; } =
        new string([.. Title.Where(c => !char.IsWhiteSpace(c))]).ToUpperInvariant();

    /// <summary>The title reduced to its digits - what the waybills are looked for in.</summary>
    public string Digits { get; } = new([.. Title.Where(char.IsAsciiDigit)]);

    /// <summary>How well this transfer answers to a report - and by what.</summary>
    public CodPayoutMatch Match(CodReport report)
    {
        var agrees = Math.Abs(Amount - report.ParcelTotal) <= 0.004m;

        if (NamesReference(report.Reference))
        {
            return new CodPayoutMatch(this, ByReference: true, Waybills: 0, Agrees: agrees);
        }

        // Waybills are looked for among the digits alone. They are eleven to thirteen digits of
        // pure number, so a chance hit is not a real worry - and the bank's line breaks leave no
        // other way to find them whole.
        var hits = report.Parcels.Count(p => p.Waybill.Length > 4
            && Digits.Contains(OnlyDigits(p.Waybill), StringComparison.Ordinal));

        return new CodPayoutMatch(this, ByReference: false, Waybills: hits, Agrees: agrees);
    }

    /// <summary>
    /// Whether the title names the courier's payment reference as a value of its own.
    /// </summary>
    /// <remarks>
    /// Compared whole, letters included, and only where it is not part of a longer number. Reduced
    /// to digits it is far too weak a thing to match on: DPD's reference A24862 becomes "24862",
    /// which duly turned up inside the invoice number FS-24862/26/SPR in the title of an unrelated
    /// transfer, and a fifty-thousand-złoty report was matched to a four-thousand-złoty payment.
    /// </remarks>
    private bool NamesReference(string reference)
    {
        var wanted = new string([.. reference.Where(c => !char.IsWhiteSpace(c))]).ToUpperInvariant();
        if (wanted.Length < 5) return false;

        for (var at = Compact.IndexOf(wanted, StringComparison.Ordinal); at >= 0;
             at = Compact.IndexOf(wanted, at + 1, StringComparison.Ordinal))
        {
            var before = at == 0 || !char.IsAsciiDigit(Compact[at - 1]);
            var end = at + wanted.Length;
            var after = end == Compact.Length || !char.IsAsciiDigit(Compact[end]);

            if (before && after) return true;
        }

        return false;
    }

    private static string OnlyDigits(string value) => new([.. value.Where(char.IsAsciiDigit)]);
}

/// <summary>How strongly a transfer answers to a report.</summary>
/// <param name="ByReference">
/// The courier's own payment reference was found in the title. That is a name the courier gave the
/// payout, so one hit settles the question.
/// </param>
/// <param name="Waybills">
/// How many of the report's waybills the title lists. DPD and GLS name no reference and simply
/// print the parcels, of which only the first few fit - so this is a count rather than a flag.
/// </param>
/// <param name="Agrees">Whether the transfer is worth exactly what the report adds up to.</param>
public sealed record CodPayoutMatch(CodPayout Payout, bool ByReference, int Waybills, bool Agrees)
{
    /// <summary>
    /// Whether this transfer answers to the report.
    /// </summary>
    /// <remarks>
    /// A single waybill is not enough on its own. The bank breaks a title across lines and the
    /// numbers are read back with the spaces removed, so adjacent waybills run together and an
    /// eleven-digit string turns up inside them by accident - a one-parcel FedEx report matched a
    /// transfer of 180 806,58 that had nothing to do with it, which is how this was found.
    ///
    /// So: the courier's own reference settles it; failing that, either two waybills, or one
    /// waybill on a transfer worth exactly what the report adds up to. That second case is what
    /// still lets a genuine divergence be found on a large report, without inventing one.
    /// </remarks>
    public bool Found => ByReference || Waybills >= 2 || (Waybills == 1 && Agrees);

    /// <summary>
    /// Which of two candidates answers better: the reference first, then a transfer whose amount
    /// agrees, then the one naming more parcels.
    /// </summary>
    public (int, int, int) Strength => (ByReference ? 1 : 0, Agrees ? 1 : 0, Waybills);

    /// <summary>How the match came about - it goes into the log and onto the report's row.</summary>
    public string Reason => ByReference
        ? "matched on the payment reference in the title"
        : $"matched on {Waybills} waybills in the title";
}
