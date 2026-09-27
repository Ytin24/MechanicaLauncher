using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MechanicaLauncher.Core.Mods;

namespace MechanicaLauncher.Core.Servers;

public sealed partial class ServerModSync
{
    private const int MaxStateBytes = 8 * MaxJsonBytes;
    private static string MetaPath(string gameDir, string name) => SafePath(Path.Combine(gameDir, ".mechanica", name));
    private static string ModPath(string gameDir, string name) => SafePath(Path.Combine(gameDir, "mods", FileName(name)));

    internal static FileStream AcquireInstanceLock(string gameDir) => Acquire(gameDir);

    internal static async Task<IReadOnlySet<string>> GetManagedFileNamesAsync(string gameDir, CancellationToken cancellationToken = default)
        => (await GetManagedFilesAsync(gameDir, cancellationToken)).Select(f => f.FileName).ToHashSet(StringComparer.OrdinalIgnoreCase);

    internal static async Task<IReadOnlyList<ServerSyncManagedFile>> GetManagedFilesAsync(string gameDir, CancellationToken cancellationToken = default)
    {
        if (File.Exists(MetaPath(gameDir, "server-sync-journal.json"))) throw Error("busy", "Сначала нужно восстановить предыдущую установку.");
        var bytes = await ReadOptionalAsync(MetaPath(gameDir, "server-sync.json"), cancellationToken);
        return ReadState(bytes).Servers.SelectMany(s => s.Files).ToArray();
    }

    private static FileStream Acquire(string gameDir)
    {
        string path = MetaPath(gameDir, "server-sync.lock");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        SafePath(path);
        try { return new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException ex) { throw Error("busy", "Сборка уже обновляется.", ex); }
    }

    private static async Task<byte[]?> ReadOptionalAsync(string path, CancellationToken ct)
    {
        SafePath(path);
        try
        {
            await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
            if (input.Length > MaxStateBytes) throw Error("conflict", "Реестр синхронизации слишком большой.");
            using var output = new MemoryStream();
            await input.CopyToAsync(output, ct);
            if (output.Length > MaxStateBytes) throw Error("conflict", "Реестр синхронизации слишком большой.");
            return output.ToArray();
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    private static ServerSyncState ReadState(byte[]? bytes)
    {
        var state = bytes == null ? new ServerSyncState() : ReadJson<ServerSyncState>(bytes, MaxStateBytes);
        if (state.Version != 1 || state.Servers == null || state.Servers.Count > 64 || state.Servers.Any(s => s == null || s.Target == null ||
            s.ServerId == Guid.Empty || s.LastPlanId == Guid.Empty || !ValidTarget(s.Target.TargetId, s.Target.Minecraft, s.Target.Loader, s.Target.LoaderVersion) ||
            s.Revision is < 1 or > 9007199254740991 || !ValidHash(s.ManifestSha512) || s.Origin != Origin(CheckedUrl(s.Origin, true)) ||
            s.Files == null || s.Files.Any(f => f == null || !ValidId(f.ArtifactId) || FileName(f.FileName) != f.FileName || f.Size is <= 0 or > MaxFileBytes || !ValidHash(f.Sha512))))
            throw Error("conflict", "Повреждён реестр синхронизации.");
        var files = state.Servers.SelectMany(s => s.Files).ToArray();
        if (files.Length > MaxFiles || files.Select(f => f.FileName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != files.Length ||
            state.Servers.GroupBy(s => (s.ServerId, s.Origin, s.Target.TargetId)).Any(g => g.Count() > 1) ||
            state.Servers.Any(s => s.Files.Select(f => f.ArtifactId).Distinct(StringComparer.Ordinal).Count() != s.Files.Count))
            throw Error("conflict", "Повреждён реестр синхронизации.");
        return state;
    }

    private static async Task<List<ServerSyncSeen>> ReadSeenAsync(string gameDir, CancellationToken ct)
    {
        var bytes = await ReadOptionalAsync(MetaPath(gameDir, "server-sync-seen.json"), ct);
        if (bytes == null) return [];
        var seen = ReadJson<List<ServerSyncSeen>>(bytes, MaxStateBytes);
        if (seen.Count > 128 || seen.Any(s => s == null || s.ServerId == Guid.Empty || s.Target == null ||
            !ValidTarget(s.Target.TargetId, s.Target.Minecraft, s.Target.Loader, s.Target.LoaderVersion) || s.Revision is < 1 or > 9007199254740991 ||
            !ValidHash(s.ManifestSha512) || s.Origin != Origin(CheckedUrl(s.Origin, true))) ||
            seen.GroupBy(s => (s.ServerId, s.Origin, s.Target.TargetId)).Any(g => g.Count() > 1))
            throw Error("conflict", "Повреждён реестр ревизий сервера.");
        return seen;
    }

    private static async Task WriteJsonAsync<T>(string path, T value, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        if (bytes.Length > MaxStateBytes) throw Error("invalid_manifest", "Превышен размер журнала синхронизации.");
        await WriteBytesAsync(path, bytes, ct);
    }

    private static async Task WriteBytesAsync(string path, byte[] bytes, CancellationToken ct)
    {
        SafePath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = SafePath(path + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                await stream.WriteAsync(bytes, ct);
                stream.Flush(flushToDisk: true);
            }
            ct.ThrowIfCancellationRequested();
            SafePath(path);
            File.Move(temporary, path, overwrite: true);
        }
        finally { DeleteTemporary(temporary); }
    }

    private static void DeleteTemporary(string path)
    {
        try { SafePath(path); File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ServerModSyncException) { }
    }

    private static async Task<string> FileHashAsync(string path, CancellationToken ct)
    {
        SafePath(path);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        return Convert.ToHexString(await SHA512.HashDataAsync(stream, ct)).ToLowerInvariant();
    }

    private static async Task<bool> MatchesAsync(string path, string hash, long size, CancellationToken ct)
    {
        SafePath(path);
        return File.Exists(path) && new FileInfo(path).Length == size && await FileHashAsync(path, ct) == hash;
    }

    private static async Task<List<ServerSyncLocalFile>> ReadLocalAsync(string gameDir, CancellationToken ct)
    {
        var result = new List<ServerSyncLocalFile>();
        long total = 0;
        string mods = SafePath(Path.Combine(gameDir, "mods"));
        if (!Directory.Exists(mods)) return result;
        foreach (var path in Directory.EnumerateFileSystemEntries(mods).Order(StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            string name = Path.GetFileName(path);
            if (!name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) && !name.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase)) continue;
            SafePath(path);
            if (Directory.Exists(path)) continue;
            FileName(name, disabled: true);
            if (result.Count >= MaxFiles * 2) throw Error("conflict", "Слишком много локальных модов.");
            try
            {
                long size = new FileInfo(path).Length;
                total += size;
                if (size > MaxFileBytes || total > MaxPlanBytes) throw Error("conflict", $"Слишком большой локальный набор модов: {name}");
                string hash = await FileHashAsync(path, ct);
                result.Add(new(name, size, hash, ReadModIds(path), name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or InvalidOperationException)
            { throw Error("conflict", $"Не удалось проверить локальный мод: {name}", ex); }
        }
        return result;
    }

    private static string[] ReadModIds(string path)
    {
        using var file = new FileStream(SafePath(path), FileMode.Open, FileAccess.Read, FileShare.Read);
        using var zip = new ZipArchive(file, ZipArchiveMode.Read);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        long budget = 32 * 1024 * 1024;
        ReadIds(zip, ids, 0, ref budget);
        return ids.ToArray();
    }

    private static byte[] EntryBytes(ZipArchiveEntry entry, long limit)
    {
        if (entry.Length > limit) throw new InvalidDataException("Mod metadata exceeds the size limit.");
        using var stream = entry.Open();
        using var memory = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = stream.Read(buffer)) != 0)
        {
            if (memory.Length + read > limit) throw new InvalidDataException("Mod metadata exceeds the size limit.");
            memory.Write(buffer, 0, read);
        }
        return memory.ToArray();
    }

    private static void ReadIds(ZipArchive zip, HashSet<string> ids, int depth, ref long budget)
    {
        if (depth > 3) return;
        void Add(JsonElement e, string key)
        {
            if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 and <= 128 } id)
                ids.Add(id);
        }
        if (zip.GetEntry("fabric.mod.json") is { } fabric)
        {
            using var input = new MemoryStream(EntryBytes(fabric, MaxJsonBytes));
            using var doc = ModCompatibilityChecker.ReadFabricMetadata(input);
            var root = doc.RootElement;
            Add(root, "id");
            if (root.TryGetProperty("provides", out var provides) && provides.ValueKind == JsonValueKind.Array)
                foreach (var p in provides.EnumerateArray()) if (p.ValueKind == JsonValueKind.String && p.GetString() is { Length: > 0 and <= 128 } id) ids.Add(id);
            if (root.TryGetProperty("jars", out var jars) && jars.ValueKind == JsonValueKind.Array)
                foreach (var item in jars.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("file", out var name) || name.ValueKind != JsonValueKind.String) continue;
                    var entry = zip.GetEntry(name.GetString()!);
                    if (entry == null || entry.Length > budget) continue;
                    var bytes = EntryBytes(entry, budget);
                    budget -= bytes.Length;
                    using var memory = new MemoryStream(bytes);
                    using var nested = new ZipArchive(memory, ZipArchiveMode.Read);
                    ReadIds(nested, ids, depth + 1, ref budget);
                }
        }
        if (zip.GetEntry("quilt.mod.json") is { } quilt)
        {
            using var doc = JsonDocument.Parse(EntryBytes(quilt, MaxJsonBytes));
            if (doc.RootElement.TryGetProperty("quilt_loader", out var q)) Add(q, "id");
        }
        foreach (var metadata in new[] { "META-INF/mods.toml", "META-INF/neoforge.mods.toml" })
            if (zip.GetEntry(metadata) is { } toml)
            {
                string text = Encoding.UTF8.GetString(EntryBytes(toml, MaxJsonBytes));
                text = Regex.Replace(text, "(?s)\"\"\".*?\"\"\"|'''.*?'''", "", RegexOptions.CultureInvariant);
                bool mod = false;
                foreach (string line in text.Split('\n'))
                {
                    string trimmed = line.Trim();
                    if (trimmed.StartsWith('[')) { mod = Regex.IsMatch(trimmed, @"^\[\[mods\]\]\s*(#.*)?$", RegexOptions.CultureInvariant); continue; }
                    if (!mod) continue;
                    var match = Regex.Match(trimmed, "^modId\\s*=\\s*[\"']([a-zA-Z0-9_.-]+)[\"']", RegexOptions.CultureInvariant);
                    if (match.Success) ids.Add(match.Groups[1].Value);
                }
            }
        if (zip.GetEntry("mcmod.info") is { } legacy)
        {
            using var doc = JsonDocument.Parse(EntryBytes(legacy, MaxJsonBytes));
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("modList", out var list)) root = list;
            if (root.ValueKind == JsonValueKind.Array) foreach (var mod in root.EnumerateArray()) Add(mod, "modid");
        }
        if (ids.Count > MaxFiles) throw new InvalidDataException("Too many mod IDs in one archive.");
    }

    private static async Task CopyCheckedAsync(string source, string destination, string hash, long size, CancellationToken ct)
    {
        SafePath(source);
        SafePath(destination);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true))
        await using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
        {
            if (input.Length != size) throw Error("conflict", "Файл изменился после проверки.");
            await input.CopyToAsync(output, ct);
            output.Flush(flushToDisk: true);
        }
        if (!await MatchesAsync(destination, hash, size, ct)) throw Error("hash_mismatch", "Файл изменился после проверки.");
    }
}
