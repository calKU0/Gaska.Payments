namespace Gaska.Payments.Domain.Model;

/// <summary>
/// Kind of document a customer may refer to in a payment title. The symbols match the
/// abbreviations in CDN.Obiekty (OB_Skrot) in Comarch ERP XL.
/// </summary>
public enum DocumentKind
{
    /// <summary>The kind could not be established - the title carried a bare number.</summary>
    Unknown = 0,

    /// <summary>The customer wrote something generic (FV, FA, "faktura"): an invoice, but which one is unknown.</summary>
    AnyInvoice,

    /// <summary>Sales invoice (2033).</summary>
    Fs,

    /// <summary>Export invoice / intra-community supply (2037).</summary>
    Fse,

    /// <summary>Sales invoice correction (2041).</summary>
    Fsk,

    /// <summary>Export invoice correction (2045).</summary>
    Fke,

    /// <summary>Receipt (2034).</summary>
    Pa,

    /// <summary>Receipt correction (2042).</summary>
    Pak,

    /// <summary>Invoice issued against a receipt (2035).</summary>
    Ra,

    /// <summary>Advance sales invoice (1824).</summary>
    Fsl,

    /// <summary>Goods issue note (2001) - creates no payment, but customers do quote it.</summary>
    Wz,

    /// <summary>Sales order (960) - same as above.</summary>
    Zs,

    /// <summary>Interest note (2832).</summary>
    No,
}

public static class DocumentKindExtensions
{
    /// <summary>Whether the kind is a correction, that is whether it reduces the receivable balance.</summary>
    public static bool IsCorrection(this DocumentKind kind) =>
        kind is DocumentKind.Fsk or DocumentKind.Fke or DocumentKind.Pak;

    /// <summary>
    /// Whether two kinds may denote the same document. <see cref="DocumentKind.Unknown"/>
    /// matches anything, <see cref="DocumentKind.AnyInvoice"/> matches any invoice.
    /// </summary>
    public static bool IsCompatibleWith(this DocumentKind a, DocumentKind b)
    {
        if (a == b) return true;
        if (a == DocumentKind.Unknown || b == DocumentKind.Unknown) return true;

        if (a == DocumentKind.AnyInvoice) return IsInvoiceLike(b);
        if (b == DocumentKind.AnyInvoice) return IsInvoiceLike(a);

        return false;
    }

    private static bool IsInvoiceLike(DocumentKind kind) => kind is
        DocumentKind.Fs or DocumentKind.Fse or DocumentKind.Fsk or DocumentKind.Fke or
        DocumentKind.Ra or DocumentKind.Fsl or DocumentKind.Pa or DocumentKind.Pak or
        DocumentKind.AnyInvoice;

    public static DocumentKind FromErpSymbol(string? symbol) => (symbol ?? string.Empty).ToUpperInvariant() switch
    {
        "FS" => DocumentKind.Fs,
        "FSE" => DocumentKind.Fse,
        "FSK" => DocumentKind.Fsk,
        "FKE" => DocumentKind.Fke,
        "PA" => DocumentKind.Pa,
        "PAK" => DocumentKind.Pak,
        "RA" => DocumentKind.Ra,
        "FSL" => DocumentKind.Fsl,
        "WZ" or "WZE" => DocumentKind.Wz,
        "ZAM" or "ZS" => DocumentKind.Zs,
        "NO" => DocumentKind.No,
        _ => DocumentKind.Unknown,
    };
}
