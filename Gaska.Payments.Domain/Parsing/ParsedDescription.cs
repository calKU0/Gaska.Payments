using Gaska.Payments.Domain.Model;

namespace Gaska.Payments.Domain.Parsing;

/// <summary>The result of taking a payment title apart.</summary>
public sealed class ParsedDescription
{
    public required string Original { get; init; }

    /// <summary>The text after normalisation and space collapsing - what the regexes work on.</summary>
    public required string Compact { get; init; }

    public IReadOnlyList<DocumentReference> References { get; init; } = [];

    /// <summary>Tax id from the /IDC/ field of a split payment message.</summary>
    public string? SplitPaymentNip { get; init; }

    /// <summary>VAT amount from the /VAT/ field of a split payment message.</summary>
    public decimal? SplitPaymentVat { get; init; }

    /// <summary>Contents of the /INV/ field of a split payment message.</summary>
    public string? SplitPaymentInvoiceField { get; init; }

    /// <summary>KSeF numbers found in the title (ERP does not keep them here, but titles do carry them).</summary>
    public IReadOnlyList<string> KsefNumbers { get; init; } = [];

    /// <summary>True when the title mentions an invoice generically, with no number attached.</summary>
    public bool MentionsInvoiceWithoutNumber { get; init; }
}
