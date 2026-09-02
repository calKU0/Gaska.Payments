namespace Gaska.Payments.Domain.Couriers;

/// <summary>The POP3 mailbox the couriers write to.</summary>
/// <remarks>
/// POP3 rather than IMAP because that is what the provider gives us. It has no flags, so what has
/// already been read is remembered on our side by the message's UIDL - the one identifier POP3
/// guarantees to be stable between sessions.
/// </remarks>
public sealed class CodMailboxOptions
{
    public string Host { get; set; } = string.Empty;

    public int Port { get; set; } = 995;

    public bool UseSsl { get; set; } = true;

    public string User { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    /// <summary>How many messages to read in one pass, newest first.</summary>
    public int MaxMessagesPerRun { get; set; } = 100;

    /// <summary>
    /// Whether a message is deleted from the server once it has been read. Left off: the mailbox
    /// is the original of an accounting document, and people look into it.
    /// </summary>
    public bool DeleteAfterDownload { get; set; }
}

/// <summary>The mailbox the service writes from, and who hears about a problem.</summary>
public sealed class CodNotificationOptions
{
    public string Host { get; set; } = string.Empty;

    public int Port { get; set; } = 465;

    public bool UseSsl { get; set; } = true;

    /// <summary>The account to send as. Empty skips authentication.</summary>
    public string User { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    /// <summary>The address the message comes from.</summary>
    public string From { get; set; } = string.Empty;

    /// <summary>Who is told. Empty switches notifications off.</summary>
    public List<string> Recipients { get; set; } = [];

    public bool IsConfigured =>
        Host.Trim().Length > 0 && From.Trim().Length > 0 && Recipients.Count > 0;
}

/// <summary>One courier: how its report is read and where its files are filed.</summary>
public sealed class CourierOptions
{
    /// <summary>The courier's name - the folder in the archive and the text on the entry.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Which reader handles the attachment: <c>GlsCsv</c>, <c>DpdXls</c> or <c>FedexReport</c>.
    /// </summary>
    public string Format { get; set; } = string.Empty;

    /// <summary>
    /// Addresses the reports come from. Empty means any sender, which is what GLS has until it
    /// starts sending to this mailbox - until then the format alone identifies its reports.
    /// </summary>
    public List<string> Senders { get; set; } = [];

    /// <summary>
    /// The accounts of ours the courier pays into. A report naming any other account is not our
    /// money and produces no entries.
    /// </summary>
    /// <remarks>
    /// This is what tells FedEx's dropshipping apart: there the customer is paid directly and the
    /// report names the customer's own account, so it looks exactly like ours but describes money
    /// we never receive. Fifteen of the nineteen reports in the mailbox are of that kind.
    ///
    /// An empty list accepts every account, which is right for GLS and DPD - their files name no
    /// account at all.
    /// </remarks>
    public List<string> Accounts { get; set; } = [];

    /// <summary>Whether the message is from this courier. An empty sender list accepts everyone.</summary>
    public bool Accepts(string sender) =>
        Senders.Count == 0 ||
        Senders.Any(s => sender.Contains(s.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether the payout goes to one of our accounts.</summary>
    public bool AcceptsAccount(string account) =>
        Accounts.Count == 0 ||
        Accounts.Any(a => Compact(a) == Compact(account));

    /// <summary>Account numbers compared without spaces - the reports space them out variously.</summary>
    private static string Compact(string account) =>
        new([.. account.Where(char.IsAsciiLetterOrDigit)]);
}
