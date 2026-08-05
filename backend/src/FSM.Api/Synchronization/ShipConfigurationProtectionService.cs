using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FSM.Api.Synchronization;

public sealed class ShipConfigurationProtectionService
{
    private const int Iterations = 210_000;

    public string Protect(
        ShipSynchronizationConfiguration configuration,
        string passphrase)
    {
        ValidatePassphrase(passphrase);
        var salt = RandomNumberGenerator.GetBytes(16);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(configuration);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];
        using var key = new Rfc2898DeriveBytes(
            passphrase,
            salt,
            Iterations,
            HashAlgorithmName.SHA256);
        using var aes = new AesGcm(key.GetBytes(32), tag.Length);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);
        return JsonSerializer.Serialize(new ProtectedShipConfiguration(
            1,
            Iterations,
            Convert.ToBase64String(salt),
            Convert.ToBase64String(nonce),
            Convert.ToBase64String(ciphertext),
            Convert.ToBase64String(tag)));
    }

    public ShipSynchronizationConfiguration Unprotect(
        string protectedConfiguration,
        string passphrase)
    {
        ValidatePassphrase(passphrase);
        var envelope = JsonSerializer.Deserialize<ProtectedShipConfiguration>(
            protectedConfiguration,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("The Ship configuration file is empty.");
        if (envelope.Version != 1 || envelope.Iterations < 100_000)
            throw new InvalidDataException("The Ship configuration file format is not supported.");
        try
        {
            var salt = Convert.FromBase64String(envelope.Salt);
            var nonce = Convert.FromBase64String(envelope.Nonce);
            var ciphertext = Convert.FromBase64String(envelope.Ciphertext);
            var tag = Convert.FromBase64String(envelope.Tag);
            var plaintext = new byte[ciphertext.Length];
            using var key = new Rfc2898DeriveBytes(
                passphrase,
                salt,
                envelope.Iterations,
                HashAlgorithmName.SHA256);
            using var aes = new AesGcm(key.GetBytes(32), tag.Length);
            aes.Decrypt(nonce, ciphertext, tag, plaintext);
            return JsonSerializer.Deserialize<ShipSynchronizationConfiguration>(
                plaintext,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidDataException("The Ship configuration payload is empty.");
        }
        catch (Exception exception) when (
            exception is FormatException or CryptographicException or JsonException)
        {
            throw new InvalidDataException(
                "The Ship configuration file or passphrase is invalid.");
        }
    }

    private static void ValidatePassphrase(string passphrase)
    {
        if (string.IsNullOrWhiteSpace(passphrase) || passphrase.Length < 12)
            throw new InvalidDataException("Use a configuration passphrase of at least 12 characters.");
    }

    private sealed record ProtectedShipConfiguration(
        int Version,
        int Iterations,
        string Salt,
        string Nonce,
        string Ciphertext,
        string Tag);
}
