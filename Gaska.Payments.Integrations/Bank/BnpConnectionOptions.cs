namespace Gaska.Payments.Integrations.Bank;

/// <summary>
/// A company account we download the operation history for.
/// </summary>
/// <remarks>
/// Account numbers are not held in configuration - they come from the ERP bank registers named in
/// <c>Settlement:Registers</c> (<c>CDN.Rejestry.KAR_NrRachunku</c>). One source of truth: the
/// register the entries go to and the account the statement comes from cannot drift apart,
/// because they are the same row in ERP.
/// </remarks>
public sealed record BnpAccount(string Iban, string Currency);

/// <summary>A bank statement returned as a PDF: the bank's own file name and its content.</summary>
public sealed record BankStatementPdf(string Name, byte[] Content);

public sealed class BnpConnectionOptions
{
    public const string SectionName = "Bnp";

    /// <summary>Endpoint of the CDC service (GOconnect Biznes).</summary>
    public string Endpoint { get; set; } = "https://connect.bnpparibas.pl/bnpp-cdc/cdc00101";

    /// <summary>Path to the communication certificate (PKCS#12).</summary>
    public string CertificatePath { get; set; } = string.Empty;

    public string CertificatePassword { get; set; } = string.Empty;

    /// <summary>
    /// Length of a single history request. The bank can reject a range that is too long, so
    /// longer periods are split into chunks.
    /// </summary>
    public int MaxDaysPerRequest { get; set; } = 7;

    /// <summary>
    /// How many days back the archive looks for statements it does not hold yet.
    /// </summary>
    /// <remarks>
    /// In the steady state exactly one day is fetched, yesterday's; the window exists so that a
    /// gap left by an outage fills itself in. It is deliberately much shorter than
    /// <c>Settlement:LookbackDays</c>: a missing day inside it is asked about on every pass, and
    /// weekends never produce a statement at all.
    /// </remarks>
    public int StatementBackfillDays { get; set; } = 7;
}
