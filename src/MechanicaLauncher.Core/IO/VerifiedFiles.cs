using System.Collections.Concurrent;
using System.Text.Json;

namespace MechanicaLauncher.Core.IO;

// Remembers files whose hash was already verified, keyed by full path + size + last write time.
// Without it every Play re-hashes the client, all libraries and thousands of assets (hundreds of MB).
// A file that changes on disk gets a new size or write time and is hashed again.
public static class VerifiedFiles
{
    private sealed record Entry(long Size, long WriteTicks, string Hash);

    private static readonly ConcurrentDictionary<string, Entry> Entries = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object SaveLock = new();
    private static string? storePath;
    private static int dirty;

    // Persists the index between launcher runs. Without a store the index lives only in memory.
    public static void Attach(string path)
    {
        lock (SaveLock)
        {
            storePath = path;
            try
            {
                if (!File.Exists(path)) return;
                var saved = JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(path));
                if (saved == null) return;
                foreach (var (file, entry) in saved)
                    if (!string.IsNullOrEmpty(entry.Hash)) Entries.TryAdd(file, entry);
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        }
    }

    public static void Clear()
    {
        Entries.Clear();
        Interlocked.Exchange(ref dirty, 1);
        Flush();
    }

    internal static bool IsKnown(string path, FileInfo info, string hash) =>
        Entries.TryGetValue(Path.GetFullPath(path), out var entry) &&
        entry.Size == info.Length && entry.WriteTicks == info.LastWriteTimeUtc.Ticks &&
        entry.Hash.Equals(hash, StringComparison.OrdinalIgnoreCase);

    internal static void Remember(string path, string hash)
    {
        var info = new FileInfo(path);
        if (!info.Exists) return;
        Entries[Path.GetFullPath(path)] = new Entry(info.Length, info.LastWriteTimeUtc.Ticks, hash.ToLowerInvariant());
        Interlocked.Exchange(ref dirty, 1);
    }

    internal static void Forget(string path)
    {
        if (Entries.TryRemove(Path.GetFullPath(path), out _)) Interlocked.Exchange(ref dirty, 1);
    }

    public static void Flush()
    {
        if (Interlocked.Exchange(ref dirty, 0) == 0) return;
        lock (SaveLock)
        {
            if (storePath == null) return;
            try
            {
                var snapshot = Entries.Where(e => File.Exists(e.Key)).ToDictionary(e => e.Key, e => e.Value, StringComparer.OrdinalIgnoreCase);
                AtomicFile.WriteText(storePath, JsonSerializer.Serialize(snapshot));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Interlocked.Exchange(ref dirty, 1); }
        }
    }
}
