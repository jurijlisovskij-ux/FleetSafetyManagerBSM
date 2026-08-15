using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace FSM.Api.Services;

public sealed record RecoveryKeyPayload(Guid KeyId, string Secret);

public sealed class RecoveryKeyService(IDataProtectionProvider provider, IConfiguration configuration)
{
    private readonly IDataProtector protector = provider.CreateProtector("FSM.OfficeRecovery.v1");
    private readonly bool isOffice = (configuration["Application:Edition"] ?? "Office")
        .Equals("Office", StringComparison.OrdinalIgnoreCase);
    private readonly string directory = Path.GetFullPath(Path.Combine("Data", "Recovery"));
    public string KeyPath => Path.Combine(directory, "FSM-Recovery.fsmkey");
    private string MetadataPath => Path.Combine(directory, "active-key.dat");

    public bool EnsureCreated()
    {
        if (!isOffice || (File.Exists(KeyPath) && File.Exists(MetadataPath))) return false;
        Rotate();
        return true;
    }

    public byte[] Rotate()
    {
        if (!isOffice) throw new InvalidOperationException("Recovery keys are available only in Office Edition.");
        Directory.CreateDirectory(directory);
        var payload = new RecoveryKeyPayload(Guid.NewGuid(), Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        var protectedKey = protector.Protect(JsonSerializer.Serialize(payload));
        var metadata = protector.Protect(JsonSerializer.Serialize(new
        {
            payload.KeyId,
            SecretHash = Convert.ToHexString(SHA256.HashData(Convert.FromBase64String(payload.Secret)))
        }));
        File.WriteAllText(KeyPath, protectedKey);
        File.WriteAllText(MetadataPath, metadata);
        return File.ReadAllBytes(KeyPath);
    }

    public byte[] Export()
    {
        EnsureCreated();
        return File.ReadAllBytes(KeyPath);
    }

    public bool Validate(byte[] file)
    {
        if (!isOffice) return false;
        try
        {
            var payload = JsonSerializer.Deserialize<RecoveryKeyPayload>(
                protector.Unprotect(System.Text.Encoding.UTF8.GetString(file)))!;
            using var metadata = JsonDocument.Parse(protector.Unprotect(File.ReadAllText(MetadataPath)));
            var keyId = metadata.RootElement.GetProperty("KeyId").GetGuid();
            var expected = metadata.RootElement.GetProperty("SecretHash").GetString();
            var actual = Convert.ToHexString(SHA256.HashData(Convert.FromBase64String(payload.Secret)));
            return payload.KeyId == keyId && CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(actual), Convert.FromHexString(expected!));
        }
        catch { return false; }
    }
}
