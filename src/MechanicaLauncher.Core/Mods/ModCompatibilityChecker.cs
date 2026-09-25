using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using MechanicaLauncher.Core.Instances;
using MechanicaLauncher.Core.IO;
using MechanicaLauncher.Core.Models;

namespace MechanicaLauncher.Core.Mods;

public sealed record CompatibilityIssue(string Code, string Detail, bool IsError);
public sealed record CompatibilityReport(int EnabledFiles, int IdentifiedFiles, int DisabledFiles,
    bool CatalogAvailable, IReadOnlyList<CompatibilityIssue> Issues);

public sealed class ModCompatibilityChecker(ModrinthClient? client = null)
{
    private sealed record LocalMod(string Id, string Version, string File, bool Nested,
        Dictionary<string, string[]> Dependencies, Dictionary<string, string[]> Breaks);

    public async Task<CompatibilityReport> CheckAsync(GameInstance instance, string gameDir, bool useCatalog,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var issues = new List<CompatibilityIssue>();
        var directory = Path.Combine(gameDir, "mods");
        if (!Directory.Exists(directory)) return new(0, 0, 0, true, issues);
        var files = Directory.GetFiles(directory, "*.jar").Order(StringComparer.OrdinalIgnoreCase).ToArray();
        var disabled = Directory.GetFiles(directory, "*.jar.disabled").Length;
        var mods = new List<LocalMod>();
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
                hashes[file] = Convert.ToHexString(await SHA1.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
                stream.Position = 0;
                await Task.Run(() =>
                {
                    using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
                    var fabric = zip.GetEntry("fabric.mod.json") != null;
                    var quilt = zip.GetEntry("quilt.mod.json") != null;
                    var forge = zip.GetEntry("META-INF/mods.toml") != null || zip.GetEntry("mcmod.info") != null;
                    var neo = zip.GetEntry("META-INF/neoforge.mods.toml") != null;
                    bool recognized = fabric || quilt || forge || neo;
                    bool loaderMatches = instance.Loader switch
                    {
                        LoaderType.Fabric => fabric,
                        LoaderType.Quilt => quilt || fabric,
                        LoaderType.Forge => forge,
                        LoaderType.NeoForge => neo || forge,
                        _ => false
                    };
                    if (recognized && !loaderMatches)
                        issues.Add(new("loader", Path.GetFileName(file), true));
                    var budget = 32 * 1024 * 1024L;
                    ReadFabric(zip, Path.GetFileName(file), mods, 0, ref budget);
                }, cancellationToken);
            }
            catch (Exception ex) when (ex is InvalidDataException or JsonException or IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                issues.Add(new("unreadable", Path.GetFileName(file), true));
            }
        }
        if (instance.Loader == LoaderType.None && files.Length > 0)
            issues.Add(new("vanilla", string.Join(", ", files.Select(Path.GetFileName).Take(5)), true));

        var ids = mods.Select(m => m.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        ids.UnionWith(["minecraft", "java", "fabricloader", "quilt_loader"]);
        foreach (var group in mods.Where(m => !m.Nested).GroupBy(m => m.Id).Where(g => g.Select(m => m.File).Distinct().Count() > 1))
            issues.Add(new("duplicate", $"{group.Key}: {string.Join(", ", group.Select(m => m.File).Distinct())}", true));
        var installedVersions = mods.GroupBy(m => m.Id).ToDictionary(g => g.Key, g => g.Select(m => m.Version).ToArray());
        installedVersions["minecraft"] = [instance.McVersion];
        if (instance.Loader == LoaderType.Fabric && instance.LoaderVersion != null) installedVersions["fabricloader"] = [instance.LoaderVersion];
        if (instance.Loader == LoaderType.Quilt && instance.LoaderVersion != null) installedVersions["quilt_loader"] = [instance.LoaderVersion];
        foreach (var mod in mods)
        {
            foreach (var (dependency, ranges) in mod.Dependencies)
            {
                if (!ids.Contains(dependency)) issues.Add(new("dependency", $"{mod.File} → {dependency}", false));
                else if (installedVersions.TryGetValue(dependency, out var present) && present.All(v => FabricVersionRequirement.Matches(v, ranges) == false))
                    issues.Add(new("version_range", $"{mod.File} → {dependency} {string.Join(" | ", ranges)} · {string.Join(", ", present)}", true));
            }
            foreach (var (incompatible, ranges) in mod.Breaks)
                if (installedVersions.TryGetValue(incompatible, out var present) && present.Any(v => FabricVersionRequirement.Matches(v, ranges) == true))
                    issues.Add(new("conflict", $"{mod.File} ↔ {incompatible} {string.Join(" | ", ranges)}", true));
        }

        var cachePath = Path.Combine(gameDir, ".mechanica", "compatibility.json");
        Dictionary<string, ModrinthVersion> versions;
        try { versions = JsonSerializer.Deserialize<Dictionary<string, ModrinthVersion>>(await File.ReadAllTextAsync(cachePath, cancellationToken)) ?? []; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { versions = []; }
        bool available = !useCatalog || hashes.Count == 0;
        if (useCatalog && hashes.Count > 0)
        {
            try
            {
                versions = await (client ?? new ModrinthClient()).GetVersionsFromHashesAsync(hashes.Values, cancellationToken);
                available = true;
                try { AtomicFile.WriteText(cachePath, JsonSerializer.Serialize(versions)); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested && (ex is HttpRequestException or JsonException or TaskCanceledException))
            {
                issues.Add(new("offline", "", false));
            }
        }
        var identified = hashes.Where(h => versions.ContainsKey(h.Value)).Select(h => (File: h.Key, Version: versions[h.Value])).ToArray();
        var projectIds = identified.Select(v => v.Version.ProjectId).Where(id => id != null).ToHashSet();
        var versionIds = identified.Select(v => v.Version.Id).ToHashSet();
        foreach (var (file, version) in identified)
        {
            if (!version.GameVersions.Contains(instance.McVersion))
                issues.Add(new("minecraft", $"{Path.GetFileName(file)} · Minecraft {string.Join(", ", version.GameVersions)}", true));
            var loader = instance.Loader.ToString().ToLowerInvariant();
            if (!version.Loaders.Contains(loader) && !(instance.Loader == LoaderType.Quilt && version.Loaders.Contains("fabric")))
                issues.Add(new("loader", $"{Path.GetFileName(file)} · {string.Join(", ", version.Loaders)}", true));
            foreach (var dependency in version.Dependencies)
            {
                bool present = dependency.VersionId != null ? versionIds.Contains(dependency.VersionId)
                    : dependency.ProjectId != null && projectIds.Contains(dependency.ProjectId);
                if (dependency.DependencyType == "required" && !present)
                    issues.Add(new("catalog_dependency", $"{Path.GetFileName(file)} → {dependency.ProjectId ?? dependency.VersionId}", false));
                if (dependency.DependencyType == "incompatible" && present)
                    issues.Add(new("conflict", $"{Path.GetFileName(file)} ↔ {dependency.ProjectId ?? dependency.VersionId}", true));
            }
        }
        foreach (var group in identified.Where(x => x.Version.ProjectId != null).GroupBy(x => x.Version.ProjectId).Where(g => g.Count() > 1))
            issues.Add(new("duplicate", string.Join(", ", group.Select(x => Path.GetFileName(x.File))), true));
        return new(files.Length, identified.Length, disabled, available, issues.Distinct().ToArray());
    }

    private static void ReadFabric(ZipArchive zip, string file, List<LocalMod> mods, int depth, ref long budget)
    {
        if (depth > 3) return;
        var entry = zip.GetEntry("fabric.mod.json");
        if (entry == null) return;
        if (entry.Length > 1024 * 1024) throw new InvalidDataException("Mod metadata is too large.");
        using var input = entry.Open();
        using var document = JsonDocument.Parse(input);
        var root = document.RootElement;
        if (root.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
        {
            mods.Add(new(id.GetString()!, root.TryGetProperty("version", out var version) ? version.ToString() : "", file, depth > 0,
                ReadRequirements(root, "depends"), ReadRequirements(root, "breaks")));
        }
        if (root.TryGetProperty("provides", out var provides) && provides.ValueKind == JsonValueKind.Array)
            foreach (var alias in provides.EnumerateArray().Where(a => a.ValueKind == JsonValueKind.String))
                mods.Add(new(alias.GetString()!, "", file, true, [], []));
        if (!root.TryGetProperty("jars", out var jars) || jars.ValueKind != JsonValueKind.Array) return;
        foreach (var jar in jars.EnumerateArray())
        {
            if (!jar.TryGetProperty("file", out var name) || name.ValueKind != JsonValueKind.String) continue;
            var nested = zip.GetEntry(name.GetString()!);
            if (nested == null || nested.Length > budget) continue;
            budget -= nested.Length;
            using var stream = nested.Open();
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            memory.Position = 0;
            using var archive = new ZipArchive(memory);
            ReadFabric(archive, file, mods, depth + 1, ref budget);
        }
    }

    private static Dictionary<string, string[]> ReadRequirements(JsonElement root, string key)
    {
        if (!root.TryGetProperty(key, out var requirements) || requirements.ValueKind != JsonValueKind.Object) return [];
        return requirements.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.ValueKind switch
        {
            JsonValueKind.String => new[] { p.Value.GetString()! },
            JsonValueKind.Array => p.Value.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!).ToArray(),
            _ => []
        });
    }
}
