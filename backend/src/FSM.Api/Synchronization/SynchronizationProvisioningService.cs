using System.Security.Cryptography;
using System.Text;
using FSM.Domain.Entities;
using FSM.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FSM.Api.Synchronization;

public sealed class SynchronizationProvisioningService(
    FsmDbContext db,
    SynchronizationTokenService tokens,
    IOptions<SynchronizationOptions> options,
    IConfiguration configuration)
{
    private const string ActivationAlphabet = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ";
    private readonly SynchronizationOptions options = options.Value;
    private readonly bool isOffice = string.Equals(
        configuration["Application:Edition"],
        "Office",
        StringComparison.OrdinalIgnoreCase);

    public (SynchronizationSetting Setting, string ActivationCode)
        ProvisionNewVessel(Vessel vessel)
    {
        var setting = new SynchronizationSetting
        {
            VesselId = vessel.Id,
            Vessel = vessel,
            SynchronizationIdentity = Guid.NewGuid(),
            Enabled = true,
            AllowInsecureHttp = options.AllowInsecureHttp,
            AutomaticIntervalHours = 24,
            LastStatus = "Warning",
            CurrentStage = "WaitingForConnection"
        };
        tokens.InitializeCredentials(setting);
        var activationCode = SetNewActivationCode(setting);
        db.SynchronizationSettings.Add(setting);
        return (setting, activationCode);
    }

    public async Task<string> RegenerateActivationCodeAsync(
        Guid vesselId,
        CancellationToken cancellationToken)
    {
        var setting = await tokens.GetOrCreateSettingAsync(
            vesselId,
            cancellationToken);
        EnsureOfficeCredentials(setting);
        var code = SetNewActivationCode(setting);
        await db.SaveChangesAsync(cancellationToken);
        return code;
    }

    public async Task EnsureExistingInstallationsAsync(
        CancellationToken cancellationToken = default)
    {
        var vessels = await db.Vessels
            .IgnoreQueryFilters()
            .Where(x => !x.IsDeleted)
            .ToListAsync(cancellationToken);
        var settings = await db.SynchronizationSettings
            .IgnoreQueryFilters()
            .ToDictionaryAsync(x => x.VesselId, cancellationToken);
        var changed = false;

        foreach (var vessel in vessels)
        {
            if (!settings.TryGetValue(vessel.Id, out var setting))
            {
                if (!isOffice && vessels.Count != 1)
                    continue;
                if (isOffice)
                {
                    ProvisionNewVessel(vessel);
                }
                else
                {
                    setting = new SynchronizationSetting
                    {
                        VesselId = vessel.Id,
                        SynchronizationIdentity = Guid.NewGuid(),
                        RemoteBaseUrl = string.IsNullOrWhiteSpace(options.OfficeBaseUrl)
                            ? null
                            : options.OfficeBaseUrl.TrimEnd('/'),
                        Enabled = !string.IsNullOrWhiteSpace(options.OfficeBaseUrl) &&
                            !string.IsNullOrWhiteSpace(options.VesselToken),
                        AllowInsecureHttp = options.AllowInsecureHttp,
                        AutomaticIntervalHours = 24,
                        CurrentStage = "WaitingForConnection"
                    };
                    if (!string.IsNullOrWhiteSpace(options.VesselToken))
                        tokens.SetOutboundToken(setting, options.VesselToken);
                    db.SynchronizationSettings.Add(setting);
                }
                changed = true;
                continue;
            }

            if (setting.SynchronizationIdentity == Guid.Empty)
            {
                setting.SynchronizationIdentity = Guid.NewGuid();
                changed = true;
            }

            if (isOffice)
            {
                changed |= EnsureOfficeCredentials(setting);
                if (!setting.Enabled)
                {
                    setting.Enabled = true;
                    changed = true;
                }
                if (options.AllowInsecureHttp && !setting.AllowInsecureHttp)
                {
                    setting.AllowInsecureHttp = true;
                    changed = true;
                }
            }
            if (setting.Enabled && setting.AutomaticIntervalHours == 0)
            {
                setting.AutomaticIntervalHours = 24;
                changed = true;
            }
            if (!isOffice)
            {
                if (string.IsNullOrWhiteSpace(setting.RemoteBaseUrl) &&
                    !string.IsNullOrWhiteSpace(options.OfficeBaseUrl))
                {
                    setting.RemoteBaseUrl = options.OfficeBaseUrl.TrimEnd('/');
                    changed = true;
                }
                if (string.IsNullOrWhiteSpace(setting.ProtectedToken) &&
                    !string.IsNullOrWhiteSpace(options.VesselToken))
                {
                    tokens.SetOutboundToken(setting, options.VesselToken);
                    changed = true;
                }
                if (!setting.Enabled &&
                    !string.IsNullOrWhiteSpace(setting.RemoteBaseUrl) &&
                    !string.IsNullOrWhiteSpace(setting.ProtectedToken))
                {
                    setting.Enabled = true;
                    changed = true;
                }
            }
        }

        if (changed)
            await db.SaveChangesAsync(cancellationToken);
    }

    private bool EnsureOfficeCredentials(SynchronizationSetting setting)
    {
        var changed = false;
        if (setting.SynchronizationIdentity == Guid.Empty)
        {
            setting.SynchronizationIdentity = Guid.NewGuid();
            changed = true;
        }
        if (string.IsNullOrWhiteSpace(setting.TokenHash) ||
            string.IsNullOrWhiteSpace(setting.ProtectedToken))
        {
            tokens.InitializeCredentials(setting);
            changed = true;
        }
        return changed;
    }

    private static string SetNewActivationCode(SynchronizationSetting setting)
    {
        Span<char> characters = stackalloc char[12];
        for (var index = 0; index < characters.Length; index++)
        {
            characters[index] = ActivationAlphabet[
                RandomNumberGenerator.GetInt32(ActivationAlphabet.Length)];
        }
        var compact = new string(characters);
        var code = $"FSM-{compact[..4]}-{compact[4..8]}-{compact[8..]}";
        setting.ActivationCodeHash = SynchronizationTokenService.HashToken(code);
        setting.ActivationCodeExpiresUtc = DateTime.UtcNow.AddDays(7);
        setting.ActivationCodeUsedUtc = null;
        setting.LastModifiedUtc = DateTime.UtcNow;
        return code;
    }

    public static bool IsActivationCodeValid(
        SynchronizationSetting setting,
        string? candidate,
        DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(candidate) ||
            string.IsNullOrWhiteSpace(setting.ActivationCodeHash) ||
            setting.ActivationCodeUsedUtc.HasValue ||
            setting.ActivationCodeExpiresUtc <= nowUtc)
        {
            return false;
        }

        try
        {
            var expected = Convert.FromHexString(setting.ActivationCodeHash);
            var actual = Convert.FromHexString(
                SynchronizationTokenService.HashToken(
                    NormalizeActivationCode(candidate)));
            return CryptographicOperations.FixedTimeEquals(expected, actual);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public static string NormalizeActivationCode(string value)
    {
        var compact = new string(value
            .Trim()
            .ToUpperInvariant()
            .Where(char.IsLetterOrDigit)
            .ToArray());
        if (compact.StartsWith("FSM", StringComparison.Ordinal))
            compact = compact[3..];
        return compact.Length == 12
            ? $"FSM-{compact[..4]}-{compact[4..8]}-{compact[8..]}"
            : value.Trim().ToUpperInvariant();
    }
}
