namespace Gaska.Payments.Domain.Model;

/// <summary>
/// Where a document reference found in a payment title came from. The order matters - a higher
/// value means stronger evidence.
/// </summary>
public enum ReferenceStrength
{
    /// <summary>A bare run of digits with no context at all ("30752").</summary>
    BareNumber = 0,

    /// <summary>A number inferred from a list ("FS-17887,18222/26/SPR" - the second element).</summary>
    ListContinuation = 1,

    /// <summary>A number preceded by a generic keyword ("za FV 23622").</summary>
    InvoiceKeyword = 2,

    /// <summary>Document symbol plus number, without a year ("FS-25371").</summary>
    KindAndNumber = 3,

    /// <summary>Symbol, number and year ("FS-25371/26").</summary>
    KindNumberYear = 4,

    /// <summary>The full number including the series ("(S)FS-25371/26/SPR").</summary>
    FullNumber = 5,
}

/// <summary>
/// A single document reference extracted from a payment title.
/// </summary>
/// <param name="Kind">The recognised document kind.</param>
/// <param name="Number">Sequential number of the document within its series.</param>
/// <param name="Year">Two-digit year, when given.</param>
/// <param name="Series">Series (SPR, WDT, DET, ...), when given.</param>
/// <param name="Strength">How strong this piece of evidence is.</param>
/// <param name="RawText">The fragment of the title the reference was built from.</param>
/// <param name="DeclaredAmount">Amount the customer assigned to this document, e.g. "FS-18713/26/SPR (1 521,60 PLN)".</param>
/// <param name="IsExplicitCorrection">The customer marked it as a correction ("minus KOR. FSK-...").</param>
public sealed record DocumentReference(
    DocumentKind Kind,
    int Number,
    int? Year,
    string? Series,
    ReferenceStrength Strength,
    string RawText,
    decimal? DeclaredAmount = null,
    bool IsExplicitCorrection = false)
{
    public override string ToString()
    {
        var kind = Kind == DocumentKind.Unknown ? "?" : Kind.ToString().ToUpperInvariant();
        var year = Year is null ? string.Empty : $"/{Year:00}";
        var series = string.IsNullOrEmpty(Series) ? string.Empty : $"/{Series}";
        return $"{kind}-{Number}{year}{series}";
    }
}
