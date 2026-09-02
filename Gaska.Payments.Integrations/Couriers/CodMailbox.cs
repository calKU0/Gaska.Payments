using MailKit.Net.Pop3;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;

using Gaska.Payments.Domain.Couriers;

namespace Gaska.Payments.Integrations.Couriers;

/// <summary>One attachment from the mailbox.</summary>
public sealed record CodAttachment(string FileName, byte[] Content);

/// <summary>One message from the mailbox, with whatever it was carrying.</summary>
/// <param name="Uid">
/// The POP3 unique identifier. It is the only thing the protocol promises to keep stable between
/// sessions, so it is what we record as "already read".
/// </param>
public sealed record CodMessage(
    string Uid,
    DateTimeOffset Received,
    string Sender,
    string Subject,
    IReadOnlyList<CodAttachment> Attachments);

/// <summary>
/// Reads the courier reports out of the shared mailbox.
/// </summary>
/// <remarks>
/// POP3, because that is what the provider offers. It has no folders and no flags, so nothing on
/// the server records that we have seen a message - that is kept in <c>pay.CourierReport</c>,
/// keyed by UIDL. Messages are left on the server: the mailbox is the original of an accounting
/// document and people look into it.
///
/// The whole mailbox is listed but only the unseen messages are downloaded, and only up to the
/// configured limit. A mailbox nobody empties grows without bound, and downloading all of it on
/// every pass would eventually be the slowest thing the service does.
/// </remarks>
public sealed class CodMailbox(IOptions<CodMailboxOptions> options, ILogger<CodMailbox> logger)
{
    private readonly CodMailboxOptions _mailbox = options.Value;

    /// <summary>
    /// The messages that are not in <paramref name="alreadyRead"/>, newest first.
    /// </summary>
    public async Task<IReadOnlyList<CodMessage>> FetchAsync(
        IReadOnlySet<string> alreadyRead, CancellationToken cancellationToken = default)
    {
        var mailbox = _mailbox;

        if (string.IsNullOrWhiteSpace(mailbox.Host) || string.IsNullOrWhiteSpace(mailbox.User))
        {
            logger.LogWarning("The cash on delivery mailbox is not configured - skipping the mail.");
            return [];
        }

        using var client = new Pop3Client();

        var listing = System.Diagnostics.Stopwatch.StartNew();

        await client.ConnectAsync(
            mailbox.Host, mailbox.Port,
            mailbox.UseSsl ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTlsWhenAvailable,
            cancellationToken);

        await client.AuthenticateAsync(mailbox.User, mailbox.Password, cancellationToken);

        var uids = await client.GetMessageUidsAsync(cancellationToken);
        listing.Stop();

        // Newest first: an index in POP3 runs from the oldest message, so the tail of the list is
        // what arrived last, and that is what the service wants when the limit bites.
        var pending = Enumerable.Range(0, uids.Count)
            .Reverse()
            .Where(i => !alreadyRead.Contains(uids[i]))
            .Take(Math.Max(1, _mailbox.MaxMessagesPerRun))
            .ToList();

        logger.LogInformation(
            "Mailbox {User} on {Host}: {Total} messages, {Pending} not read yet, listed in {ElapsedMs} ms.",
            mailbox.User, mailbox.Host, uids.Count, pending.Count, listing.ElapsedMilliseconds);

        var messages = new List<CodMessage>();

        foreach (var index in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var download = System.Diagnostics.Stopwatch.StartNew();
                var message = await client.GetMessageAsync(index, cancellationToken);
                var converted = Convert(uids[index], message);
                messages.Add(converted);

                logger.LogDebug(
                    "Read message {Uid} from {Sender} in {ElapsedMs} ms: {Subject} ({Attachments} attachments).",
                    uids[index], converted.Sender, download.ElapsedMilliseconds,
                    converted.Subject, converted.Attachments.Count);

                if (mailbox.DeleteAfterDownload) await client.DeleteMessageAsync(index, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // One unreadable message must not cost us the rest of the mailbox. It is left
                // unrecorded on purpose, so the next pass tries it again.
                logger.LogWarning(exception, "Could not read message {Uid}.", uids[index]);
            }
        }

        await client.DisconnectAsync(quit: true, cancellationToken);

        return messages;
    }

    private static CodMessage Convert(string uid, MimeMessage message)
    {
        var attachments = new List<CodAttachment>();

        // Attachments and body parts both: FedEx sends its report as a body part rather than as a
        // declared attachment, and it is the same file either way.
        foreach (var part in message.BodyParts.OfType<MimePart>())
        {
            var name = part.FileName;
            if (string.IsNullOrWhiteSpace(name) || part.Content is null) continue;

            using var buffer = new MemoryStream();
            part.Content.DecodeTo(buffer);

            attachments.Add(new CodAttachment(name, buffer.ToArray()));
        }

        var sender = message.From.Mailboxes.FirstOrDefault()?.Address ?? string.Empty;

        return new CodMessage(uid, message.Date, sender, message.Subject ?? string.Empty, attachments);
    }
}
