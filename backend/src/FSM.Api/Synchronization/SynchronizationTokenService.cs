using System.Security.Cryptography;
using System.Text;
using FSM.Domain.Entities;
using FSM.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FSM.Api.Synchronization;

public sealed class SynchronizationTokenService
{
    public const string HeaderName = "X-FSM-Synchronization-Token";

    private readonly FsmDbContext db;
    private readonly SynchronizationOptions options;
    private readonly IDataProtector protector;

    public SynchronizationTokenService(
        FsmDbContext db,
        IOptions<SynchronizationOptions> options,
        IDataProtectionProvider protectionProvider)
    {
        this.db = db;
        this.options = options.Value;
        protector = protectionProvider.CreateProtector(
            "FleetSafetyManager.SynchronizationToken.v1");
    }

    public static string CreateToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    public async Task<string> RotateAsync(
        Guid vesselId,
        CancellationToken cancellationToken)
    {
        var setting = await GetOrCreateSettingAsync(
            vesselId,
            cancellationToken);
        var token = InitializeCredentials(setting);
        setting.LastModifiedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return token;
    }

    public string InitializeCredentials(SynchronizationSetting setting)
    {
        var token = CreateToken();
        setting.TokenHash = HashToken(token);
        setting.ProtectedToken = protector.Protect(token);
        setting.LastModifiedUtc = DateTime.UtcNow;
        return token;
    }

    public async Task<bool> ValidateAsync(
        Guid vesselId,
        string? candidate,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(candidate))
            return false;

        var hashes = new List<string>();
        var storedHash = await db.SynchronizationSettings
            .AsNoTracking()
            .Where(x => x.VesselId == vesselId)
            .Select(x => x.TokenHash)
            .SingleOrDefaultAsync(cancellationToken);

        if (!string.IsNullOrWhiteSpace(storedHash))
            hashes.Add(storedHash);
        var candidateBytes = Convert.FromHexString(HashToken(candidate.Trim()));

        return hashes.Any(hash =>
        {
            try
            {
                return CryptographicOperations.FixedTimeEquals(
                    candidateBytes,
                    Convert.FromHexString(hash));
            }
            catch (FormatException)
            {
                return false;
            }
        });
    }

    public string GetOutboundToken(SynchronizationSetting setting)
    {
        if (!string.IsNullOrWhiteSpace(setting.ProtectedToken))
            return protector.Unprotect(setting.ProtectedToken);
        if (!string.IsNullOrWhiteSpace(options.VesselToken))
            return options.VesselToken;

        throw new InvalidOperationException(
            "A synchronization token has not been configured.");
    }

    public void SetOutboundToken(
        SynchronizationSetting setting,
        string token)
    {
        var normalized = token.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            throw new InvalidDataException("The synchronization token is required.");
        setting.ProtectedToken = protector.Protect(normalized);
        setting.LastModifiedUtc = DateTime.UtcNow;
    }

    public string GetRemoteBaseUrl(
        SynchronizationSetting setting,
        string edition)
    {
        var configured = setting.RemoteBaseUrl;
        if (string.IsNullOrWhiteSpace(configured) &&
            edition.Equals("Ship", StringComparison.OrdinalIgnoreCase))
        {
            configured = options.OfficeBaseUrl;
        }

        if (!Uri.TryCreate(configured, UriKind.Absolute, out var uri) ||
            (!uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
             !(setting.AllowInsecureHttp &&
               uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))))
        {
            throw new InvalidOperationException(
                "The synchronization endpoint must use HTTPS unless local HTTP test mode is explicitly enabled.");
        }

        return uri.ToString().TrimEnd('/');
    }

    public async Task<SynchronizationSetting> GetOrCreateSettingAsync(
        Guid vesselId,
        CancellationToken cancellationToken)
    {
        var setting = await db.SynchronizationSettings
            .SingleOrDefaultAsync(
                x => x.VesselId == vesselId,
                cancellationToken);

        if (setting is not null)
            return setting;

        var vesselExists = await db.Vessels
            .AnyAsync(x => x.Id == vesselId, cancellationToken);
        if (!vesselExists)
            throw new KeyNotFoundException("Vessel not found.");

        setting = new SynchronizationSetting
        {
            VesselId = vesselId,
            SynchronizationIdentity = Guid.NewGuid(),
            AutomaticIntervalHours = NormalizeInterval(
                options.AutomaticIntervalHours)
        };
        db.SynchronizationSettings.Add(setting);
        await db.SaveChangesAsync(cancellationToken);
        return setting;
    }

    public static int NormalizeInterval(int value) =>
        value is 0 or 6 or 12 or 24 ? value : 24;

    public static string HashToken(string token) =>
        Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(token.Trim())));
}
