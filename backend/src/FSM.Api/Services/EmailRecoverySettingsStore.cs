using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

namespace FSM.Api.Services;

public sealed record EmailRecoverySettings(
    string Host,
    int Port,
    bool UseSsl,
    string Username,
    string Password,
    string FromAddress,
    string FromName);

public sealed class EmailRecoverySettingsStore
{
    private readonly string path = Path.Combine(AppContext.BaseDirectory, "Data", "email-recovery.json");
    private readonly IDataProtector protector;
    private readonly SmtpSettings defaults;
    private readonly object gate = new();

    public EmailRecoverySettingsStore(IDataProtectionProvider protection, IOptions<SmtpSettings> options)
    {
        protector = protection.CreateProtector("FSM.EmailRecovery.v1");
        defaults = options.Value;
    }

    public EmailRecoverySettings Get()
    {
        lock (gate)
        {
            if (File.Exists(path))
            {
                var saved = JsonSerializer.Deserialize<SavedSettings>(File.ReadAllText(path));
                if (saved is not null)
                    return new(saved.Host, saved.Port, saved.UseSsl, saved.Username,
                        protector.Unprotect(saved.ProtectedPassword), saved.FromAddress, saved.FromName);
            }

            return new(defaults.Host ?? "smtp.gmail.com", defaults.Port, defaults.UseSsl,
                defaults.Username ?? "", defaults.Password ?? "", defaults.FromAddress ?? "",
                defaults.FromName);
        }
    }

    public void Save(EmailRecoverySettings settings)
    {
        lock (gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var saved = new SavedSettings(settings.Host, settings.Port, settings.UseSsl,
                settings.Username, protector.Protect(settings.Password), settings.FromAddress,
                settings.FromName);
            File.WriteAllText(path, JsonSerializer.Serialize(saved, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    private sealed record SavedSettings(string Host, int Port, bool UseSsl, string Username,
        string ProtectedPassword, string FromAddress, string FromName);
}
