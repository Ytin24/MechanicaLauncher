using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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
        List<KeyValuePair<string, string[]>> Dependencies, List<KeyValuePair<string, string[]>> Breaks, List<MavenDependency>? MavenDependencies = null, bool Alias = false);
    private sealed record MavenDependency(string Id, string Range, string Kind);

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
                    if (instance.Loader is LoaderType.Forge or LoaderType.NeoForge)
                        ReadForge(zip, Path.GetFileName(file), mods, instance, 0, ref budget);
                    else ReadFabric(zip, Path.GetFileName(file), mods, 0, ref budget);
                }, cancellationToken);
            }
            catch (Exception ex) when (ex is InvalidDataException or JsonException or IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                issues.Add(new("unreadable", Path.GetFileName(file), true));
            }
        }
        if (instance.Loader == LoaderType.None && files.Length > 0)
            issues.Add(new("vanilla", string.Join(", ", files.Select(Path.GetFileName).Take(5)), true));
        if (instance.Loader == LoaderType.Fabric && FabricVersionRequirement.Matches(instance.LoaderVersion ?? "", [">=0.11.1"]) == true)
        {
            string path = Path.Combine(gameDir, "config", "fabric_loader_dependencies.json");
            if (File.Exists(path))
                try
                {
                    if (new FileInfo(path).Length > 1024 * 1024) throw new InvalidDataException("Dependency overrides are too large.");
                    ApplyFabricOverrides(await File.ReadAllTextAsync(path, cancellationToken), mods);
                }
                catch (Exception ex) when (ex is InvalidDataException or JsonException or IOException or UnauthorizedAccessException or InvalidOperationException)
                { issues.Add(new("unreadable", "fabric_loader_dependencies.json", true)); }
        }

        var ids = mods.Select(m => m.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        ids.UnionWith(["minecraft", "java"]);
        if (instance.Loader is LoaderType.Fabric or LoaderType.Quilt) ids.Add("fabricloader");
        if (instance.Loader == LoaderType.Quilt) ids.Add("quilt_loader");
        string? forgeId = instance.Loader == LoaderType.Forge || instance.Loader == LoaderType.NeoForge && instance.McVersion == "1.20.1"
            ? "forge" : instance.Loader == LoaderType.NeoForge ? "neoforge" : null;
        if (forgeId != null) ids.Add(forgeId);
        foreach (var group in mods.Where(m => !m.Nested).GroupBy(m => m.Id).Where(g => g.Select(m => m.File).Distinct().Count() > 1))
            issues.Add(new("duplicate", $"{group.Key}: {string.Join(", ", group.Select(m => m.File).Distinct())}", true));
        var installedVersions = mods.GroupBy(m => m.Id).ToDictionary(g => g.Key, g => g.Select(m => m.Version).ToArray());
        installedVersions["minecraft"] = [instance.McVersion];
        if (instance.Loader == LoaderType.Fabric && instance.LoaderVersion != null) installedVersions["fabricloader"] = [instance.LoaderVersion];
        if (instance.Loader == LoaderType.Quilt && instance.LoaderVersion != null) installedVersions["quilt_loader"] = [instance.LoaderVersion];
        if (forgeId != null && instance.LoaderVersion != null)
            installedVersions[forgeId] = [instance.LoaderVersion.StartsWith(instance.McVersion + "-", StringComparison.Ordinal)
                ? instance.LoaderVersion[(instance.McVersion.Length + 1)..] : instance.LoaderVersion];
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
            foreach (var dependency in mod.MavenDependencies ?? [])
            {
                string detail = $"{mod.File} → {dependency.Id} {dependency.Range}".TrimEnd();
                if (dependency.Kind == "unknown") { issues.Add(new("unknown", detail, false)); continue; }
                if (dependency.Kind == "language")
                {
                    string? language = forgeId == "forge" && dependency.Id is ("javafml" or "lowcodefml")
                        && installedVersions.TryGetValue("forge", out var forgeVersions) ? forgeVersions[0].Split('.')[0] : null;
                    bool? match = language == null ? null : MatchesMaven(language, dependency.Range);
                    if (match != true) issues.Add(new(match == false ? "version_range" : "unknown", detail, match == false));
                    continue;
                }
                if (!ids.Contains(dependency.Id))
                {
                    if (dependency.Kind == "required") issues.Add(new("dependency", detail, true));
                    continue;
                }
                var matches = installedVersions.TryGetValue(dependency.Id, out var present)
                    ? present.Select(v => MatchesMaven(v, dependency.Range)).ToArray() : new bool?[] { null };
                if (dependency.Kind is "incompatible" or "discouraged")
                {
                    if (matches.Any(m => m == true)) issues.Add(new("conflict", detail, dependency.Kind == "incompatible"));
                    else if (matches.Any(m => m == null)) issues.Add(new("unknown", detail, false));
                }
                else if (matches.All(m => m == false)) issues.Add(new("version_range", detail + " · " + string.Join(", ", present ?? []), true));
                else if (!matches.Any(m => m == true)) issues.Add(new("unknown", detail, false));
            }
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
        using var document = ReadModMetadata(input);
        var root = document.RootElement;
        if (root.TryGetProperty("environment", out var environment) && environment.ValueKind == JsonValueKind.String && environment.GetString() == "server") return;
        if (root.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
        {
            mods.Add(new(id.GetString()!, root.TryGetProperty("version", out var version) ? version.ToString() : "", file, depth > 0,
                ReadRequirements(root, "depends"), ReadRequirements(root, "breaks")));
        }
        if (root.TryGetProperty("provides", out var provides) && provides.ValueKind == JsonValueKind.Array)
            foreach (var alias in provides.EnumerateArray().Where(a => a.ValueKind == JsonValueKind.String))
                mods.Add(new(alias.GetString()!, "", file, true, [], [], Alias: true));
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

    internal static JsonDocument ReadModMetadata(Stream input)
    {
        using var buffer = new MemoryStream();
        input.CopyTo(buffer);
        var bytes = buffer.GetBuffer().AsSpan(0, (int)buffer.Length);
        using var normalized = new MemoryStream();
        bool quoted = false, escaped = false;
        int start = 0;
        for (int i = 0; i < bytes.Length; i++)
        {
            byte value = bytes[i];
            if (escaped) { escaped = false; continue; }
            if (quoted && value == '\\') { escaped = true; continue; }
            if (value == '"') { quoted = !quoted; continue; }
            if (!quoted || value >= 0x20) continue;

            // The loaders' Gson JsonReader accepts raw control characters inside quoted strings.
            normalized.Write(bytes[start..i]);
            normalized.Write("\\u00"u8);
            normalized.WriteByte("0123456789abcdef"u8[value >> 4]);
            normalized.WriteByte("0123456789abcdef"u8[value & 0x0f]);
            start = i + 1;
        }
        if (start == 0)
        {
            buffer.Position = 0;
            return JsonDocument.Parse(buffer);
        }
        normalized.Write(bytes[start..]);
        normalized.Position = 0;
        return JsonDocument.Parse(normalized);
    }

    private static List<KeyValuePair<string, string[]>> ReadRequirements(JsonElement root, string key)
    {
        if (!root.TryGetProperty(key, out var requirements) || requirements.ValueKind != JsonValueKind.Object) return [];
        return requirements.EnumerateObject().Select(p => new KeyValuePair<string, string[]>(p.Name, p.Value.ValueKind switch
        {
            JsonValueKind.String => new[] { p.Value.GetString()! },
            JsonValueKind.Array => p.Value.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!).ToArray(),
            _ => []
        })).ToList();
    }

    private static void ApplyFabricOverrides(string text, List<LocalMod> mods)
    {
        using var document = JsonDocument.Parse(text);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid dependency overrides.");
        var properties = root.EnumerateObject().ToArray();
        if (properties.Length == 0 || properties[0].Name != "version" || properties[0].Value.ValueKind != JsonValueKind.Number
            || !properties[0].Value.TryGetInt32(out int version) || version != 1
            || properties.Skip(1).Any(p => p.Name != "overrides")) throw new InvalidDataException("Unsupported dependency overrides.");
        if (!root.TryGetProperty("overrides", out var overrides)) return;
        if (overrides.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid dependency overrides.");
        foreach (var modOverride in overrides.EnumerateObject())
        {
            if (modOverride.Value.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid dependency override.");
            foreach (var operation in modOverride.Value.EnumerateObject())
            {
                string kind = operation.Name.TrimStart('+', '-');
                if (kind is not ("depends" or "breaks" or "recommends" or "suggests" or "conflicts")
                    || operation.Name != kind && operation.Name != "+" + kind && operation.Name != "-" + kind
                    || operation.Value.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid dependency override operation.");
                foreach (var dependency in operation.Value.EnumerateObject())
                    if (dependency.Value.ValueKind != JsonValueKind.String && (dependency.Value.ValueKind != JsonValueKind.Array
                        || dependency.Value.EnumerateArray().Any(v => v.ValueKind != JsonValueKind.String)))
                        throw new InvalidDataException("Invalid dependency override range.");
            }
            foreach (var mod in mods.Where(m => !m.Alias && m.Id == modOverride.Name))
                foreach (var (kind, dependencies) in new[] { ("depends", mod.Dependencies), ("breaks", mod.Breaks) })
                {
                    if (modOverride.Value.TryGetProperty(kind, out _))
                    {
                        dependencies.Clear(); dependencies.AddRange(ReadRequirements(modOverride.Value, kind));
                        continue;
                    }
                    var removed = ReadRequirements(modOverride.Value, "-" + kind).Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
                    dependencies.RemoveAll(p => removed.Contains(p.Key));
                    dependencies.AddRange(ReadRequirements(modOverride.Value, "+" + kind));
                }
        }
    }

    private static void ReadForge(ZipArchive zip, string file, List<LocalMod> mods, GameInstance instance, int depth, ref long budget)
    {
        if (depth > 3) return;
        var entry = (instance.Loader == LoaderType.NeoForge ? zip.GetEntry("META-INF/neoforge.mods.toml") : null)
            ?? zip.GetEntry("META-INF/mods.toml");
        if (entry != null)
        {
            var tables = ReadToml(EntryText(entry));
            var header = tables[0];
            string? language = TomlString(header, "modLoader"), languageRange = TomlString(header, "loaderVersion");
            string? manifestVersion = ManifestVersion(zip);
            foreach (var table in tables.Where(t => t.Name == "mods"))
            {
                string id = TomlString(table, "modId") ?? throw new InvalidDataException("Missing TOML mod ID.");
                string version = TomlString(table, "version") ?? "1";
                if (manifestVersion != null) version = version.Replace("${file.jarVersion}", manifestVersion, StringComparison.Ordinal);
                if (version.Contains("${", StringComparison.Ordinal)) version = "";
                var dependencies = new List<MavenDependency>();
                if (language != null && languageRange != null) dependencies.Add(new(language, languageRange, "language"));
                foreach (var dependency in tables.Where(t => t.Name == "dependencies." + id))
                {
                    string target = TomlString(dependency, "modId") ?? throw new InvalidDataException("Missing TOML dependency ID.");
                    string? side = dependency.Fields.ContainsKey("side") ? TomlString(dependency, "side") : "BOTH";
                    if (side == "SERVER") continue;
                    string? kind;
                    if (entry.FullName == "META-INF/neoforge.mods.toml")
                        kind = dependency.Fields.ContainsKey("type") ? TomlString(dependency, "type")?.ToLowerInvariant() : "required";
                    else
                        kind = dependency.Fields.TryGetValue("mandatory", out var mandatory)
                            ? mandatory == "true" ? "required" : mandatory == "false" ? "optional" : null : null;
                    string? range = dependency.Fields.ContainsKey("versionRange") ? TomlString(dependency, "versionRange") : "";
                    if (side is not ("BOTH" or "CLIENT") || kind is not ("required" or "optional" or "incompatible" or "discouraged") || range == null)
                        dependencies.Add(new(target, range ?? "", "unknown"));
                    else dependencies.Add(new(target, range, kind));
                }
                mods.Add(new(id, version, file, depth > 0, [], [], dependencies));
            }
        }
        else if (zip.GetEntry("mcmod.info") is { } legacy)
        {
            if (legacy.Length > 1024 * 1024) throw new InvalidDataException("Mod metadata is too large.");
            using var input = legacy.Open();
            using var document = ReadModMetadata(input);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("modList", out var list)) root = list;
            if (root.ValueKind == JsonValueKind.Array)
                foreach (var item in root.EnumerateArray())
                    if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("modid", out var id) && id.ValueKind == JsonValueKind.String)
                        mods.Add(new(id.GetString()!, item.TryGetProperty("version", out var version) ? version.ToString() : "", file, depth > 0, [], []));
        }
        if (zip.GetEntry("META-INF/jarjar/metadata.json") is not { } jarjar) return;
        using var metadata = JsonDocument.Parse(EntryText(jarjar));
        if (!metadata.RootElement.TryGetProperty("jars", out var jars) || jars.ValueKind != JsonValueKind.Array) return;
        foreach (var jar in jars.EnumerateArray())
        {
            if (jar.ValueKind != JsonValueKind.Object || !jar.TryGetProperty("path", out var path) || path.ValueKind != JsonValueKind.String) continue;
            var nested = zip.GetEntry(path.GetString()!);
            if (nested == null || nested.Length > budget) continue;
            budget -= nested.Length;
            using var input = nested.Open(); using var memory = new MemoryStream();
            input.CopyTo(memory); memory.Position = 0;
            using var archive = new ZipArchive(memory);
            ReadForge(archive, file, mods, instance, depth + 1, ref budget);
        }
    }

    private static string EntryText(ZipArchiveEntry entry)
    {
        if (entry.Length > 1024 * 1024) throw new InvalidDataException("Mod metadata is too large.");
        using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static string? ManifestVersion(ZipArchive zip)
    {
        if (zip.GetEntry("META-INF/MANIFEST.MF") is not { } manifest) return null;
        var lines = EntryText(manifest).Replace("\r\n ", "", StringComparison.Ordinal).Replace("\n ", "", StringComparison.Ordinal).Split('\n');
        const string key = "Implementation-Version:";
        return lines.FirstOrDefault(l => l.StartsWith(key, StringComparison.OrdinalIgnoreCase))?[key.Length..].Trim();
    }

    private sealed record TomlTable(string Name, Dictionary<string, string> Fields);
    private static List<TomlTable> ReadToml(string text)
    {
        var tables = new List<TomlTable> { new("", new(StringComparer.Ordinal)) };
        foreach (string statement in TomlStatements(text))
        {
            if (statement.StartsWith('['))
            {
                bool array = statement.StartsWith("[[", StringComparison.Ordinal);
                string end = array ? "]]" : "]";
                if (!statement.EndsWith(end, StringComparison.Ordinal)) throw new InvalidDataException("Invalid TOML section.");
                string name = statement[(array ? 2 : 1)..^end.Length].Trim();
                name = Regex.Replace(name, "\"([a-zA-Z0-9_-]+)\"|'([a-zA-Z0-9_-]+)'", m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value);
                name = Regex.Replace(name, @"\s*\.\s*", ".");
                tables.Add(new(name, new(StringComparer.Ordinal)));
                continue;
            }
            int equals = statement.IndexOf('=');
            if (equals < 0) throw new InvalidDataException("Invalid TOML field.");
            string key = statement[..equals].Trim();
            if (key.StartsWith('"') || key.StartsWith('\'')) key = DecodeTomlString(key) ?? key;
            if (!tables[^1].Fields.TryAdd(key, statement[(equals + 1)..].Trim())) throw new InvalidDataException("Duplicate TOML field.");
        }
        return tables;
    }

    private static IEnumerable<string> TomlStatements(string text)
    {
        var statement = new StringBuilder();
        char quote = '\0'; bool triple = false, escaped = false, comment = false;
        int brackets = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            if (comment) { if (ch != '\n') continue; comment = false; }
            if (quote != '\0')
            {
                statement.Append(ch);
                if (escaped) { escaped = false; continue; }
                if (quote == '"' && ch == '\\') { escaped = true; continue; }
                if (ch != quote) continue;
                if (triple)
                {
                    if (i + 2 >= text.Length || text[i + 1] != quote || text[i + 2] != quote) continue;
                    int count = 3;
                    while (count < 5 && i + count < text.Length && text[i + count] == quote) count++;
                    statement.Append(quote, count - 1); i += count - 1;
                }
                quote = '\0'; triple = false;
                continue;
            }
            if (ch == '#') { comment = true; continue; }
            if (ch is '"' or '\'')
            {
                quote = ch; triple = i + 2 < text.Length && text[i + 1] == ch && text[i + 2] == ch;
                statement.Append(ch);
                if (triple) { statement.Append(ch, 2); i += 2; }
                continue;
            }
            if (ch is '[' or '{') brackets++;
            if (ch is ']' or '}') brackets--;
            if (brackets < 0) throw new InvalidDataException("Invalid TOML brackets.");
            if (ch == '\n' && brackets == 0)
            {
                string value = statement.ToString().Trim(); statement.Clear();
                if (value.Length > 0) yield return value;
            }
            else statement.Append(ch);
        }
        if (quote != '\0' || brackets != 0) throw new InvalidDataException("Unterminated TOML value.");
        string last = statement.ToString().Trim();
        if (last.Length > 0) yield return last;
    }

    private static string? TomlString(TomlTable table, string key) => table.Fields.TryGetValue(key, out var value) ? DecodeTomlString(value) : null;
    private static string? DecodeTomlString(string value)
    {
        if (value.Length < 2 || value[0] is not ('"' or '\'')) return null;
        char quote = value[0]; int delimiter = value.StartsWith(new string(quote, 3), StringComparison.Ordinal) ? 3 : 1;
        if (value.Length < delimiter * 2 || !value.EndsWith(new string(quote, delimiter), StringComparison.Ordinal)) return null;
        string body = value[delimiter..^delimiter];
        if (delimiter == 3) { if (body.StartsWith("\r\n", StringComparison.Ordinal)) body = body[2..]; else if (body.StartsWith('\n')) body = body[1..]; }
        if (quote == '\'') return body;
        var decoded = new StringBuilder();
        for (int i = 0; i < body.Length; i++)
        {
            char ch = body[i];
            if (ch != '\\') { decoded.Append(ch); continue; }
            if (++i == body.Length) return null;
            ch = body[i];
            if (delimiter == 3 && char.IsWhiteSpace(ch))
            {
                while (i + 1 < body.Length && char.IsWhiteSpace(body[i + 1])) i++;
                continue;
            }
            if (ch is 'u' or 'U')
            {
                int count = ch == 'u' ? 4 : 8;
                if (i + count >= body.Length || !int.TryParse(body.AsSpan(i + 1, count), System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out int code) || code is < 0 or > 0x10ffff or >= 0xd800 and <= 0xdfff) return null;
                decoded.Append(char.ConvertFromUtf32(code)); i += count; continue;
            }
            char? escaped = ch switch { 'b' => '\b', 't' => '\t', 'n' => '\n', 'f' => '\f', 'r' => '\r', '"' => '"', '\\' => '\\', _ => null };
            if (escaped == null) return null;
            decoded.Append(escaped.Value);
        }
        return decoded.ToString();
    }

    internal static bool? MatchesMaven(string version, string expression)
    {
        expression = expression.Trim();
        if (expression.Length == 0) return true;
        if (expression[0] is not ('[' or '(')) return expression.IndexOfAny(['[', ']', '(', ')', ',']) < 0 ? true : null;
        var intervals = new List<(string Lower, string Upper, bool IncludeLower, bool IncludeUpper)>();
        string rest = expression;
        while (rest.Length > 0)
        {
            if (rest[0] is not ('[' or '(')) return null;
            int end = rest.IndexOfAny([']', ')']);
            if (end < 0) return null;
            string[] bounds = rest[1..end].Split(',').Select(s => s.Trim()).ToArray();
            bool lowerInclusive = rest[0] == '[', upperInclusive = rest[end] == ']';
            if (bounds.Length == 1)
            {
                if (!lowerInclusive || !upperInclusive || bounds[0].Length == 0) return null;
                bounds = [bounds[0], bounds[0]];
            }
            if (bounds.Length != 2) return null;
            if (bounds[0].Length > 0 && bounds[1].Length > 0)
            {
                int? comparison = CompareMaven(bounds[0], bounds[1]);
                if (comparison == null || comparison > 0 || comparison == 0 && (!lowerInclusive || !upperInclusive)) return null;
            }
            if (intervals.Count > 0 && intervals[^1].Upper.Length > 0)
            {
                if (bounds[0].Length == 0) return null;
                int? comparison = CompareMaven(bounds[0], intervals[^1].Upper);
                if (comparison == null || comparison < 0) return null;
            }
            intervals.Add((bounds[0], bounds[1], lowerInclusive, upperInclusive));
            rest = rest[(end + 1)..].Trim();
            if (rest.Length > 0)
            {
                if (rest[0] != ',') return null;
                rest = rest[1..].Trim(); if (rest.Length == 0) return null;
            }
        }
        bool unknown = false, matched = false;
        foreach (var interval in intervals)
        {
            int? lower = interval.Lower.Length == 0 ? 1 : CompareMaven(version, interval.Lower);
            int? upper = interval.Upper.Length == 0 ? -1 : CompareMaven(version, interval.Upper);
            if (lower == null || upper == null) { unknown = true; continue; }
            matched |= (lower > 0 || lower == 0 && interval.IncludeLower) && (upper < 0 || upper == 0 && interval.IncludeUpper);
        }
        return matched ? true : unknown ? null : false;
    }

    private static int? CompareMaven(string left, string right)
    {
        if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right)) return null;
        if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase)) return 0;
        var a = Parse(left); var b = Parse(right);
        if (a == null || b == null) return null;
        for (int i = 0; i < Math.Max(a.Value.Numbers.Length, b.Value.Numbers.Length); i++)
        {
            int comparison = a.Value.Numbers.ElementAtOrDefault(i).CompareTo(b.Value.Numbers.ElementAtOrDefault(i));
            if (comparison != 0) return comparison;
        }
        int qualifier = a.Value.Qualifier.CompareTo(b.Value.Qualifier);
        return qualifier != 0 ? qualifier : a.Value.Build.CompareTo(b.Value.Build);

        static (int[] Numbers, int Qualifier, int Build)? Parse(string value)
        {
            var match = Regex.Match(value, @"^(\d+(?:\.\d+)*)(?:-(alpha|beta|milestone|rc|snapshot|final|ga|release|sp)(\d+)?)?$",
                RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
            if (!match.Success) return null;
            var components = match.Groups[1].Value.Split('.');
            if (components.Length > 16) return null;
            var numbers = new int[components.Length];
            for (int i = 0; i < components.Length; i++) if (!int.TryParse(components[i], out numbers[i])) return null;
            int qualifier = match.Groups[2].Value.ToLowerInvariant() switch
                { "alpha" => 0, "beta" => 1, "milestone" => 2, "rc" => 3, "snapshot" => 4, "sp" => 6, _ => 5 };
            int build = 0;
            if (match.Groups[3].Success && match.Groups[2].Value.ToLowerInvariant() is not ("alpha" or "beta" or "milestone" or "rc")) return null;
            if (match.Groups[3].Success && !int.TryParse(match.Groups[3].Value, out build)) return null;
            return (numbers, qualifier, build);
        }
    }
}
