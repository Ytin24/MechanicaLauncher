using System.Net.Http.Json;
using System.Text.Json;
using MechanicaLauncher.Core.Models;

namespace MechanicaLauncher.Core.Game;

public sealed class VersionManager
{
    private static readonly HttpClient Http = new();
    private const string ManifestUrl = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";

    private readonly string _gameDir;

    public VersionManager(string? gameDir = null)
    {
        _gameDir = gameDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft");
    }

    public string GameDir => _gameDir;

    public async Task<VersionManifest> GetManifestAsync()
    {
        return await Http.GetFromJsonAsync<VersionManifest>(ManifestUrl) ?? new();
    }

    public async Task<VersionMeta> GetVersionMetaAsync(VersionEntry entry)
    {
        var localPath = Path.Combine(_gameDir, "versions", entry.Id, $"{entry.Id}.json");
        if (File.Exists(localPath))
        {
            var json = await File.ReadAllTextAsync(localPath);
            return JsonSerializer.Deserialize<VersionMeta>(json) ?? new();
        }

        var meta = await Http.GetFromJsonAsync<VersionMeta>(entry.Url) ?? new();
        Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
        await File.WriteAllTextAsync(localPath, JsonSerializer.Serialize(meta));
        return meta;
    }

    public bool IsVersionInstalled(string versionId)
    {
        var jar = Path.Combine(_gameDir, "versions", versionId, $"{versionId}.jar");
        return File.Exists(jar);
    }

    public List<string> GetInstalledVersions()
    {
        var versionsDir = Path.Combine(_gameDir, "versions");
        if (!Directory.Exists(versionsDir)) return [];

        return Directory.GetDirectories(versionsDir)
            .Select(Path.GetFileName)
            .Where(name => name != null && File.Exists(Path.Combine(versionsDir, name!, $"{name}.json")))
            .Cast<string>()
            .ToList();
    }
}
