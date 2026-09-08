using System.Net;
using MailKit.Security;
using Serilog;
using Serilog.Configuration;
using Serilog.Debugging;
using Serilog.Sinks.Email;

namespace Gaska.Payments.Service.Logging;

/// <summary>
/// The parts of the logging configuration that cannot be expressed in <c>appsettings.json</c>.
/// </summary>
public static class LoggingSetup
{
    /// <summary>
    /// Sends warnings and errors on by e-mail, in batches.
    /// </summary>
    /// <remarks>
    /// A sink that cannot deliver must not be able to stop the service, so nothing here throws: a
    /// section that is not filled in leaves the sink out, and the caller says so in the log once
    /// the logger exists. Delivery failures afterwards are Serilog's own business and surface in
    /// the self-log rather than in the service - see <see cref="RedirectSelfLog"/>.
    /// </remarks>
    public static LoggerConfiguration WithEmailAlerts(
        this LoggerConfiguration configuration, EmailAlertOptions options)
    {
        // Nothing to write to. Returning the configuration untouched keeps the call site a
        // straight line - the caller reports the reason rather than branching around it.
        if (!options.IsConfigured) return configuration;

        var email = new EmailSinkOptions
        {
            From = options.From,
            To = [.. options.Recipients],
            Host = options.MailServer,
            Port = options.Port,
            ConnectionSecurity = Security(options),
            Subject = new AlertSubjectFormatter(options.Subject),
            Body = new AlertBodyFormatter(),
            IsBodyHtml = false,
        };

        if (options.Username.Length > 0)
        {
            email.Credentials = new NetworkCredential(options.Username, options.Password);
        }

        var batching = new BatchingOptions
        {
            BatchSizeLimit = Math.Max(1, options.BatchPostingLimit),
            BufferingTimeLimit = TimeSpan.FromMinutes(Math.Max(1, options.BatchPostingPeriodMinutes)),

            // The first problem after a quiet spell is sent at once instead of waiting out the
            // whole period; everything after it is collected. Without this an error at five past
            // the hour would not be read until an hour later, which defeats the point of alerting.
            EagerlyEmitFirstEvent = true,

            // A backlog of alert mail is worth less the older it gets, and an hour of a failing
            // run can produce thousands of events. Past this many the sink drops them - the file
            // and Seq still hold every one, so nothing is actually lost.
            QueueLimit = 1000,
        };

        return configuration.WriteTo.Email(email, batching, options.Level);
    }

    /// <summary>
    /// How the connection to the mail server is encrypted.
    /// </summary>
    /// <remarks>
    /// Configuration says only whether encryption is on, which is the way every other service in
    /// the company writes it. The manner follows from the port, as it does everywhere else: 465 is
    /// wrapped in TLS from the first byte, 587 and the rest negotiate STARTTLS. Left to
    /// <see cref="SecureSocketOptions.Auto"/> MailKit would work it out too, but then a server
    /// that fails to advertise STARTTLS would quietly send the password in the clear.
    /// </remarks>
    private static SecureSocketOptions Security(EmailAlertOptions options) =>
        !options.EnableSsl ? SecureSocketOptions.None
        : options.Port == 465 ? SecureSocketOptions.SslOnConnect
        : SecureSocketOptions.StartTls;

    /// <summary>
    /// Writes Serilog's own complaints to a file.
    /// </summary>
    /// <remarks>
    /// Sinks swallow their exceptions on purpose - logging must never take down what it is
    /// logging - which means a mail server refusing the password, or Seq being unreachable, is
    /// invisible by default. This is the only place those are reported, and it is worth the one
    /// file: without it the first sign of a broken alert path is nobody being alerted.
    /// </remarks>
    public static void RedirectSelfLog(string path)
    {
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);

        var gate = new Lock();

        SelfLog.Enable(message =>
        {
            try
            {
                lock (gate)
                {
                    File.AppendAllText(full, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
                }
            }
            catch
            {
                // The self-log is the last line - there is nowhere left to report that it failed.
            }
        });
    }
}
