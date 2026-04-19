using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using MechanicaLauncher.Core.Instances;

namespace MechanicaLauncher.Core.Mods;

public sealed class ModpackInstaller
{
    private static readonly HttpClient Http = new();

    public event Action<string, double>? ProgressChanged;

    // Exports an instance to a Modrinth .mrpack. Config/saves/mods all shipped as overrides —
    // files go in as-is without Modrinth hashes because the user may have mods from other sources.
    public static async Task ExportAsync(GameInstance inst, InstanceManager im, string outputPath)
    {
        var gameDir = im.GetGameDir(inst.Id);
        if (!Directory.Exists(gameDir))
            throw new DirectoryNotFoundException($"Instance folder missing: {gameDir}");

        var deps = new Dictionary<string, string> { ["minecraft"] = inst.McVersion };
        if (!string.IsNullOrEmpty(inst.LoaderVersion))
        {
            switch (inst.Loader)
            {
                case LoaderType.NeoForge:     deps["neoforge"]      = inst.LoaderVersion; break;
                case LoaderType.Forge:        deps["forge"]         = inst.LoaderVersion; break;
                case LoaderType.Fabric:       deps["fabric-loader"] = inst.LoaderVersion; break;
                case LoaderType.Quilt:        deps["quilt-loader"]  = inst.LoaderVersion; break;
            }
        }

        var index = new
        {
            formatVersion = 1,
            game = "minecraft",
            versionId = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"),
            name = inst.Name,
            files = Array.Empty<object>(),
            dependencies = deps,
        };
        var indexJson = JsonSerializer.Serialize(index, new JsonSerializerOptions { WriteIndented = true });

        if (File.Exists(outputPath)) File.Delete(outputPath);
        using var archive = ZipFile.Open(outputPath, ZipArchiveMode.Create);

        // modrinth.index.json first
        var indexEntry = archive.CreateEntry("modrinth.index.json");
        using (var w = new StreamWriter(indexEntry.Open()))
            await w.WriteAsync(indexJson);

        // Ship the instance icon at the standard root location so other launchers can pick it up.
        var iconAbs = im.GetIconAbsolutePath(inst);
        if (iconAbs != null)
        {
            var iconExt = Path.GetExtension(iconAbs).ToLowerInvariant();
            var iconEntryName = iconExt == ".jpg" || iconExt == ".jpeg" ? "icon.jpg" : "icon.png";
            archive.CreateEntryFromFile(iconAbs, iconEntryName, System.IO.Compression.CompressionLevel.Optimal);
        }

        // Only ship user-editable directories as overrides; skip versions/libraries/natives/logs
        // which are regenerated on first launch.
        string[] include = ["mods", "config", "resourcepacks", "shaderpacks", "saves", "options.txt", "servers.dat"];
        foreach (var rel in include)
        {
            var abs = Path.Combine(gameDir, rel);
            if (File.Exists(abs))
            {
                archive.CreateEntryFromFile(abs, $"overrides/{rel}", System.IO.Compression.CompressionLevel.Optimal);
            }
            else if (Directory.Exists(abs))
            {
                foreach (var file in Directory.EnumerateFiles(abs, "*", SearchOption.AllDirectories))
                {
                    var entryPath = "overrides/" + Path.GetRelativePath(gameDir, file).Replace('\\', '/');
                    archive.CreateEntryFromFile(file, entryPath, System.IO.Compression.CompressionLevel.Optimal);
                }
            }
        }
    }

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

        // Pull icon if the mrpack ships one. Modrinth's packs commonly include pack.png / icon.png
        // at archive root, or inside overrides/.
        foreach (var candidate in new[] { "icon.png", "pack.png", "overrides/icon.png", "overrides/pack.png" })
        {
            var entry = zip.GetEntry(candidate);
            if (entry == null) continue;
            var ext = Path.GetExtension(candidate);
            var iconDst = Path.Combine(im.GetInstanceDir(inst.Id), "icon" + ext);
            try
            {
                using var src = entry.Open();
                using var fs = File.Create(iconDst);
                await src.CopyToAsync(fs);
                inst.IconPath = Path.GetFileName(iconDst);
                im.SaveInstance(inst);
            }
            catch { }
            break;
        }

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
