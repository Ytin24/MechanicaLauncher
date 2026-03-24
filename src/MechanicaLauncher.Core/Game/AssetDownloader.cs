using System.Net.Http.Json;
using MechanicaLauncher.Core.Models;

namespace MechanicaLauncher.Core.Game;

public sealed class AssetDownloader
{
    private static readonly HttpClient Http = new();
    private readonly string _gameDir;

    public AssetDownloader(string gameDir)
    {
        _gameDir = gameDir;
    }

    public event Action<string, double>? ProgressChanged;

    public async Task DownloadVersionAsync(VersionMeta meta)
    {
        // Download client jar
        if (meta.Downloads.TryGetValue("client", out var client))
        {
            var jarPath = Path.Combine(_gameDir, "versions", meta.Id, $"{meta.Id}.jar");
            await DownloadFileAsync(client.Url, jarPath, "Downloading client...");
        }

        // Download libraries
        var libs = meta.Libraries.Where(l => ShouldInclude(l)).ToList();
        for (int i = 0; i < libs.Count; i++)
        {
            var lib = libs[i];
            if (lib.Downloads?.Artifact is { } artifact && !string.IsNullOrEmpty(artifact.Url))
            {
                var libPath = Path.Combine(_gameDir, "libraries", artifact.Path);
                if (!File.Exists(libPath))
                {
                    var progress = (double)(i + 1) / libs.Count * 80;
                    ProgressChanged?.Invoke($"Libraries ({i + 1}/{libs.Count})", progress);
                    await DownloadFileAsync(artifact.Url, libPath);
                }
            }
        }

        // Download asset index
        if (meta.AssetIndex is { } assetIndex)
        {
            var indexPath = Path.Combine(_gameDir, "assets", "indexes", $"{assetIndex.Id}.json");
            if (!File.Exists(indexPath))
            {
                await DownloadFileAsync(assetIndex.Url, indexPath, "Downloading asset index...");
            }

            var indexJson = await File.ReadAllTextAsync(indexPath);
            var assets = System.Text.Json.JsonSerializer.Deserialize<AssetIndexData>(indexJson);
            if (assets != null)
            {
                var objects = assets.Objects.Values.ToList();
                for (int i = 0; i < objects.Count; i++)
                {
                    var obj = objects[i];
                    var prefix = obj.Hash[..2];
                    var assetPath = Path.Combine(_gameDir, "assets", "objects", prefix, obj.Hash);
                    if (!File.Exists(assetPath))
                    {
                        if (i % 50 == 0)
                        {
                            var progress = 80 + (double)(i + 1) / objects.Count * 20;
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

    private async Task DownloadFileAsync(string url, string path, string? status = null)
    {
        if (status != null) ProgressChanged?.Invoke(status, -1);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bytes = await Http.GetByteArrayAsync(url);
        await File.WriteAllBytesAsync(path, bytes);
    }

    private static bool ShouldInclude(Library lib)
    {
        if (lib.Rules == null || lib.Rules.Count == 0) return true;
        foreach (var rule in lib.Rules)
        {
            if (rule.Os?.Name == "osx" && rule.Action == "allow") return false;
            if (rule.Os?.Name == "linux" && rule.Action == "allow") return false;
            if (rule.Os?.Name == "windows" && rule.Action == "disallow") return false;
        }
        return true;
    }
}
