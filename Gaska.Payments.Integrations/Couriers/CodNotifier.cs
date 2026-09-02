using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;

using Gaska.Payments.Domain.Couriers;

namespace Gaska.Payments.Integrations.Couriers;

/// <summary>
/// Tells the accounting team when a courier report cannot be trusted.
/// </summary>
/// <remarks>
/// One thing only gets a message: the courier's transfer arrived, but it is not the amount the
/// report adds up to. Then nothing from that report is settled automatically, and somebody has to
/// look - a silent log entry would leave the money sitting there for weeks.
///
/// A report is written to once. The service comes round every hour and would otherwise send the
/// same message twenty-four times a day until somebody fixed it.
/// </remarks>
public sealed class CodNotifier(IOptions<CodNotificationOptions> options, ILogger<CodNotifier> logger)
{
    private readonly CodNotificationOptions _options = options.Value;

    /// <summary>
    /// Sends the message about one report. Returns false when nothing was sent, so the report is
    /// not marked as notified and the next pass tries again.
    /// </summary>
    public async Task<bool> ReportMismatchAsync(
        string courier, CodReport report, CodPayout payout, string file,
        CancellationToken cancellationToken = default)
    {
        if (!_options.IsConfigured)
        {
            logger.LogWarning(
                "The {Courier} report {Reference} does not agree with its transfer, but notifications " +
                "are not configured - nobody is being told.",
                courier, report.Reference);

            return false;
        }

        var difference = payout.Amount - report.ParcelTotal;

        var body =
            $"""
             Zestawienie pobrań od {courier} nie zgadza się z przelewem, więc nic z niego nie zostało
             rozliczone automatycznie. Zapisy w rejestrze pobrań powstały datą przelewu i czekają na
             ręczne rozliczenie.

             Kurier:              {courier}
             Referencja:          {Or(report.Reference, "brak")}
             Rachunek:            {Or(report.Account, "nie podany w pliku")}

             Suma pozycji:        {report.ParcelTotal,14:N2} PLN  ({report.Parcels.Count} paczek)
             Przelew:             {payout.Amount,14:N2} PLN  z dnia {payout.BookedOn:yyyy-MM-dd}
             Różnica:             {difference,14:N2} PLN

             Zapis przelewu:      {payout.EntryId} w rejestrze {payout.Register}
             Plik:                {Or(file, "nie zapisany w archiwum")}

             Wiadomość wysłana automatycznie przez Gaska.Payments.Service.
             """;

        var message = new MimeMessage
        {
            Subject = $"Pobrania {courier}: rozjazd {difference:N2} PLN "
                      + $"(zestawienie {Or(report.Reference, report.PayoutDate.ToString("yyyy-MM-dd"))})",
            Body = new TextPart("plain") { Text = body },
        };

        message.From.Add(MailboxAddress.Parse(_options.From));
        foreach (var recipient in _options.Recipients) message.To.Add(MailboxAddress.Parse(recipient));

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            using var client = new SmtpClient();

            await client.ConnectAsync(
                _options.Host, _options.Port,
                _options.UseSsl ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTlsWhenAvailable,
                cancellationToken);

            if (_options.User.Length > 0)
            {
                await client.AuthenticateAsync(_options.User, _options.Password, cancellationToken);
            }

            await client.SendAsync(message, cancellationToken);
            await client.DisconnectAsync(quit: true, cancellationToken);

            logger.LogInformation(
                "Told {To} about the divergence in the {Courier} report {Reference}, sent in {ElapsedMs} ms.",
                string.Join(", ", _options.Recipients), courier, report.Reference,
                stopwatch.ElapsedMilliseconds);

            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A mail server that will not answer must not cost us the pass. The report stays
            // un-notified and the next round tries again.
            logger.LogError(exception,
                "Could not send the notification about the {Courier} report {Reference} to {Host}:{Port}.",
                courier, report.Reference, _options.Host, _options.Port);

            return false;
        }
    }

    private static string Or(string value, string fallback) => value.Trim().Length > 0 ? value : fallback;
}
