using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Options;

namespace FSM.Api.Services;

public sealed class EmailSender(
    IOptions<SmtpSettings> options,
    ILogger<EmailSender> logger)
{
    private readonly SmtpSettings settings = options.Value;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(settings.Host) &&
        !string.IsNullOrWhiteSpace(settings.FromAddress);

    public async Task SendAsync(
        string recipient,
        string subject,
        string htmlBody,
        CancellationToken cancellationToken)
    {
        if (!IsConfigured)
            return;

        using var message = new MailMessage
        {
            From = new MailAddress(
                settings.FromAddress!,
                settings.FromName),
            Subject = subject,
            Body = htmlBody,
            IsBodyHtml = true
        };

        message.To.Add(recipient);

        using var client = new SmtpClient(
            settings.Host!,
            settings.Port)
        {
            EnableSsl = settings.UseSsl
        };

        if (!string.IsNullOrWhiteSpace(settings.Username))
        {
            client.Credentials = new NetworkCredential(
                settings.Username,
                settings.Password);
        }

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            await client.SendMailAsync(message);
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Failed to send FSM notification to {Recipient}.",
                recipient);
            throw;
        }
    }
}
