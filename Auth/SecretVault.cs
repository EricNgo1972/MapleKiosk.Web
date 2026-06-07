using System.Text;

namespace SPC.Infrastructure.Auth;

// Tenant-side companion to MapleKiosk.AuthHost.SecretVault. Same XOR key, so
// cipher bytes can be copied between the two vaults and both will decode to the
// identical plaintext.
//
// The embedded fallback is only an operational backstop. It is not secure
// against reverse engineering and should be treated as a recoverable secret —
// prefer setting Authentication:TokenSigningKey via user secrets / KeyVault /
// environment variables in production. Config wins; the vault is the fallback.
public static class SecretVault
{
    private static readonly byte[] s_key = [0x4D, 0x61, 0x70, 0x6C, 0x65, 0x4B, 0x69, 0x6F, 0x73, 0x6B];

    // Replace with the output of ObfuscateForEmbedding(...) before shipping a
    // fallback secret. Must hold the SAME plaintext as the auth host's
    // SecretVault entry for "Authentication:TokenSigningKey".
    private static readonly IReadOnlyDictionary<string, byte[]> s_fallbackSecrets =
        new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["Authentication:TokenSigningKey"] = [0x20, 0x21, 0x00, 0x00, 0x00, 0x20, 0x00, 0x5F, 0x00, 0x00, 0x60, 0x00, 0x05, 0x18, 0x0D, 0x66, 0x1A, 0x06, 0x14, 0x05, 0x24, 0x0F, 0x17, 0x41, 0x0E, 0x2E, 0x10, 0x42, 0x41, 0x5B, 0x7F, 0x57, 0x5D, 0x1C, 0x17, 0x24, 0x0D, 0x42, 0x05, 0x5A],
        };

    public static bool TryResolve(string configPath, out string secret)
    {
        secret = "";

        if (!s_fallbackSecrets.TryGetValue(configPath, out var cipher))
            return false;

        var decoded = Decode(cipher);
        if (string.IsNullOrWhiteSpace(decoded))
            return false;

        secret = decoded;
        return true;
    }

    public static string ObfuscateForEmbedding(string secret)
    {
        ArgumentNullException.ThrowIfNull(secret);

        var plain = Encoding.UTF8.GetBytes(secret);
        var sb = new StringBuilder("[");
        for (int i = 0; i < plain.Length; i++)
        {
            if (i > 0)
                sb.Append(", ");
            byte b = (byte)(plain[i] ^ s_key[i % s_key.Length]);
            sb.Append("0x").Append(b.ToString("X2"));
        }
        sb.Append(']');
        return sb.ToString();
    }

    private static string? Decode(byte[] cipher)
    {
        if (cipher.Length == 0)
            return null;

        var plain = new byte[cipher.Length];
        for (int i = 0; i < cipher.Length; i++)
            plain[i] = (byte)(cipher[i] ^ s_key[i % s_key.Length]);

        var secret = Encoding.UTF8.GetString(plain).Trim();
        return string.IsNullOrWhiteSpace(secret) ? null : secret;
    }
}
