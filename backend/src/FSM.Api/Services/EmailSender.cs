using System.Net;
using System.Net.Mail;

namespace FSM.Api.Services;

public sealed class EmailSender(
    EmailRecoverySettingsStore settingsStore,
    ILogger<EmailSender> logger)
{
    public bool IsConfigured { get { var settings = settingsStore.Get(); return
        !string.IsNullOrWhiteSpace(settings.Host) &&
        !string.IsNullOrWhiteSpace(settings.FromAddress) &&
        !string.IsNullOrWhiteSpace(settings.Password); } }

    public async Task SendAsync(
        string recipient,
        string subject,
        string htmlBody,
        CancellationToken cancellationToken)
    {
        var settings = settingsStore.Get();
        if (!IsConfigured) throw new InvalidOperationException("Administrator email recovery is not configured.");

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
