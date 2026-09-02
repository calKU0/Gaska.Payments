namespace Gaska.Payments.Desktop.Data;

/// <summary>
/// The ERP operator signed in through the Comarch window, the centre they signed in to, and the
/// registers that centre gives them.
/// </summary>
/// <remarks>
/// Rights to cash and bank registers hang on the centre, not on the operator: a centre owns a list
/// of registers in <c>CDN.FrmObiekty</c>, or takes its parent's list when <c>FRS_RejestryZRodzica</c>
/// says so. That is why the operator card leads nowhere here - <c>Ope_KaRNumer</c> is only the
/// register a document starts on, and no operator in this database has one.
/// </remarks>
/// <param name="Ident">The operator's ERP login, from <c>CDN.Sesje.SES_OpeIdent</c>.</param>
/// <param name="Name">Their surname off the operator card, empty when there is no card.</param>
/// <param name="CentreId">The centre they signed in to, 0 when ERP recorded none.</param>
/// <param name="CentreName">That centre's name, for the window header.</param>
/// <param name="Registers">
/// Which of the registers we handle that centre reaches. Meaningful only when
/// <see cref="IsRestricted"/> - otherwise nothing was established and everything is shown.
/// </param>
public sealed record OperatorAccess(
    string Ident,
    string Name,
    int CentreId,
    string CentreName,
    IReadOnlyList<string> Registers)
{
    /// <summary>Nobody identified - the application then shows every configured register.</summary>
    public static readonly OperatorAccess Unknown = new(string.Empty, string.Empty, 0, string.Empty, []);

    /// <summary>
    /// Whether we established a centre and can therefore narrow anything down. An operator whose
    /// centre reaches none of our registers is restricted with an empty list - that is an answer,
    /// not a failure, and it must not be confused with not having asked.
    /// </summary>
    public bool IsRestricted => CentreId != 0;

    /// <summary>Whether the operator's centre reaches the register.</summary>
    public bool MaySee(string series) =>
        !IsRestricted || Registers.Contains(series.Trim(), StringComparer.OrdinalIgnoreCase);

    /// <summary>How the operator is named in the window header.</summary>
    public string Label => (Ident, Name, CentreName) switch
    {
        ("", _, _) => "nieznany operator",
        (var ident, "", "") => ident,
        (var ident, "", var centre) => $"{ident} · {centre}",
        (var ident, var name, "") => $"{ident} ({name})",
        var (ident, name, centre) => $"{ident} ({name}) · {centre}",
    };
}
