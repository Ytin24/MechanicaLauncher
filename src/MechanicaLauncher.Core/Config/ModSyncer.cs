using System.Security.Cryptography;

namespace MechanicaLauncher.Core.Config;

public sealed class ModSyncer
{
    private static readonly HttpClient Http = new();

    public event Action<string, double>? ProgressChanged;

    public async Task SyncAsync(EventConfig config, string modsDir)
    {
        Directory.CreateDirectory(modsDir);

        if (config.Mods?.Forbidden != null)
        {
            foreach (var file in Directory.GetFiles(modsDir, "*.jar"))
            {
                var name = Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
                foreach (var forbidden in config.Mods.Forbidden)
                {
                    if (name.Contains(forbidden, StringComparison.OrdinalIgnoreCase))
                    {
                        ProgressChanged?.Invoke($"Removing forbidden: {Path.GetFileName(file)}", -1);
                        try { File.Delete(file); } catch { }
                    }
                }
            }
        }

        if (config.Mods?.Required != null)
        {
            var total = config.Mods.Required.Count;
            for (int i = 0; i < total; i++)
            {
                var mod = config.Mods.Required[i];
                var dest = Path.Combine(modsDir, mod.Filename);
                var progress = (double)(i + 1) / total * 100;

                if (File.Exists(dest) && !string.IsNullOrEmpty(mod.Sha256))
                {
                    var hash = await ComputeSha256Async(dest);
                    if (hash.Equals(mod.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        ProgressChanged?.Invoke($"Verified: {mod.Name}", progress);
                        continue;
                    }
                    File.Delete(dest);
                }

                if (!File.Exists(dest))
                {
                    ProgressChanged?.Invoke($"Downloading: {mod.Name} ({i + 1}/{total})", progress);
                    try
                    {
                        var bytes = await Http.GetByteArrayAsync(mod.Url);
                        await File.WriteAllBytesAsync(dest, bytes);

                        if (!string.IsNullOrEmpty(mod.Sha256))
                        {
                            var hash = await ComputeSha256Async(dest);
                            if (!hash.Equals(mod.Sha256, StringComparison.OrdinalIgnoreCase))
                            {
                                File.Delete(dest);
                                ProgressChanged?.Invoke($"Hash mismatch: {mod.Name}", progress);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        ProgressChanged?.Invoke($"Failed: {mod.Name} — {ex.Message}", progress);
                    }
                }
            }
        }

        ProgressChanged?.Invoke("Mods synced!", 100);
    }

    private static async Task<string> ComputeSha256Async(string path)
    {
        using var sha = SHA256.Create();
        await using var stream = File.OpenRead(path);
        var hash = await sha.ComputeHashAsync(stream);
        return Convert.ToHexStringLower(hash);
    }
}
