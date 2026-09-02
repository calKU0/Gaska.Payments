namespace Gaska.Payments.Domain.Model;

/// <summary>
/// An open payment of a trade document from ERP (CDN.TraPlat + CDN.TraNag).
/// </summary>
public sealed class OpenReceivable
{
    /// <summary>The payment's key in ERP: GIDTyp/GIDNumer/GIDLp.</summary>
    public required int PaymentDocType { get; init; }
    public required int PaymentDocId { get; init; }
    public required int PaymentLp { get; init; }

    public required DocumentKind Kind { get; init; }

    /// <summary>The full document number as ERP displays it, e.g. "(S)FS-29838/26/SPR".</summary>
    public required string DocumentNumber { get; init; }

    public required int Number { get; init; }
    public required int Year { get; init; }
    public string Series { get; init; } = string.Empty;

    public required int ContractorId { get; init; }
    public string ContractorAcronym { get; init; } = string.Empty;
    public string ContractorName { get; init; } = string.Empty;
    public string ContractorNip { get; init; } = string.Empty;

    /// <summary>Document amount in the payment currency.</summary>
    public required decimal Amount { get; init; }

    /// <summary>Amount still open for settlement (positive).</summary>
    public required decimal Remaining { get; init; }

    public required string Currency { get; init; }

    public required DateTime DueDate { get; init; }

    public DateTime IssueDate { get; init; }

    /// <summary>True for corrections - they enter the payment balance with a negative sign.</summary>
    public bool IsCorrection { get; init; }

    /// <summary>Signed amount: a receivable is positive, a correction negative.</summary>
    public decimal SignedRemaining => IsCorrection ? -Remaining : Remaining;

    /// <summary>Numbers of related documents (goods issues, orders) customers also quote.</summary>
    public IReadOnlyList<DocumentReference> RelatedNumbers { get; init; } = [];

    /// <summary>The invoice's KSeF number (CDN.KSeFDokumenty.KSF_Numer), when it was sent to KSeF.</summary>
    public string KsefNumber { get; init; } = string.Empty;

    /// <summary>
    /// The document number at the contractor's end (<c>CDN.TraNag.TrN_DokumentObcy</c>) - for
    /// liabilities this is the number of the invoice the supplier issued to us, and the very
    /// thing our outgoing transfer quotes.
    /// </summary>
    public string ForeignNumber { get; init; } = string.Empty;

    /// <summary>
    /// The order reference handed to the bank when the payment was sent
    /// (<c>CDN.TraPlat.TrP_EndToEndId</c>). The bank returns it in the statement, so it ties a
    /// transfer to one specific document payment with no guessing.
    /// </summary>
    public string BankReference { get; init; } = string.Empty;

    public override string ToString() => $"{DocumentNumber} {Remaining:N2} {Currency}";
}
