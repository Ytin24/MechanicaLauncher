using MechanicaLauncher.Core.Models;
using MechanicaLauncher.Core.IO;
using MechanicaLauncher.Core.Instances;

namespace MechanicaLauncher.Core.Mods;

public sealed class ModInstaller
{
    private static readonly HttpClient DefaultHttp = new();
    private readonly HttpClient Http;
    private readonly ModrinthClient _client;

    public ModInstaller(HttpClient? http = null, ModrinthClient? client = null)
    {
        Http = http ?? DefaultHttp;
        _client = client ?? new();
    }

    public event Action<string>? StatusChanged;

    public Task InstallModAsync(ModrinthVersion version, string modsDir,
                                       string? mcVersion = null, string? loader = null,
                                       string? gameDir = null, CancellationToken cancellationToken = default)
        => InstallFilesAsync(version, modsDir, ".jar", mcVersion, loader, cancellationToken);

    public Task InstallContentAsync(ModrinthVersion version, string gameDir, string contentType,
        string mcVersion, string? loader = null, string? worldName = null, CancellationToken cancellationToken = default)
    {
        if (contentType == "mod" && string.IsNullOrEmpty(loader))
            throw new InvalidOperationException("Select an instance with a mod loader before installing mods.");
        var directory = GetContentDirectory(gameDir, contentType, worldName);
        return InstallFilesAsync(version, directory, contentType == "mod" ? ".jar" : ".zip", mcVersion,
            contentType == "datapack" ? "datapack" : contentType == "mod" ? loader : null, cancellationToken);
    }

    public static string GetContentDirectory(string gameDir, string contentType, string? worldName = null)
    {
        var folder = contentType switch
        {
            "mod" => "mods",
            "shader" => "shaderpacks",
            "resourcepack" => "resourcepacks",
            "datapack" => null,
            _ => throw new ArgumentException("Unsupported content type.", nameof(contentType))
        };
        if (folder != null) return FileDownloader.GetPath(gameDir, folder);
        if (string.IsNullOrWhiteSpace(worldName) || Path.GetFileName(worldName) != worldName)
            throw new ArgumentException("Select an existing world.", nameof(worldName));
        var world = FileDownloader.GetPath(Path.Combine(gameDir, "saves"), worldName);
        if (!File.Exists(Path.Combine(world, "level.dat"))) throw new DirectoryNotFoundException("The selected world no longer exists.");
        return FileDownloader.GetPath(world, "datapacks");
    }

    public async Task<GameInstance> ImportModpackAsync(ModrinthVersion version, InstanceManager instances,
        CancellationToken cancellationToken = default)
    {
        var file = SelectFile(version, ".mrpack");
        var path = Path.Combine(Path.GetTempPath(), "mechanica-" + Guid.NewGuid().ToString("N") + ".mrpack");
        try
        {
            StatusChanged?.Invoke(file.Filename);
            await DownloadAsync(file, path, cancellationToken);
            var installer = new ModpackInstaller(Http);
            installer.ProgressChanged += (message, _) => StatusChanged?.Invoke(message);
            return await installer.ImportAsync(path, instances, cancellationToken);
        }
        finally { AtomicFile.TryDelete(path); }
    }

    public static ModrinthFile SelectFile(ModrinthVersion version, string extension) =>
        version.Files.FirstOrDefault(f => f.Primary && f.Filename.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
        ?? version.Files.FirstOrDefault(f => f.Filename.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidDataException($"No {extension} file found for {version.Name}.");

    private async Task InstallFilesAsync(ModrinthVersion version, string modsDir, string extension,
        string? mcVersion, string? loader, CancellationToken cancellationToken)
    {
        var selected = new Dictionary<string, string>(StringComparer.Ordinal);
        var files = new List<ModrinthFile>();
        await ResolveAsync(version);
        var destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
            if (!destinations.Add(FileDownloader.GetPath(modsDir, file.Filename)))
                throw new InvalidDataException($"Dependencies contain conflicting filenames: {file.Filename}");

        var staging = Path.Combine(modsDir, ".install-" + Guid.NewGuid().ToString("N"));
        try
        {
            foreach (var file in files)
            {
                var dest = FileDownloader.GetPath(modsDir, file.Filename);
                if (await FileDownloader.IsValidAsync(dest, file.Hashes.GetValueOrDefault("sha1"), file.Size,
                    cancellationToken, file.Hashes.GetValueOrDefault("sha512"))) continue;
                StatusChanged?.Invoke(file.Filename);
                await DownloadAsync(file, FileDownloader.GetPath(staging, file.Filename), cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var file in files)
            {
                var staged = FileDownloader.GetPath(staging, file.Filename);
                if (File.Exists(staged)) File.Move(staged, FileDownloader.GetPath(modsDir, file.Filename), overwrite: true);
            }
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); }

        async Task ResolveAsync(ModrinthVersion current)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = current.ProjectId ?? current.Id;
            if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(current.Id)) throw new InvalidDataException("Mod version identity is missing.");
            if (selected.TryGetValue(key, out var previous))
            {
                if (previous != current.Id) throw new InvalidDataException($"Required dependencies select conflicting versions of {key}.");
                return;
            }
            selected.Add(key, current.Id);
            if ((mcVersion != null && current.GameVersions.Count > 0 && !current.GameVersions.Contains(mcVersion)) ||
                (loader != null && current.Loaders.Count > 0 && !current.Loaders.Contains(loader)))
                throw new InvalidDataException($"Required mod version is incompatible: {current.Name} ({current.Id}).");
            foreach (var dep in current.Dependencies.Where(d => d.DependencyType == "required"))
            {
                ModrinthVersion? dependency;
                if (!string.IsNullOrEmpty(dep.VersionId))
                    dependency = await _client.GetVersionAsync(dep.VersionId, cancellationToken);
                else if (!string.IsNullOrEmpty(dep.ProjectId))
                    dependency = (await _client.GetProjectVersionsAsync(dep.ProjectId, mcVersion, loader, cancellationToken)).FirstOrDefault();
                else throw new InvalidDataException("Required dependency has no project or version ID.");
                if (dependency == null) throw new InvalidDataException($"Required dependency is unavailable: {dep.VersionId ?? dep.ProjectId}.");
                await ResolveAsync(dependency);
            }
            var file = SelectFile(current, extension);
            if (Path.GetFileName(file.Filename) != file.Filename) throw new InvalidDataException("Mod filename must not contain a directory.");
            files.Add(file);
        }
    }

    private Task DownloadAsync(ModrinthFile file, string path, CancellationToken cancellationToken) =>
        FileDownloader.EnsureAsync(Http, file.Url, path, file.Hashes.GetValueOrDefault("sha1"), file.Size,
            cancellationToken, file.Hashes.GetValueOrDefault("sha512"));

    public static List<InstalledMod> GetInstalledMods(string modsDir, string extension = ".jar")
    {
        if (!Directory.Exists(modsDir)) return [];

        var result = new List<InstalledMod>();
        foreach (var file in Directory.GetFiles(modsDir, "*" + extension))
        {
            result.Add(new InstalledMod
            {
                FileName = Path.GetFileName(file),
                FilePath = file,
                Enabled = true,
                SizeBytes = new FileInfo(file).Length
            });
        }
        foreach (var file in Directory.GetFiles(modsDir, "*" + extension + ".disabled"))
        {
            result.Add(new InstalledMod
            {
                FileName = Path.GetFileNameWithoutExtension(file),
                FilePath = file,
                Enabled = false,
                SizeBytes = new FileInfo(file).Length
            });
        }
        return result.OrderBy(m => m.FileName).ToList();
    }

    public static void ToggleMod(string modPath)
    {
        if (modPath.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase) || modPath.EndsWith(".zip.disabled", StringComparison.OrdinalIgnoreCase))
            File.Move(modPath, modPath[..^".disabled".Length]);
        else if (modPath.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) || modPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            File.Move(modPath, modPath + ".disabled");
    }

    public static void RemoveMod(string modPath)
    {
        if (File.Exists(modPath)) File.Delete(modPath);
    }
}

public sealed class InstalledMod
{
    public string FileName { get; set; } = "";
    public string FilePath { get; set; } = "";
    public bool Enabled { get; set; }
    public long SizeBytes { get; set; }

    public string SizeFormatted => SizeBytes switch
    {
        >= 1_048_576 => $"{SizeBytes / 1_048_576.0:0.#} MB",
        >= 1024 => $"{SizeBytes / 1024.0:0.#} KB",
        _ => $"{SizeBytes} B"
    };
}
