using System.Security.Cryptography;

namespace MechanicaLauncher.Core.Config;

public sealed class IntegrityResult
{
    public bool IsValid => Violations.Count == 0;
    public List<string> Violations { get; } = [];
}

public static class IntegrityChecker
{
    public static async Task<IntegrityResult> VerifyAsync(EventConfig config, string gameDir)
    {
        var result = new IntegrityResult();
        var modsDir = Path.Combine(gameDir, "mods");

        if (config.Integrity is not { Enabled: true }) return result;
        if (!Directory.Exists(modsDir)) return result;

        // Check required mods exist and have correct hash
        if (config.Mods?.Required != null)
        {
            foreach (var mod in config.Mods.Required)
            {
                var path = Path.Combine(modsDir, mod.Filename);
                if (!File.Exists(path))
                {
                    result.Violations.Add($"Missing required mod: {mod.Name} ({mod.Filename})");
                    continue;
                }

                if (!string.IsNullOrEmpty(mod.Sha256))
                {
                    var hash = await ComputeSha256Async(path);
                    if (!hash.Equals(mod.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        result.Violations.Add($"Modified mod: {mod.Name} (hash mismatch)");
                    }
                }
            }
        }

        // Check forbidden mods not present
        if (config.Mods?.Forbidden != null)
        {
            foreach (var file in Directory.GetFiles(modsDir, "*.jar"))
            {
                var name = Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
                foreach (var forbidden in config.Mods.Forbidden)
                {
                    if (name.Contains(forbidden, StringComparison.OrdinalIgnoreCase))
                    {
                        result.Violations.Add($"Forbidden mod detected: {Path.GetFileName(file)}");
                    }
                }
            }
        }

        // Strict mode: no extra jars allowed
        if (config.Integrity.Strict && config.Mods?.Required != null)
        {
            var allowed = new HashSet<string>(
                config.Mods.Required.Select(m => m.Filename.ToLowerInvariant()));
            if (config.Mods.Optional != null)
                foreach (var m in config.Mods.Optional)
                    allowed.Add(m.Filename.ToLowerInvariant());

            foreach (var file in Directory.GetFiles(modsDir, "*.jar"))
            {
                var name = Path.GetFileName(file).ToLowerInvariant();
                if (!allowed.Contains(name))
                {
                    result.Violations.Add($"Unauthorized mod: {Path.GetFileName(file)}");
                }
            }
        }

        return result;
    }

    private static async Task<string> ComputeSha256Async(string path)
    {
        using var sha = SHA256.Create();
        await using var stream = File.OpenRead(path);
        var hash = await sha.ComputeHashAsync(stream);
        return Convert.ToHexStringLower(hash);
    }
}
