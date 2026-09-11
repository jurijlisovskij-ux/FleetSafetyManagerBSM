using System.Security.Cryptography;
using System.Text;

namespace FSM.Api.Services;

public sealed class AdminPasswordRecoveryService
{
    private readonly object gate = new();
    private string? codeHash;
    private string? email;
    private DateTime expiresUtc;
    private int failedAttempts;

    public (string Code, DateTime ExpiresUtc) Issue(string recipient)
    {
        var code = RandomNumberGenerator.GetInt32(0, 1_000_000)
            .ToString("D6");

        lock (gate)
        {
            email = Normalize(recipient);
            codeHash = Hash(code);
            expiresUtc = DateTime.UtcNow.AddMinutes(10);
            failedAttempts = 0;
            return (code, expiresUtc);
        }
    }

    public bool TryConsume(string recipient, string code)
    {
        lock (gate)
        {
            if (codeHash is null ||
                expiresUtc <= DateTime.UtcNow ||
                failedAttempts >= 5 ||
                !string.Equals(email, Normalize(recipient),
                    StringComparison.Ordinal))
            {
                Clear();
                return false;
            }

            var suppliedHash = Hash(code.Trim());
            var valid = CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(codeHash),
                Convert.FromHexString(suppliedHash));

            if (!valid)
            {
                failedAttempts++;
                if (failedAttempts >= 5) Clear();
                return false;
            }

            Clear();
            return true;
        }
    }

    private void Clear()
    {
        codeHash = null;
        email = null;
        expiresUtc = default;
        failedAttempts = 0;
    }

    private static string Normalize(string value) =>
        value.Trim().ToLowerInvariant();

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
