using System.Text.Json;
using MechanicaLauncher.Core.IO;
using MechanicaLauncher.Core.Models;

namespace MechanicaLauncher.Core.Game;

public sealed class VersionManager
{
    private static readonly HttpClient DefaultHttp = new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly HttpClient Http;
    private const string ManifestUrl = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";
    private readonly string _sharedDir;

    public VersionManager(string sharedDir, HttpClient? http = null)
    {
        _sharedDir = sharedDir;
        Http = http ?? DefaultHttp;
    }

    public async Task<VersionManifest> GetManifestAsync(CancellationToken cancellationToken = default)
    {
        var cachePath = Path.Combine(_sharedDir, "version_manifest.json");
        string json;
        try
        {
            json = await Http.GetStringAsync(ManifestUrl, cancellationToken);
        }
        catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException) && !cancellationToken.IsCancellationRequested && File.Exists(cachePath))
        {
            json = await File.ReadAllTextAsync(cachePath, cancellationToken);
        }
        var manifest = JsonSerializer.Deserialize<VersionManifest>(json)
            ?? throw new InvalidDataException("Minecraft version manifest is empty.");
        if (manifest.Versions.Count == 0) throw new InvalidDataException("Minecraft version manifest has no versions.");
        await AtomicFile.WriteTextAsync(cachePath, json, cancellationToken);
        return manifest;
    }

    public async Task<VersionMeta> GetVersionMetaAsync(VersionEntry entry, CancellationToken cancellationToken = default)
    {
        var localPath = GetVersionPath(_sharedDir, entry.Id);
        if (File.Exists(localPath))
        {
            try
            {
                var cached = JsonSerializer.Deserialize<VersionMeta>(await File.ReadAllTextAsync(localPath, cancellationToken));
                if (cached?.Id == entry.Id && !string.IsNullOrEmpty(cached.MainClass)) return cached;
            }
            catch (JsonException) { }
        }

        var rawJson = await Http.GetStringAsync(entry.Url, cancellationToken);
        var meta = JsonSerializer.Deserialize<VersionMeta>(rawJson)
            ?? throw new InvalidDataException($"Version metadata is empty: {entry.Id}");
        if (meta.Id != entry.Id || string.IsNullOrEmpty(meta.MainClass))
            throw new InvalidDataException($"Invalid version metadata: {entry.Id}");
        await AtomicFile.WriteTextAsync(localPath, rawJson, cancellationToken);
        return meta;
    }

    public async Task<VersionMeta> GetMergedMetaAsync(string versionId, string instanceGameDir, CancellationToken cancellationToken = default)
    {
        var instanceVersionPath = GetVersionPath(instanceGameDir, versionId);
        var sharedVersionPath = GetVersionPath(_sharedDir, versionId);

        string? json = null;
        if (File.Exists(instanceVersionPath))
            json = await File.ReadAllTextAsync(instanceVersionPath, cancellationToken);
        else if (File.Exists(sharedVersionPath))
            json = await File.ReadAllTextAsync(sharedVersionPath, cancellationToken);

        if (json == null)
            throw new FileNotFoundException($"Version JSON not found for {versionId}");

        var meta = JsonSerializer.Deserialize<VersionMeta>(json)
            ?? throw new InvalidDataException($"Version metadata is empty: {versionId}");
        if (string.IsNullOrWhiteSpace(meta.MainClass)) throw new InvalidDataException($"Main class is missing: {versionId}");

        if (!string.IsNullOrEmpty(meta.InheritsFrom))
        {
            var parentMeta = await GetVersionMetaAsync(meta.InheritsFrom, cancellationToken);
            meta = MergeVersionMeta(parentMeta, meta);
        }

        return meta;
    }

    public async Task<VersionMeta> GetVersionMetaAsync(string versionId, CancellationToken cancellationToken = default)
    {
        var path = GetVersionPath(_sharedDir, versionId);
        if (File.Exists(path))
        {
            try
            {
                var cached = JsonSerializer.Deserialize<VersionMeta>(await File.ReadAllTextAsync(path, cancellationToken));
                if (cached?.Id == versionId && !string.IsNullOrEmpty(cached.MainClass)) return cached;
            }
            catch (JsonException) { }
        }

        var manifest = await GetManifestAsync(cancellationToken);
        var entry = manifest.Versions.FirstOrDefault(v => v.Id == versionId)
            ?? throw new Exception($"Version {versionId} not found in manifest");

        return await GetVersionMetaAsync(entry, cancellationToken);
    }

    private static string GetVersionPath(string root, string versionId)
    {
        if (string.IsNullOrWhiteSpace(versionId) || versionId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || versionId is "." or "..")
            throw new InvalidDataException($"Invalid version ID: {versionId}");
        return FileDownloader.GetPath(root, $"versions/{versionId}/{versionId}.json");
    }

    private static VersionMeta MergeVersionMeta(VersionMeta parent, VersionMeta child)
    {
        return new VersionMeta
        {
            Id = child.Id,
            InheritsFrom = child.InheritsFrom,
            MainClass = !string.IsNullOrEmpty(child.MainClass) ? child.MainClass : parent.MainClass,
            Type = !string.IsNullOrEmpty(child.Type) ? child.Type : parent.Type,
            Assets = !string.IsNullOrEmpty(child.Assets) ? child.Assets : parent.Assets,
            AssetIndex = child.AssetIndex ?? parent.AssetIndex,
            Downloads = child.Downloads.Count > 0 ? child.Downloads : parent.Downloads,
            Libraries = DedupeLibraries(child.Libraries, parent.Libraries),
            Arguments = MergeArguments(parent.Arguments, child.Arguments),
            MinecraftArguments = child.MinecraftArguments ?? parent.MinecraftArguments,
            JavaVersion = child.JavaVersion ?? parent.JavaVersion,
            Logging = child.Logging.Count > 0 ? child.Logging : parent.Logging,
        };
    }

    // Child (loader) libraries take precedence over parent (vanilla) on group:artifact clash;
    // otherwise classpath ends up with two versions of asm/guava/etc and class-version mismatches surface at runtime.
    private static List<Library> DedupeLibraries(List<Library> child, List<Library> parent)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<Library>(child.Count + parent.Count);
        foreach (var lib in child.Concat(parent))
        {
            var key = LibraryKey(lib.Name);
            if (seen.Add(key)) result.Add(lib);
        }
        return result;
    }

    private static string LibraryKey(string name)
    {
        var parts = name.Split(':');
        if (parts.Length < 2) return name;
        // group:artifact[:classifier] — version excluded so different versions collide.
        return parts.Length >= 4 ? $"{parts[0]}:{parts[1]}:{parts[3]}" : $"{parts[0]}:{parts[1]}";
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
