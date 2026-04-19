using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using MechanicaLauncher.Core.Instances;

namespace MechanicaLauncher.Core.Mods;

public sealed class ModpackInstaller
{
    private static readonly HttpClient Http = new();

    public event Action<string, double>? ProgressChanged;

    public async Task<GameInstance> ImportAsync(string mrpackPath, InstanceManager im)
    {
        using var zip = ZipFile.OpenRead(mrpackPath);
        var indexEntry = zip.GetEntry("modrinth.index.json")
            ?? throw new Exception("Invalid mrpack: missing modrinth.index.json");

        MrpackIndex index;
        using (var stream = indexEntry.Open())
            index = await JsonSerializer.DeserializeAsync<MrpackIndex>(stream)
                ?? throw new Exception("Invalid mrpack: empty index");

        if (!index.Dependencies.TryGetValue("minecraft", out var mcVersion) || string.IsNullOrEmpty(mcVersion))
            throw new Exception("Modpack manifest missing minecraft dependency");

        // Modrinth dependency keys map to our loader enum.
        LoaderType loader = LoaderType.None;
        string? loaderVersion = null;
        foreach (var (key, ver) in index.Dependencies)
        {
            switch (key)
            {
                case "neoforge":       loader = LoaderType.NeoForge; loaderVersion = ver; break;
                case "forge":          loader = LoaderType.Forge;    loaderVersion = ver; break;
                case "fabric-loader":  loader = LoaderType.Fabric;   loaderVersion = ver; break;
                case "quilt-loader":   loader = LoaderType.Quilt;    loaderVersion = ver; break;
            }
        }

        var instanceName = !string.IsNullOrEmpty(index.Name) ? index.Name : Path.GetFileNameWithoutExtension(mrpackPath);

        // Raise later — don't reveal the instance on Home until files are on disk.
        var inst = im.CreateInstance(instanceName, mcVersion, loader, loaderVersion, raiseChangedEvent: false);
        await InstallAsync(mrpackPath, im.GetGameDir(inst.Id));
        im.NotifyChanged();
        return inst;
    }

    public async Task InstallAsync(string mrpackPath, string gameDir)
    {
        using var zip = ZipFile.OpenRead(mrpackPath);

        var indexEntry = zip.GetEntry("modrinth.index.json")
            ?? throw new Exception("Invalid mrpack: missing modrinth.index.json");

        MrpackIndex index;
        using (var stream = indexEntry.Open())
        {
            index = await JsonSerializer.DeserializeAsync<MrpackIndex>(stream) ?? new();
        }

        ProgressChanged?.Invoke($"Installing {index.Name}...", 0);

        // Download files from manifest
        var total = index.Files.Count;
        for (int i = 0; i < total; i++)
        {
            var file = index.Files[i];
            var dest = Path.Combine(gameDir, file.Path.Replace('/', Path.DirectorySeparatorChar));

            if (File.Exists(dest)) continue;
            if (file.Downloads.Count == 0) continue;

            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);

            var progress = (double)(i + 1) / total * 90;
            if (i % 10 == 0)
                ProgressChanged?.Invoke($"Downloading ({i + 1}/{total}): {Path.GetFileName(file.Path)}", progress);

            try
            {
                var bytes = await Http.GetByteArrayAsync(file.Downloads[0]);
                await File.WriteAllBytesAsync(dest, bytes);
            }
            catch { }
        }

        // Extract overrides
        ProgressChanged?.Invoke("Extracting configs...", 92);
        foreach (var entry in zip.Entries)
        {
            if (!entry.FullName.StartsWith("overrides/") || string.IsNullOrEmpty(entry.Name))
                continue;

            var relativePath = entry.FullName["overrides/".Length..];
            var dest = Path.Combine(gameDir, relativePath.Replace('/', Path.DirectorySeparatorChar));

            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            if (!File.Exists(dest))
                entry.ExtractToFile(dest, false);
        }

        // Also handle client-overrides
        foreach (var entry in zip.Entries)
        {
            if (!entry.FullName.StartsWith("client-overrides/") || string.IsNullOrEmpty(entry.Name))
                continue;

            var relativePath = entry.FullName["client-overrides/".Length..];
            var dest = Path.Combine(gameDir, relativePath.Replace('/', Path.DirectorySeparatorChar));

            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            if (!File.Exists(dest))
                entry.ExtractToFile(dest, false);
        }

        ProgressChanged?.Invoke("Modpack installed!", 100);
    }
}

file sealed class MrpackIndex
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("versionId")]
    public string VersionId { get; set; } = "";

    [JsonPropertyName("files")]
    public List<MrpackFile> Files { get; set; } = [];

    [JsonPropertyName("dependencies")]
    public Dictionary<string, string> Dependencies { get; set; } = [];
}

file sealed class MrpackFile
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    [JsonPropertyName("downloads")]
    public List<string> Downloads { get; set; } = [];

    [JsonPropertyName("fileSize")]
    public long FileSize { get; set; }
}
