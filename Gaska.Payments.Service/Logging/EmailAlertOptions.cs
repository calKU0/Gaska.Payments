using Microsoft.Extensions.Configuration;
using Serilog.Events;

namespace Gaska.Payments.Service.Logging;

/// <summary>
/// Where the service writes when something goes wrong.
/// </summary>
/// <remarks>
/// This is the one sink that is not configured from the <c>Serilog:WriteTo</c> array like the
/// others. The e-mail sink batches, and the batching is the whole point of it: left at Serilog's
/// defaults it flushes every two seconds, so a run that fails a hundred times - which is what a
/// broken ERP connection looks like - would send a hundred messages in as many seconds. The
/// overload that accepts <c>BatchingOptions</c> cannot be reached from JSON, because the sink's
/// subject and body are <c>ITextFormatter</c>s rather than strings, so the sink is built in code
/// and only its data is read from here.
/// </remarks>
public sealed class EmailAlertOptions
{
    public const string SectionName = "SerilogEmail";

    /// <summary>The mailbox the alerts are sent from.</summary>
    public string From { get; set; } = string.Empty;

    /// <summary>Recipients, separated by semicolons or commas.</summary>
    public string To { get; set; } = string.Empty;

    public string MailServer { get; set; } = string.Empty;

    public int Port { get; set; } = 587;

    /// <summary>
    /// Whether the connection is encrypted. How it is encrypted follows from the port: 465 is
    /// wrapped in TLS from the first byte, everything else upgrades with STARTTLS.
    /// </summary>
    public bool EnableSsl { get; set; } = true;

    public string Username { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    /// <summary>
    /// The label the subject line opens with. The severity and the machine name are appended, so
    /// that a message can be triaged from the inbox without opening it.
    /// </summary>
    public string Subject { get; set; } = "Gaska.Payments.Service";

    /// <summary>How many events one message may carry.</summary>
    public int BatchPostingLimit { get; set; } = 100;

    /// <summary>How long events are collected before a message is sent.</summary>
    public int BatchPostingPeriodMinutes { get; set; } = 60;

    /// <summary>
    /// The lowest severity worth an e-mail. Warnings and above by default - anything lower belongs
    /// in the log file and in Seq, not in somebody's inbox.
    /// </summary>
    public string MinimumLevel { get; set; } = "Warning";

    /// <summary>Why the sink is switched off, or null when it is configured.</summary>
    public string? DisabledReason =>
        From.Length == 0 ? $"{SectionName}:From is empty"
        : Recipients.Count == 0 ? $"{SectionName}:To is empty"
        : MailServer.Length == 0 ? $"{SectionName}:MailServer is empty"
        : Port <= 0 ? $"{SectionName}:Port is {Port}"
        : null;

    public bool IsConfigured => DisabledReason is null;

    /// <summary>
    /// The recipients as a list. Written as one string because that is how the other services in
    /// the company keep theirs, and a shared shape is easier to copy than a better one.
    /// </summary>
    public IReadOnlyList<string> Recipients =>
        [.. To.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    public LogEventLevel Level =>
        Enum.TryParse<LogEventLevel>(MinimumLevel, ignoreCase: true, out var level)
            ? level
            : LogEventLevel.Warning;

    public static EmailAlertOptions Read(IConfiguration configuration) =>
        configuration.GetSection(SectionName).Get<EmailAlertOptions>() ?? new EmailAlertOptions();
}
