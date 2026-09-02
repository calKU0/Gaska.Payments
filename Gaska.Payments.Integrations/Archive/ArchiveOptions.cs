namespace Gaska.Payments.Integrations.Archive;

/// <summary>
/// Where the documents the service collects are filed: bank statements and courier payout reports.
/// </summary>
/// <remarks>
/// One root for both, because to the people who look into it this is one archive - what came in
/// today about money. The layout underneath is fixed rather than configurable, so that a path can
/// be worked out from what is known about an entry instead of being stored with it; that is how
/// the accounting application opens the statement behind a bank entry.
/// </remarks>
public sealed class ArchiveOptions
{
    public const string SectionName = "Archive";

    /// <summary>
    /// The root folder. A relative path is taken against the application's own directory, because
    /// a Windows service starts in System32.
    /// </summary>
    public string Directory { get; set; } = string.Empty;

    /// <summary>Whether an archive is configured at all.</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Directory);

    /// <summary>The root as an absolute path.</summary>
    public string Root => Path.IsPathRooted(Directory)
        ? Directory
        : Path.Combine(AppContext.BaseDirectory, Directory);
}

/// <summary>
/// The archive's layout. Both the service, which writes the files, and the accounting application,
/// which opens them, work it out with the same rules.
/// </summary>
public static class ArchivePaths
{
    /// <summary>Bank statements, one folder per register.</summary>
    public const string Statements = "Wyciągi";

    /// <summary>Courier payout reports, one folder per courier.</summary>
    public const string CodReports = "Pobrania";

    /// <summary>
    /// The folder of one bank account: register symbol and account number, as the accountants
    /// name it themselves.
    /// </summary>
    public static string StatementFolder(string root, string series, string account) =>
        Path.Combine(root, Statements, Clean($"{series} - {account}"));

    /// <summary>
    /// One statement. The month makes a level of its own so that no folder grows past a few
    /// hundred files, and the register and date are in the name so a given day is found without
    /// searching.
    /// </summary>
    public static string Statement(string root, string series, string account, DateTime day) =>
        Path.Combine(StatementFolder(root, series, account), day.ToString("yyyy-MM"),
            $"{Clean(series)}_{day:yyyy-MM-dd}.pdf");

    /// <summary>
    /// One courier report, under the courier's own folder and the month of the payout.
    /// </summary>
    /// <remarks>
    /// The payout date and the courier's own payment reference both go into the name, because the
    /// couriers reuse file names: every FedEx report is called <c>FedexPayOutReport.xls</c>, and
    /// nineteen of them arrived over five days in the mailbox as it stands. Without the reference
    /// the ones sharing a day overwrite one another - four files where there should have been
    /// nineteen, which is how this was found.
    /// </remarks>
    public static string CodReport(string root, string courier, DateTime payoutDate, string reference,
        string fileName)
    {
        var name = Clean(Path.GetFileNameWithoutExtension(fileName));
        var extension = Path.GetExtension(fileName);
        var stamp = reference.Trim().Length > 0
            ? $"{payoutDate:yyyy-MM-dd}_{Clean(reference)}"
            : payoutDate.ToString("yyyy-MM-dd");

        return Path.Combine(root, CodReports, Clean(courier), payoutDate.ToString("yyyy-MM"),
            $"{stamp}_{name}{extension}");
    }

    /// <summary>
    /// The part of a name that may go into a path. Register symbols and courier names come from
    /// configuration and from ERP, so they are not to be trusted with a separator in them.
    /// </summary>
    private static string Clean(string value)
    {
        var cleaned = value.Trim();

        foreach (var invalid in Path.GetInvalidFileNameChars()) cleaned = cleaned.Replace(invalid, '_');

        return cleaned.Length == 0 ? "_" : cleaned;
    }
}
