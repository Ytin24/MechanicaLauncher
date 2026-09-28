using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MechanicaLauncher.Core.IO;

namespace MechanicaLauncher.Core.Mods;

public enum CatalogMatch { Matched, NotFound, Unavailable }

public sealed record IndexedContent(string FilePath, string FileName, bool Enabled, long SizeBytes,
    string Sha1, string DisplayName, string DisplayVersion, string? ProjectId, string? VersionId,
    string? IconUrl, CatalogMatch Match);

public sealed class InstalledContentIndex(ModrinthClient client)
{
    private static readonly TimeSpan NegativeLifetime = TimeSpan.FromMinutes(15);
    private readonly SemaphoreSlim _scanGate = new(1, 1);

    private sealed record CacheEntry
    {
        public string Sha1 { get; init; } = "";
        public long SizeBytes { get; init; }
        public long LastWriteUtcTicks { get; init; }
        public long CreationUtcTicks { get; init; }
        public string? LocalName { get; init; }
        public string? LocalVersion { get; init; }
        public string? ProjectId { get; init; }
        public string? VersionId { get; init; }
        public string? ProjectTitle { get; init; }
        public string? VersionNumber { get; init; }
        public string? IconUrl { get; init; }
        public DateTimeOffset? NotFoundAt { get; init; }
    }

    private sealed record CacheData(int FormatVersion, Dictionary<string, CacheEntry> Files);
    private sealed record ScannedFile(InstalledMod File, CacheEntry Entry);

    public Task<IReadOnlyList<IndexedContent>> ScanAsync(string gameDir, string contentType, string? worldName = null,
        CancellationToken cancellationToken = default, IProgress<IReadOnlyList<IndexedContent>>? progress = null) => Task.Run(async () =>
    {
        await _scanGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await ScanCoreAsync(gameDir, contentType, worldName, cancellationToken, progress).ConfigureAwait(false); }
        finally { _scanGate.Release(); }
    }, cancellationToken);

    private async Task<IReadOnlyList<IndexedContent>> ScanCoreAsync(string gameDir, string contentType, string? worldName,
        CancellationToken cancellationToken, IProgress<IReadOnlyList<IndexedContent>>? progress)
    {
        gameDir = Path.GetFullPath(gameDir);
        var directory = ModInstaller.GetContentDirectory(gameDir, contentType, worldName);
        var cachePath = Path.Combine(gameDir, ".mechanica", "content-index.json");
        var cache = await ReadCacheAsync(cachePath, cancellationToken).ConfigureAwait(false);
        var known = cache.Files.Values.Where(e => !string.IsNullOrEmpty(e.Sha1))
            .GroupBy(e => e.Sha1, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(IsMatched).ThenByDescending(e => e.ProjectTitle != null)
                .ThenByDescending(e => e.NotFoundAt).First(), StringComparer.OrdinalIgnoreCase);
        var scanned = new List<ScannedFile>();
        foreach (var file in ModInstaller.GetInstalledMods(directory, contentType == "mod" ? ".jar" : ".zip"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await using var stream = new FileStream(file.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                    81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
                var hash = Convert.ToHexString(await SHA1.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
                var info = new FileInfo(file.FilePath);
                file.SizeBytes = stream.Length;
                if (!known.TryGetValue(hash, out var entry))
                {
                    stream.Position = 0;
                    var (name, version) = ReadLocalMetadata(stream);
                    entry = new() { Sha1 = hash, LocalName = CleanLabel(name), LocalVersion = CleanVersion(version) };
                }
                entry = entry with { SizeBytes = stream.Length, LastWriteUtcTicks = info.LastWriteTimeUtc.Ticks,
                    CreationUtcTicks = info.CreationTimeUtc.Ticks };
                known[hash] = entry;
                scanned.Add(new(file, entry));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (File.Exists(file.FilePath)) scanned.Add(new(file, new()));
            }
        }

        var now = DateTimeOffset.UtcNow;
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(scanned.Select(item => ToIndexed(item, item.Entry, now)).ToArray());
        var current = scanned.Where(s => s.Entry.Sha1.Length > 0).Select(s => s.Entry.Sha1)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToDictionary(h => h, h => known[h], StringComparer.OrdinalIgnoreCase);
        foreach (var batch in current.Where(e => !IsMatched(e.Value) && !IsFreshNegative(e.Value, now)).Select(e => e.Key).ToArray().Chunk(100))
        {
            try
            {
                var versions = await client.GetVersionsFromHashesAsync(batch, cancellationToken).ConfigureAwait(false);
                foreach (var hash in batch)
                {
                    if (!versions.TryGetValue(hash, out var version)) current[hash] = current[hash] with { NotFoundAt = now };
                    else if (!string.IsNullOrWhiteSpace(version.ProjectId) && !string.IsNullOrWhiteSpace(version.Id))
                        current[hash] = current[hash] with { ProjectId = version.ProjectId, VersionId = version.Id,
                            VersionNumber = CleanVersion(version.VersionNumber), NotFoundAt = null };
                }
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested && (ex is HttpRequestException or JsonException or TaskCanceledException)) { }
        }

        var projectNames = cache.Files.Values.Where(e => IsMatched(e) && !string.IsNullOrWhiteSpace(e.ProjectTitle))
            .GroupBy(e => e.ProjectId!, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        foreach (var hash in current.Keys.ToArray())
            if (current[hash].ProjectTitle == null && current[hash].ProjectId is { } id && projectNames.TryGetValue(id, out var project))
                current[hash] = current[hash] with { ProjectTitle = project.ProjectTitle, IconUrl = project.IconUrl };
        foreach (var batch in current.Values.Where(e => IsMatched(e) && e.ProjectTitle == null).Select(e => e.ProjectId!)
            .Distinct(StringComparer.Ordinal).ToArray().Chunk(100))
        {
            try
            {
                var projects = await client.GetProjectsAsync(batch, cancellationToken).ConfigureAwait(false);
                foreach (var project in projects)
                    foreach (var hash in current.Where(e => e.Value.ProjectId == project.Id).Select(e => e.Key).ToArray())
                        current[hash] = current[hash] with { ProjectTitle = CleanLabel(project.Title), IconUrl = project.IconUrl };
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested && (ex is HttpRequestException or JsonException or TaskCanceledException)) { }
        }

        cancellationToken.ThrowIfCancellationRequested();
        var result = new List<IndexedContent>();
        var scope = Path.GetRelativePath(gameDir, directory);
        var updated = new Dictionary<string, CacheEntry>(cache.Files, StringComparer.OrdinalIgnoreCase);
        foreach (var key in updated.Keys.Where(k => string.Equals(Path.GetDirectoryName(k), scope, StringComparison.OrdinalIgnoreCase)).ToArray())
            updated.Remove(key);
        foreach (var item in scanned)
        {
            var entry = item.Entry.Sha1.Length > 0 ? current[item.Entry.Sha1] : item.Entry;
            result.Add(ToIndexed(item, entry, now));
            if (entry.Sha1.Length > 0)
                updated[Path.GetRelativePath(gameDir, item.File.FilePath)] = entry with { SizeBytes = item.File.SizeBytes,
                    LastWriteUtcTicks = item.Entry.LastWriteUtcTicks, CreationUtcTicks = item.Entry.CreationUtcTicks };
        }
        if (scanned.Count > 0 || cache.Files.Count > 0)
        {
            try { await AtomicFile.WriteTextAsync(cachePath, JsonSerializer.Serialize(new CacheData(1, updated)), cancellationToken).ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return result;
    }

    private static IndexedContent ToIndexed(ScannedFile item, CacheEntry entry, DateTimeOffset now) => new(
        item.File.FilePath, item.File.FileName, item.File.Enabled, item.File.SizeBytes, entry.Sha1,
        entry.ProjectTitle ?? entry.LocalName ?? item.File.FileName, entry.VersionNumber ?? entry.LocalVersion ?? "",
        entry.ProjectId, entry.VersionId, entry.IconUrl, IsMatched(entry) ? CatalogMatch.Matched :
            IsFreshNegative(entry, now) ? CatalogMatch.NotFound : CatalogMatch.Unavailable);

    private static bool IsMatched(CacheEntry entry) => !string.IsNullOrWhiteSpace(entry.ProjectId) && !string.IsNullOrWhiteSpace(entry.VersionId);
    private static bool IsFreshNegative(CacheEntry entry, DateTimeOffset now) => entry.NotFoundAt is { } checkedAt &&
        checkedAt <= now && now - checkedAt < NegativeLifetime;

    private static async Task<CacheData> ReadCacheAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            var data = JsonSerializer.Deserialize<CacheData>(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false));
            if (data?.FormatVersion == 1 && data.Files != null && data.Files.Values.All(e => e != null)) return data;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        return new(1, new(StringComparer.OrdinalIgnoreCase));
    }

    private static (string? Name, string? Version) ReadLocalMetadata(Stream stream)
    {
        try
        {
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
            foreach (var name in new[] { "fabric.mod.json", "quilt.mod.json", "META-INF/neoforge.mods.toml", "META-INF/mods.toml", "mcmod.info", "pack.mcmeta" })
            {
                var entry = zip.GetEntry(name);
                if (entry == null || entry.Length > 1024 * 1024) continue;
                try
                {
                    using var input = entry.Open();
                    if (name.EndsWith(".toml", StringComparison.Ordinal))
                    {
                        using var reader = new StreamReader(input, Encoding.UTF8);
                        var metadata = ReadToml(reader.ReadToEnd());
                        return (metadata.Name, metadata.Version == "${file.jarVersion}" ? ReadManifestVersion(zip) : metadata.Version);
                    }
                    using var document = ModCompatibilityChecker.ReadModMetadata(input);
                    var root = document.RootElement;
                    if (name == "fabric.mod.json") return (GetString(root, "name"), GetString(root, "version"));
                    if (name == "quilt.mod.json" && root.TryGetProperty("quilt_loader", out var loader))
                        return (loader.TryGetProperty("metadata", out var metadata) ? GetString(metadata, "name") : null, GetString(loader, "version"));
                    if (name == "mcmod.info")
                    {
                        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("modList", out var list)) root = list;
                        if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0) root = root[0];
                        return (GetString(root, "name"), GetString(root, "version"));
                    }
                    if (name == "pack.mcmeta" && root.TryGetProperty("pack", out var pack) && pack.TryGetProperty("description", out var description))
                        return (ReadText(description), null);
                }
                catch (Exception ex) when (ex is InvalidDataException or JsonException or InvalidOperationException or IOException) { }
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException) { }
        return (null, null);
    }

    private static string? GetString(JsonElement value, string property) => value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty(property, out var field) && field.ValueKind == JsonValueKind.String ? field.GetString() : null;

    private static string ReadText(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? "",
        JsonValueKind.Array => string.Concat(value.EnumerateArray().Select(ReadText)),
        JsonValueKind.Object => (GetString(value, "text") ?? "") +
            (value.TryGetProperty("extra", out var extra) ? ReadText(extra) : ""),
        _ => ""
    };

    private static string? CleanLabel(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var label = new StringBuilder();
        for (int i = 0; i < value.Length && label.Length < 512; i++)
        {
            if (value[i] == '§' && i + 1 < value.Length) { i++; continue; }
            if (value[i] is '\r' or '\n') break;
            label.Append(char.IsControl(value[i]) ? ' ' : value[i]);
        }
        var text = label.ToString().Trim();
        return text.Length == 0 ? null : text;
    }

    private static string? CleanVersion(string? value) => value?.Contains("${", StringComparison.Ordinal) == true ? null : CleanLabel(value);

    private static (string? Name, string? Version) ReadToml(string text)
    {
        string? name = null, version = null, multiline = null, multilineKey = null;
        var valueLines = new StringBuilder();
        bool modSection = false;
        foreach (var raw in text.Split('\n'))
        {
            if (multiline != null)
            {
                var end = raw.IndexOf(multiline, StringComparison.Ordinal);
                valueLines.Append('\n').Append(end < 0 ? raw : raw[..end]);
                if (end < 0) continue;
                if (modSection && multilineKey == "displayName") name = valueLines.ToString().Trim();
                if (modSection && multilineKey == "version") version = valueLines.ToString().Trim();
                multiline = null;
                continue;
            }
            var line = raw.Trim();
            if (line.StartsWith('#') || line.Length == 0) continue;
            if (line.StartsWith('['))
            {
                if (modSection) break;
                modSection = line.Split('#', 2)[0].Trim() == "[[mods]]";
                continue;
            }
            int equals = line.IndexOf('=');
            if (equals < 0) continue;
            var key = line[..equals].Trim();
            var value = line[(equals + 1)..].TrimStart();
            if (value.StartsWith("\"\"\"", StringComparison.Ordinal) || value.StartsWith("'''", StringComparison.Ordinal))
            {
                var delimiter = value[..3];
                int end = value.IndexOf(delimiter, 3, StringComparison.Ordinal);
                if (end < 0)
                {
                    multiline = delimiter;
                    multilineKey = key;
                    valueLines.Clear().Append(value[3..]);
                    continue;
                }
                value = value[3..end];
            }
            else value = ReadTomlString(value) ?? "";
            if (modSection && key == "displayName") name = value;
            if (modSection && key == "version") version = value;
        }
        return (name, version);
    }

    private static string? ReadTomlString(string value)
    {
        if (value.Length < 2 || value[0] is not ('"' or '\'')) return null;
        bool escaped = false;
        for (int i = 1; i < value.Length; i++)
        {
            if (escaped) { escaped = false; continue; }
            if (value[0] == '"' && value[i] == '\\') { escaped = true; continue; }
            if (value[i] != value[0]) continue;
            return value[0] == '\'' ? value[1..i] : JsonSerializer.Deserialize<string>(value[..(i + 1)]);
        }
        return null;
    }

    private static string? ReadManifestVersion(ZipArchive zip)
    {
        var entry = zip.GetEntry("META-INF/MANIFEST.MF");
        if (entry == null || entry.Length > 64 * 1024) return null;
        using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
        var lines = reader.ReadToEnd().Replace("\r\n ", "", StringComparison.Ordinal).Replace("\n ", "", StringComparison.Ordinal).Split('\n');
        const string key = "Implementation-Version:";
        return lines.FirstOrDefault(line => line.StartsWith(key, StringComparison.OrdinalIgnoreCase))?[key.Length..].Trim();
    }
}
