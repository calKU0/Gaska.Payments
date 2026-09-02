using Gaska.Payments.Domain.Couriers;
using Gaska.Payments.Erp;
using Gaska.Payments.Integrations.Archive;
using Gaska.Payments.Integrations.Couriers;

namespace Gaska.Payments.Application.Couriers;

/// <summary>How far a courier report has got.</summary>
public static class CodReportStatus
{
    /// <summary>Read and archived, waiting for the courier's transfer to arrive.</summary>
    public const string Pending = "Pending";

    /// <summary>The transfer arrived and agreed with the report; the parcels are in the register.</summary>
    public const string Posted = "Posted";

    /// <summary>The transfer arrived but the sums differ. Entries exist, nothing is settled.</summary>
    public const string Mismatch = "Mismatch";

    /// <summary>Not ours to post: dropshipping, a day before the takeover, a stray attachment.</summary>
    public const string Ignored = "Ignored";
}

/// <summary>One row of <c>pay.CourierReport</c> - what became of one attachment.</summary>
/// <remarks>
/// A report is kept even when it produces nothing, because that is also what stops it being read
/// again: POP3 has no flags, so this table is the only record that a message has been dealt with.
/// </remarks>
public sealed record CodReportRow(
    string Uid,
    string FileName,
    DateTimeOffset ReceivedAt,
    string Sender,
    string Subject,
    string Courier,
    string Format,
    string FilePath,
    string Status)
{
    /// <summary>The payout date printed in the report, which is not the day the money arrives.</summary>
    public DateTime? PayoutDate { get; init; }

    /// <summary>The transfer the report declares.</summary>
    public decimal? PayoutTotal { get; init; }

    public int ParcelCount { get; init; }

    /// <summary>The ERP cash entry of the courier's transfer, once it has been found.</summary>
    public int? PayoutEntryId { get; init; }

    /// <summary>The day that transfer was booked - the date every entry from this report carries.</summary>
    public DateTime? PayoutBookedOn { get; init; }

    /// <summary>What the transfer was actually worth, which is the thing checked against the file.</summary>
    public decimal? PayoutAmount { get; init; }

    public string? Note { get; init; }

    /// <summary>Whether the accounting team has already been written to about this report.</summary>
    public bool Notified { get; init; }

    public static CodReportRow For(CodMessage message, CodAttachment attachment, string status) =>
        new(message.Uid, attachment.FileName, message.Received, message.Sender, message.Subject,
            string.Empty, string.Empty, string.Empty, status);
}
