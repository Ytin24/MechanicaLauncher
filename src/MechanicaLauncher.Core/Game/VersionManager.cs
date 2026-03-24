using System.Net.Http.Json;
using System.Text.Json;
using MechanicaLauncher.Core.Models;

namespace MechanicaLauncher.Core.Game;

public sealed class VersionManager
{
    private static readonly HttpClient Http = new();
    private const string ManifestUrl = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";
    private readonly string _sharedDir;

    public VersionManager(string sharedDir)
    {
        _sharedDir = sharedDir;
    }

    public async Task<VersionManifest> GetManifestAsync()
    {
        return await Http.GetFromJsonAsync<VersionManifest>(ManifestUrl) ?? new();
    }

    public async Task<VersionMeta> GetVersionMetaAsync(VersionEntry entry)
    {
        var localPath = Path.Combine(_sharedDir, "versions", entry.Id, $"{entry.Id}.json");
        if (File.Exists(localPath))
        {
            var json = await File.ReadAllTextAsync(localPath);
            return JsonSerializer.Deserialize<VersionMeta>(json) ?? new();
        }

        var rawJson = await Http.GetStringAsync(entry.Url);
        Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
        await File.WriteAllTextAsync(localPath, rawJson);
        return JsonSerializer.Deserialize<VersionMeta>(rawJson) ?? new();
    }

    public async Task<VersionMeta> GetMergedMetaAsync(string versionId, string instanceGameDir)
    {
        var instanceVersionPath = Path.Combine(instanceGameDir, "versions", versionId, $"{versionId}.json");
        var sharedVersionPath = Path.Combine(_sharedDir, "versions", versionId, $"{versionId}.json");

        string? json = null;
        if (File.Exists(instanceVersionPath))
            json = await File.ReadAllTextAsync(instanceVersionPath);
        else if (File.Exists(sharedVersionPath))
            json = await File.ReadAllTextAsync(sharedVersionPath);

        if (json == null)
            throw new FileNotFoundException($"Version JSON not found for {versionId}");

        var meta = JsonSerializer.Deserialize<VersionMeta>(json) ?? new();

        if (!string.IsNullOrEmpty(meta.InheritsFrom))
        {
            var parentMeta = await LoadVersionMetaAsync(meta.InheritsFrom);
            meta = MergeVersionMeta(parentMeta, meta);
        }

        return meta;
    }

    private async Task<VersionMeta> LoadVersionMetaAsync(string versionId)
    {
        var path = Path.Combine(_sharedDir, "versions", versionId, $"{versionId}.json");
        if (File.Exists(path))
        {
            var json = await File.ReadAllTextAsync(path);
            return JsonSerializer.Deserialize<VersionMeta>(json) ?? new();
        }

        var manifest = await GetManifestAsync();
        var entry = manifest.Versions.FirstOrDefault(v => v.Id == versionId)
            ?? throw new Exception($"Version {versionId} not found in manifest");

        return await GetVersionMetaAsync(entry);
    }

    private static VersionMeta MergeVersionMeta(VersionMeta parent, VersionMeta child)
    {
        return new VersionMeta
        {
            Id = child.Id,
            MainClass = !string.IsNullOrEmpty(child.MainClass) ? child.MainClass : parent.MainClass,
            Type = !string.IsNullOrEmpty(child.Type) ? child.Type : parent.Type,
            Assets = !string.IsNullOrEmpty(child.Assets) ? child.Assets : parent.Assets,
            AssetIndex = child.AssetIndex ?? parent.AssetIndex,
            Downloads = child.Downloads.Count > 0 ? child.Downloads : parent.Downloads,
            Libraries = [.. child.Libraries, .. parent.Libraries],
            Arguments = MergeArguments(parent.Arguments, child.Arguments),
            MinecraftArguments = child.MinecraftArguments ?? parent.MinecraftArguments,
            JavaVersion = child.JavaVersion ?? parent.JavaVersion,
        };
    }

    private static GameArguments? MergeArguments(GameArguments? parent, GameArguments? child)
    {
        if (parent == null) return child;
        if (child == null) return parent;
        return new GameArguments
        {
            Game = [.. child.Game, .. parent.Game],
            Jvm = [.. child.Jvm, .. parent.Jvm],
        };
    }

    public bool IsVersionInstalled(string versionId, string instanceGameDir)
    {
        var jar = Path.Combine(instanceGameDir, "versions", versionId, $"{versionId}.jar");
        return File.Exists(jar);
    }
}
