namespace Gaska.Payments.Application.Cards;

/// <summary>
/// The card terminal register: payments taken on our own terminals, booked from Fiserv's reports.
/// </summary>
public sealed class CardOptions
{
    public const string SectionName = "Cards";

    public bool Enabled { get; set; }

    /// <summary>The register the terminal payments are booked on.</summary>
    public string Register { get; set; } = "KARTA";

    /// <summary>A sale paid by card.</summary>
    public string SaleOperation { get; set; } = "KP-K";

    /// <summary>Money given back to a card.</summary>
    public string RefundOperation { get; set; } = "KW-K";

    /// <summary>Fiserv's commission.</summary>
    public string FeeOperation { get; set; } = "PROW";

    /// <summary>
    /// Fiserv's card in ERP - POLCARD, 11724 - which the commission is booked on, as it always has
    /// been.
    /// </summary>
    public int FeeContractorId { get; set; } = 11724;

    /// <summary>
    /// The first day the service books. Everything earlier on the register was entered by hand,
    /// and a report reaching back past it produces nothing for those days.
    /// </summary>
    public DateTime PostFrom { get; set; } = DateTime.Today;

    /// <summary>
    /// Who may send the report: an address, or a domain after the @. A report from anyone else
    /// is recorded and left alone.
    /// </summary>
    public List<string> Senders { get; set; } = [];

    /// <summary>
    /// How many days before the transaction the document it pays may have been issued. The
    /// half-automat looked a week back, and a receipt is issued at the till, so rarely any
    /// earlier than the payment.
    /// </summary>
    public int DocumentWindowDays { get; set; } = 7;

    /// <summary>
    /// The registers Fiserv's transfers arrive on. The commission is what a batch came to less
    /// what Fiserv sent for it, so the transfer has to be found before it can be booked.
    /// </summary>
    public List<string> PayoutRegisters { get; set; } = ["FORPL"];

    /// <summary>
    /// How far back the transfers are looked for, and how long a batch is waited on before its
    /// transfer is called missing.
    /// </summary>
    public int PayoutWindowDays { get; set; } = 14;

    /// <summary>
    /// The largest commission believed, as a share of the batch. Fiserv takes around two per cent;
    /// a difference far above that means the transfer was paired with the wrong batches, and
    /// nothing is booked.
    /// </summary>
    public decimal MaximumCommissionShare { get; set; } = 0.05m;

    /// <summary>Whether the report may be booked from this sender.</summary>
    /// <remarks>
    /// Stricter than the couriers' check, which accepts any sender containing the configured text
    /// and everyone when the list is empty: here the address has to be the one named, or end in
    /// the domain named, and an empty list lets nobody in. A report that books money onto customers'
    /// invoices is worth the few characters of extra care.
    /// </remarks>
    public bool Accepts(string sender)
    {
        var address = sender.Trim();

        // "Fiserv <raporty@polcard.com.pl>" as well as the bare address.
        var open = address.LastIndexOf('<');
        if (open >= 0) address = address[(open + 1)..];
        address = address.Trim().TrimEnd('>').Trim();

        return Senders.Any(allowed => allowed.Contains('@')
            ? address.Equals(allowed, StringComparison.OrdinalIgnoreCase)
            : address.EndsWith("@" + allowed, StringComparison.OrdinalIgnoreCase)
              || address.EndsWith("." + allowed, StringComparison.OrdinalIgnoreCase));
    }
}
