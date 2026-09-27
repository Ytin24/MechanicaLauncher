using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using MechanicaLauncher.Core.Instances;
using MechanicaLauncher.Core.IO;

namespace MechanicaLauncher.Core.Mods;

public sealed class ModpackInstaller
{
    private const string ImportedFilesName = "modpack-files.json";
    private static readonly HttpClient DefaultHttp = new();
    private readonly HttpClient Http;

    public ModpackInstaller(HttpClient? http = null) => Http = http ?? DefaultHttp;

    public event Action<string, double>? ProgressChanged;

    // Exports an instance to a Modrinth .mrpack. Config/saves/mods all shipped as overrides —
    // files go in as-is without Modrinth hashes because the user may have mods from other sources.
    public static async Task ExportAsync(GameInstance inst, InstanceManager im, string outputPath, CancellationToken cancellationToken = default)
    {
        outputPath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        var temporary = outputPath + "." + Guid.NewGuid().ToString("N") + ".part";
        try
        {
            await ExportToFileAsync(inst, im, temporary, outputPath, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, outputPath, overwrite: true);
        }
        finally { AtomicFile.TryDelete(temporary); }
    }

    private static async Task ExportToFileAsync(GameInstance inst, InstanceManager im, string outputPath, string excludedPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(inst.McVersion) || !Enum.IsDefined(inst.Loader))
            throw new InvalidOperationException("Select a Minecraft version and a supported mod loader before exporting.");
        if (inst.Loader != LoaderType.None && string.IsNullOrWhiteSpace(inst.LoaderVersion))
            throw new InvalidOperationException("Select a mod loader version before exporting.");
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

        using var archive = ZipFile.Open(outputPath, ZipArchiveMode.Create);

        // modrinth.index.json first
        var indexEntry = archive.CreateEntry("modrinth.index.json");
        using (var w = new StreamWriter(indexEntry.Open()))
            await w.WriteAsync(indexJson.AsMemory(), cancellationToken);

        // Ship the instance icon at the standard root location so other launchers can pick it up.
        var iconAbs = im.GetIconAbsolutePath(inst);
        if (iconAbs != null)
        {
            var iconExt = Path.GetExtension(iconAbs).ToLowerInvariant();
            var iconEntryName = iconExt == ".jpg" || iconExt == ".jpeg" ? "icon.jpg" : "icon.png";
            await AddFileAsync(iconAbs, iconEntryName);
        }

        // Only ship user-editable directories as overrides; skip versions/libraries/natives/logs
        // which are regenerated on first launch.
        var included = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string[] include = ["mods", "config", "resourcepacks", "shaderpacks", "saves", "kubejs", "scripts", "defaultconfigs", "options.txt", "servers.dat"];
        foreach (var rel in include)
        {
            var abs = Path.Combine(gameDir, rel);
            if (File.Exists(abs))
            {
                await AddOverrideAsync(abs);
            }
            else if (Directory.Exists(abs))
            {
                foreach (var file in Directory.EnumerateFiles(abs, "*", SearchOption.AllDirectories))
                {
                    await AddOverrideAsync(file);
                }
            }
        }
        var importedFiles = Path.Combine(im.GetInstanceDir(inst.Id), ImportedFilesName);
        if (File.Exists(importedFiles))
        {
            var paths = JsonSerializer.Deserialize<string[]>(await File.ReadAllTextAsync(importedFiles, cancellationToken))
                ?? throw new InvalidDataException("Imported modpack file list is empty.");
            foreach (var relative in paths)
            {
                var file = FileDownloader.GetPath(gameDir, relative);
                if (File.Exists(file)) await AddOverrideAsync(file);
            }
        }

        async Task AddOverrideAsync(string file)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fullPath = Path.GetFullPath(file);
            if (IsPrivateContentPath(gameDir, fullPath) || fullPath.Equals(outputPath, StringComparison.OrdinalIgnoreCase) || fullPath.Equals(excludedPath, StringComparison.OrdinalIgnoreCase) || !included.Add(fullPath)) return;
            await AddFileAsync(fullPath, "overrides/" + Path.GetRelativePath(gameDir, fullPath).Replace('\\', '/'));
        }

        async Task AddFileAsync(string file, string entryName)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsPrivateContentPath(gameDir, file)) return;
            await using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var output = archive.CreateEntry(entryName, CompressionLevel.Optimal).Open();
            await input.CopyToAsync(output, cancellationToken);
        }
    }

    public async Task<GameInstance> ImportAsync(string mrpackPath, InstanceManager im, CancellationToken cancellationToken = default)
    {
        using var zip = ZipFile.OpenRead(mrpackPath);
        var indexEntry = zip.GetEntry("modrinth.index.json")
            ?? throw new Exception("Invalid mrpack: missing modrinth.index.json");

        MrpackIndex index;
        using (var stream = indexEntry.Open())
            index = await JsonSerializer.DeserializeAsync<MrpackIndex>(stream, cancellationToken: cancellationToken)
                ?? throw new Exception("Invalid mrpack: empty index");

        ValidateIndex(index);
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

        var stagingRoot = Path.Combine(im.InstancesDir, ".imports", Guid.NewGuid().ToString("N"));
        var staging = new InstanceManager(stagingRoot);
        try
        {
            var inst = staging.CreateInstance(instanceName, mcVersion, loader, loaderVersion, raiseChangedEvent: false);
            await InstallAsync(mrpackPath, staging.GetGameDir(inst.Id), cancellationToken);
            var gameDir = staging.GetGameDir(inst.Id);
            var importedFiles = Directory.EnumerateFiles(gameDir, "*", SearchOption.AllDirectories)
                .Select(file => Path.GetRelativePath(gameDir, file).Replace('\\', '/')).ToArray();
            await File.WriteAllTextAsync(Path.Combine(staging.GetInstanceDir(inst.Id), ImportedFilesName), JsonSerializer.Serialize(importedFiles), cancellationToken);

            foreach (var candidate in new[] { "icon.png", "icon.jpg", "pack.png", "overrides/icon.png", "overrides/pack.png" })
            {
                var entry = zip.GetEntry(candidate);
                if (entry == null) continue;
                var ext = Path.GetExtension(candidate);
                var iconDst = Path.Combine(staging.GetInstanceDir(inst.Id), "icon" + ext);
                using var src = entry.Open();
                using var fs = File.Create(iconDst);
                await src.CopyToAsync(fs, cancellationToken);
                inst.IconPath = Path.GetFileName(iconDst);
                break;
            }

            var stagedDir = staging.GetInstanceDir(inst.Id);
            if (Directory.Exists(im.GetInstanceDir(inst.Id))) inst.Id += "-" + Guid.NewGuid().ToString("N")[..8];
            await File.WriteAllTextAsync(Path.Combine(stagedDir, "instance.json"),
                JsonSerializer.Serialize(inst, new JsonSerializerOptions { WriteIndented = true }), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(stagedDir, im.GetInstanceDir(inst.Id));
            im.NotifyChanged();
            return inst;
        }
        finally
        {
            if (Directory.Exists(stagingRoot)) Directory.Delete(stagingRoot, recursive: true);
        }
    }

    public async Task InstallAsync(string mrpackPath, string gameDir, CancellationToken cancellationToken = default)
    {
        var staging = Path.Combine(gameDir, ".pack-" + Guid.NewGuid().ToString("N"));
        try
        {
            var index = await InstallToDirectoryAsync(mrpackPath, staging, cancellationToken);
            var downloads = index.Files.Where(f => f.Env?.GetValueOrDefault("client") != "unsupported")
                .ToDictionary(f => GetContentPath(gameDir, f.Path), StringComparer.OrdinalIgnoreCase);
            foreach (var source in Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var dest = GetContentPath(gameDir, Path.GetRelativePath(staging, source));
                if (File.Exists(dest))
                {
                    if (!downloads.TryGetValue(dest, out var file)) continue;
                    if (await FileDownloader.IsValidAsync(dest, file.Hashes["sha1"], file.FileSize, cancellationToken, file.Hashes["sha512"])) continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Move(source, dest, overwrite: true);
            }
            ProgressChanged?.Invoke("Modpack installed!", 100);
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }

    private async Task<MrpackIndex> InstallToDirectoryAsync(string mrpackPath, string gameDir, CancellationToken cancellationToken)
    {
        using var zip = ZipFile.OpenRead(mrpackPath);

        var indexEntry = zip.GetEntry("modrinth.index.json")
            ?? throw new Exception("Invalid mrpack: missing modrinth.index.json");

        MrpackIndex index;
        using (var stream = indexEntry.Open())
        {
            index = await JsonSerializer.DeserializeAsync<MrpackIndex>(stream, cancellationToken: cancellationToken)
                ?? throw new InvalidDataException("Modpack index is empty.");
        }
        ValidateIndex(index);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in index.Files)
        {
            var dest = GetContentPath(gameDir, file.Path);
            if (!seen.Add(dest)) throw new InvalidDataException($"Duplicate modpack file: {file.Path}");
            if (file.Env?.GetValueOrDefault("client") == "unsupported") continue;
            if (!file.Hashes.TryGetValue("sha1", out var sha1) || sha1.Length != 40 || !sha1.All(Uri.IsHexDigit) ||
                !file.Hashes.TryGetValue("sha512", out var sha512) || sha512.Length != 128 || !sha512.All(Uri.IsHexDigit))
                throw new InvalidDataException($"Modpack file has invalid hashes: {file.Path}");
            if (file.Downloads.Count == 0 || file.Downloads.Any(url => !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https"))
                throw new InvalidDataException($"Modpack file requires an HTTPS download: {file.Path}");
        }
        foreach (var entry in zip.Entries.Where(e => !string.IsNullOrEmpty(e.Name)))
        {
            var prefix = entry.FullName.StartsWith("overrides/") ? "overrides/" :
                entry.FullName.StartsWith("client-overrides/") ? "client-overrides/" : null;
            if (prefix != null) _ = GetContentPath(gameDir, entry.FullName[prefix.Length..]);
        }

        Directory.CreateDirectory(gameDir);
        ProgressChanged?.Invoke($"Installing {index.Name}...", 0);

        // Download files from manifest
        var total = index.Files.Count;
        for (int i = 0; i < total; i++)
        {
            var file = index.Files[i];
            if (file.Env?.GetValueOrDefault("client") == "unsupported") continue;
            var dest = GetContentPath(gameDir, file.Path);

            var progress = (double)(i + 1) / total * 90;
            if (i % 10 == 0)
                ProgressChanged?.Invoke($"Downloading ({i + 1}/{total}): {Path.GetFileName(file.Path)}", progress);

            Exception? failure = null;
            foreach (var url in file.Downloads)
            {
                try
                {
                    await FileDownloader.EnsureAsync(Http, url, dest, file.Hashes["sha1"], file.FileSize,
                        cancellationToken, file.Hashes["sha512"]);
                    failure = null;
                    break;
                }
                catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
                {
                    failure = ex;
                }
            }
            if (failure != null) throw new IOException($"Could not install required modpack file: {file.Path}", failure);
        }

        // Extract overrides
        ProgressChanged?.Invoke("Extracting configs...", 92);
        foreach (var entry in zip.Entries)
        {
            if (!entry.FullName.StartsWith("overrides/") || string.IsNullOrEmpty(entry.Name))
                continue;

            var relativePath = entry.FullName["overrides/".Length..];
            var dest = GetContentPath(gameDir, relativePath);

            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            cancellationToken.ThrowIfCancellationRequested();
            Extract(entry, dest);
        }

        // Also handle client-overrides
        foreach (var entry in zip.Entries)
        {
            if (!entry.FullName.StartsWith("client-overrides/") || string.IsNullOrEmpty(entry.Name))
                continue;

            var relativePath = entry.FullName["client-overrides/".Length..];
            var dest = GetContentPath(gameDir, relativePath);

            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            cancellationToken.ThrowIfCancellationRequested();
            Extract(entry, dest);
        }

        return index;
    }

    private static string GetContentPath(string root, string relative)
    {
        var path = FileDownloader.GetPath(root, relative);
        if (IsPrivateContentPath(root, path)) throw new InvalidDataException("Modpacks cannot contain launcher state: " + relative);
        return path;
    }

    private static bool IsPrivateContentPath(string root, string path)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
        var first = relative.Split(['/', '\\'])[0].TrimEnd(' ', '.');
        return first.Equals(".mechanica", StringComparison.OrdinalIgnoreCase);
    }

    private static void Extract(ZipArchiveEntry entry, string path)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".part";
        try
        {
            entry.ExtractToFile(temporary);
            File.Move(temporary, path, overwrite: true);
        }
        finally { AtomicFile.TryDelete(temporary); }
    }

    private static void ValidateIndex(MrpackIndex index)
    {
        if (index.FormatVersion != 1 || index.Game != "minecraft")
            throw new InvalidDataException("Unsupported modpack format or game.");
        var supported = new[] { "minecraft", "fabric-loader", "quilt-loader", "forge", "neoforge" };
        if (!index.Dependencies.TryGetValue("minecraft", out var minecraft) || string.IsNullOrWhiteSpace(minecraft) ||
            index.Dependencies.Any(d => !supported.Contains(d.Key) || string.IsNullOrWhiteSpace(d.Value)) || index.Dependencies.Count > 2)
            throw new InvalidDataException("Modpack dependencies are missing, unsupported, or contain conflicting loaders.");
    }
}

internal sealed class MrpackIndex
{
    [JsonPropertyName("formatVersion")]
    public int FormatVersion { get; set; }

    [JsonPropertyName("game")]
    public string Game { get; set; } = "";
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("versionId")]
    public string VersionId { get; set; } = "";

    [JsonPropertyName("files")]
    public List<MrpackFile> Files { get; set; } = [];

    [JsonPropertyName("dependencies")]
    public Dictionary<string, string> Dependencies { get; set; } = [];
}

internal sealed class MrpackFile
{
    [JsonPropertyName("hashes")]
    public Dictionary<string, string> Hashes { get; set; } = [];

    [JsonPropertyName("env")]
    public Dictionary<string, string>? Env { get; set; }
    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    [JsonPropertyName("downloads")]
    public List<string> Downloads { get; set; } = [];

    [JsonPropertyName("fileSize")]
    public long FileSize { get; set; }
}
