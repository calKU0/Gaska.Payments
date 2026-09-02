using Gaska.Payments.Domain.Couriers;
using Gaska.Payments.Erp;
using Gaska.Payments.Integrations.Archive;
using Gaska.Payments.Integrations.Couriers;

namespace Gaska.Payments.Application.Couriers;

/// <summary>
/// Cash on delivery: the mailbox the couriers send their payout reports to, and what is done
/// with them.
/// </summary>
/// <remarks>
/// The money comes in as one collective transfer per report, but the accounting is done parcel by
/// parcel: every parcel is a customer's payment for one invoice, and that is what has to close the
/// open item. So one report becomes as many cash entries in the COD register as it has parcels,
/// exactly as the accountants have been entering them by hand.
/// </remarks>
public sealed class CodOptions
{
    public const string SectionName = "Cod";

    /// <summary>Whether the service reads the mailbox at all.</summary>
    public bool Enabled { get; set; }

    /// <summary>The ERP register the COD entries go to - the cash register, not a bank one.</summary>
    public string Register { get; set; } = "K_GLS";

    /// <summary>
    /// Code of the cash operation the entries are created with (<c>CDN.Operacje.KAO_Kod</c>).
    /// </summary>
    /// <remarks>
    /// It cannot be taken from the register the way bank operations take theirs: the COD register
    /// has no statement import configured (<c>KAR_ImpKAO*</c> are all zero), because nothing is
    /// ever imported into it - the entries are made by hand. All 167 030 receipts on it use the
    /// same operation, and that one is named here.
    /// </remarks>
    public string OperationSymbol { get; set; } = "KP_PL";

    /// <summary>
    /// The earliest payout date the service takes over. Reports older than this are archived but
    /// produce no entries - those days were entered by hand and are already in ERP.
    /// </summary>
    public DateTime PostFrom { get; set; } = DateTime.Today;

    public CodMailboxOptions Mailbox { get; set; } = new();

    /// <summary>Where to write when a report and its transfer do not agree.</summary>
    public CodNotificationOptions Notifications { get; set; } = new();

    /// <summary>
    /// How many days on either side of a report's declared payout date the matching transfer is
    /// looked for.
    /// </summary>
    /// <remarks>
    /// The declared date is not the day the money arrives - the GLS report of 2026-05-25 was paid
    /// on the 26th - and DPD sends its file days in advance, so the window has to be generous in
    /// both directions.
    /// </remarks>
    public int PayoutWindowDays { get; set; } = 30;

    /// <summary>
    /// The couriers, in the order their formats are tried. A report is recognised by the shape of
    /// its attachment rather than by who sent it, so a change of sending address does not stop
    /// the service; the addresses only narrow down which messages are looked at.
    /// </summary>
    public List<CourierOptions> Couriers { get; set; } = [];

    /// <summary>The courier configuration for a format, or null when it is not configured.</summary>
    public CourierOptions? Courier(string format) =>
        Couriers.FirstOrDefault(c => string.Equals(c.Format, format, StringComparison.OrdinalIgnoreCase));
}
