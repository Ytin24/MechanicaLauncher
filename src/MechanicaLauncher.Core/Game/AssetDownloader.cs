using System.IO.Compression;
using MechanicaLauncher.Core.Models;

namespace MechanicaLauncher.Core.Game;

public sealed class AssetDownloader
{
    private static readonly HttpClient Http = new();
    private readonly string _sharedDir;
    private readonly string _instanceGameDir;

    public AssetDownloader(string sharedDir, string instanceGameDir)
    {
        _sharedDir = sharedDir;
        _instanceGameDir = instanceGameDir;
    }

    public event Action<string, double>? ProgressChanged;

    public async Task DownloadVersionAsync(VersionMeta meta)
    {
        if (meta.Downloads.TryGetValue("client", out var client))
        {
            var jarPath = Path.Combine(_instanceGameDir, "versions", meta.Id, $"{meta.Id}.jar");
            if (!File.Exists(jarPath))
                await DownloadFileAsync(client.Url, jarPath, "Downloading client...");
        }

        var libs = meta.Libraries.Where(ShouldIncludeLibrary).ToList();
        for (int i = 0; i < libs.Count; i++)
        {
            var lib = libs[i];
            if (lib.Downloads?.Artifact is { } artifact && !string.IsNullOrEmpty(artifact.Url))
            {
                var libPath = Path.Combine(_sharedDir, "libraries", artifact.Path.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(libPath))
                {
                    var progress = (double)(i + 1) / libs.Count * 70;
                    ProgressChanged?.Invoke($"Libraries ({i + 1}/{libs.Count})", progress);
                    await DownloadFileAsync(artifact.Url, libPath);
                }
            }
        }

        await ExtractNativesAsync(meta);

        if (meta.AssetIndex is { } assetIndex)
        {
            var indexPath = Path.Combine(_sharedDir, "assets", "indexes", $"{assetIndex.Id}.json");
            if (!File.Exists(indexPath))
                await DownloadFileAsync(assetIndex.Url, indexPath, "Downloading asset index...");

            var indexJson = await File.ReadAllTextAsync(indexPath);
            var assets = System.Text.Json.JsonSerializer.Deserialize<AssetIndexData>(indexJson);
            if (assets != null)
            {
                var objects = assets.Objects.Values.ToList();
                for (int i = 0; i < objects.Count; i++)
                {
                    var obj = objects[i];
                    var prefix = obj.Hash[..2];
                    var assetPath = Path.Combine(_sharedDir, "assets", "objects", prefix, obj.Hash);
                    if (!File.Exists(assetPath))
                    {
                        if (i % 50 == 0)
                        {
                            var progress = 70 + (double)(i + 1) / objects.Count * 30;
                            ProgressChanged?.Invoke($"Assets ({i + 1}/{objects.Count})", progress);
                        }
                        var url = $"https://resources.download.minecraft.net/{prefix}/{obj.Hash}";
                        await DownloadFileAsync(url, assetPath);
                    }
                }
            }
        }

        ProgressChanged?.Invoke("Done!", 100);
    }

    private async Task ExtractNativesAsync(VersionMeta meta)
    {
        var nativesDir = Path.Combine(_instanceGameDir, "versions", meta.Id, "natives");
        Directory.CreateDirectory(nativesDir);
        ProgressChanged?.Invoke("Extracting natives...", 70);

        foreach (var lib in meta.Libraries)
        {
            if (!lib.Name.Contains("natives-windows", StringComparison.OrdinalIgnoreCase)) continue;
            if (lib.Downloads?.Artifact is not { } artifact) continue;

            var jarPath = Path.Combine(_sharedDir, "libraries", artifact.Path.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(jarPath)) continue;

            try
            {
                using var zip = ZipFile.OpenRead(jarPath);
                foreach (var entry in zip.Entries)
                {
                    if (!entry.FullName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) continue;
                    var dest = Path.Combine(nativesDir, entry.Name);
                    if (!File.Exists(dest))
                        entry.ExtractToFile(dest, true);
                }
            }
            catch { }
        }
    }

    private async Task DownloadFileAsync(string url, string path, string? status = null)
    {
        if (status != null) ProgressChanged?.Invoke(status, -1);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bytes = await Http.GetByteArrayAsync(url);
        await File.WriteAllBytesAsync(path, bytes);
    }

    public static bool ShouldIncludeLibrary(Library lib)
    {
        if (lib.Rules == null || lib.Rules.Count == 0)
        {
            if (lib.Name.Contains("natives-linux") || lib.Name.Contains("natives-macos") ||
                lib.Name.Contains("natives-osx") || lib.Name.Contains("linux-") ||
                lib.Name.Contains("macos-"))
                return false;
            return true;
        }

        bool allowed = false;
        bool hasOsRule = false;

        foreach (var rule in lib.Rules)
        {
            if (rule.Os != null)
            {
                hasOsRule = true;
                if (rule.Os.Name == "windows")
                {
                    if (rule.Action == "allow") allowed = true;
                    if (rule.Action == "disallow") return false;
                }
            }
            else
            {
                if (rule.Action == "allow") allowed = true;
            }
        }

        return !hasOsRule || allowed;
    }
}
