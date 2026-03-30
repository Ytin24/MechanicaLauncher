using System.Security.Cryptography;
using System.Text;

namespace MechanicaLauncher.Core.Config;

public static class EventTokenGenerator
{
    public static string Generate(EventConfig config, List<string> modHashes)
    {
        if (string.IsNullOrEmpty(config.Secret)) return "";

        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var payload = $"{config.Id}:{string.Join(",", modHashes)}:{timestamp}";

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(config.Secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
        var signature = Convert.ToHexStringLower(hash);

        return $"MECHANICA:{config.Id}:{timestamp}:{signature}";
    }

    public static bool Verify(string token, string secret, string expectedConfigId, int maxAgeSeconds = 300)
    {
        var parts = token.Split(':');
        if (parts.Length != 4 || parts[0] != "MECHANICA") return false;

        var configId = parts[1];
        var timestamp = parts[2];
        var signature = parts[3];

        if (configId != expectedConfigId) return false;

        if (long.TryParse(timestamp, out var ts))
        {
            var age = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - ts;
            if (age > maxAgeSeconds || age < -60) return false;
        }
        else return false;

        // We can't fully verify here without mod hashes, but the server plugin will
        // This method is for basic validation
        return !string.IsNullOrEmpty(signature) && signature.Length == 64;
    }
}
