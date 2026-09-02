namespace Gaska.Payments.Domain.Couriers;

/// <summary>
/// The text a cash on delivery entry carries, in the form the accountants have always written it.
/// </summary>
/// <remarks>
/// All 167 030 entries made by hand on the COD register say <c>Nr wys:6232305690806 Kurier: Fedex</c>,
/// and the service writes the same thing. That is not cosmetic: the waybill inside this text is
/// what tells us a parcel has already been posted, whoever posted it, so the service and the people
/// have to agree on the wording down to the prefix.
/// </remarks>
public static class CodDescription
{
    private const string WaybillPrefix = "Nr wys:";
    private const string CourierPrefix = "Kurier:";

    /// <summary>The entry's text for one parcel.</summary>
    public static string For(string waybill, string courier) =>
        $"{WaybillPrefix}{waybill} {CourierPrefix} {courier}";

    /// <summary>
    /// The waybill out of such a text, or an empty string when the text is not one of ours.
    /// </summary>
    /// <remarks>
    /// The entries made by hand are not uniformly spaced - some say <c>Nr wys: 123</c> - so the
    /// number is taken as everything up to the next space rather than at a fixed offset.
    /// </remarks>
    public static string WaybillOf(string? description)
    {
        if (string.IsNullOrWhiteSpace(description)) return string.Empty;

        var start = description.IndexOf(WaybillPrefix, StringComparison.OrdinalIgnoreCase);
        if (start < 0) return string.Empty;

        var rest = description[(start + WaybillPrefix.Length)..].TrimStart();
        var end = rest.IndexOf(' ');

        return (end < 0 ? rest : rest[..end]).Trim();
    }
}
