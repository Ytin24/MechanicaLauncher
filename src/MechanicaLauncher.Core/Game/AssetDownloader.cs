using System.IO.Compression;
using System.Runtime.InteropServices;
using MechanicaLauncher.Core.IO;
using MechanicaLauncher.Core.Models;

namespace MechanicaLauncher.Core.Game;

public sealed class AssetDownloader
{
    private static readonly HttpClient DefaultHttp = new();
    private readonly HttpClient _http;
    private readonly string _sharedDir;
    private readonly string _instanceGameDir;

    public AssetDownloader(string sharedDir, string instanceGameDir, HttpClient? http = null)
    {
        _sharedDir = sharedDir;
        _instanceGameDir = instanceGameDir;
        _http = http ?? DefaultHttp;
    }

    public event Action<string, double>? ProgressChanged;

    public async Task DownloadVersionAsync(VersionMeta meta, CancellationToken cancellationToken = default)
    {
        if (meta.Logging.TryGetValue("client", out var logging))
        {
            var path = FileDownloader.GetPath(_sharedDir, $"assets/log_configs/{logging.File.Id}");
            await FileDownloader.EnsureAsync(_http, logging.File.Url, path, logging.File.Sha1, logging.File.Size, cancellationToken);
        }
        if (meta.Downloads.TryGetValue("client", out var client))
        {
            var jarPath = FileDownloader.GetPath(_instanceGameDir, $"versions/{meta.Id}/{meta.Id}.jar");
            ProgressChanged?.Invoke("Checking client...", -1);
            await FileDownloader.EnsureAsync(_http, client.Url, jarPath, client.Sha1, client.Size, cancellationToken);
        }

        var librariesDir = Path.Combine(_sharedDir, "libraries");
        var libs = meta.Libraries.Where(ShouldIncludeLibrary).ToList();
        for (int i = 0; i < libs.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ProgressChanged?.Invoke($"Libraries ({i + 1}/{libs.Count})", (double)(i + 1) / libs.Count * 70);
            if (GetArtifact(libs[i]) is { } artifact)
                await DownloadArtifactAsync(artifact);
            if (GetNativeArtifact(libs[i]) is { } native)
                await DownloadArtifactAsync(native);
        }

        ExtractNatives(meta, cancellationToken);

        if (meta.AssetIndex is { } assetIndex)
        {
            var indexPath = FileDownloader.GetPath(_sharedDir, $"assets/indexes/{assetIndex.Id}.json");
            ProgressChanged?.Invoke("Checking asset index...", -1);
            await FileDownloader.EnsureAsync(_http, assetIndex.Url, indexPath, assetIndex.Sha1, assetIndex.Size, cancellationToken);

            var indexJson = await File.ReadAllTextAsync(indexPath, cancellationToken);
            var assets = System.Text.Json.JsonSerializer.Deserialize<AssetIndexData>(indexJson)
                ?? throw new InvalidDataException("Asset index is empty.");
            var objects = assets.Objects.Values.DistinctBy(o => o.Hash).ToList();
            int completed = 0;
            await Parallel.ForEachAsync(objects, new ParallelOptions
            {
                MaxDegreeOfParallelism = 6,
                CancellationToken = cancellationToken
            }, async (obj, token) =>
            {
                if (obj.Hash.Length != 40 || !obj.Hash.All(Uri.IsHexDigit))
                    throw new InvalidDataException($"Invalid asset hash: {obj.Hash}");
                var prefix = obj.Hash[..2];
                var assetPath = Path.Combine(_sharedDir, "assets", "objects", prefix, obj.Hash);
                var url = $"https://resources.download.minecraft.net/{prefix}/{obj.Hash}";
                await FileDownloader.EnsureAsync(_http, url, assetPath, obj.Hash, obj.Size, token);
                var count = Interlocked.Increment(ref completed);
                if (count % 50 == 0 || count == objects.Count)
                    ProgressChanged?.Invoke($"Assets ({count}/{objects.Count})", 70 + (double)count / objects.Count * 30);
            });
        }

        ProgressChanged?.Invoke("Done!", 100);

        Task DownloadArtifactAsync(LibraryArtifact artifact) => FileDownloader.EnsureAsync(_http,
            artifact.Url, FileDownloader.GetPath(librariesDir, artifact.Path), artifact.Sha1, artifact.Size, cancellationToken);
    }

    private void ExtractNatives(VersionMeta meta, CancellationToken cancellationToken)
    {
        var nativesDir = FileDownloader.GetPath(_instanceGameDir, $"versions/{meta.Id}/natives");
        Directory.CreateDirectory(nativesDir);
        ProgressChanged?.Invoke("Extracting natives...", 70);

        foreach (var lib in meta.Libraries.Where(ShouldIncludeLibrary))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var artifact = GetNativeArtifact(lib);
            if (artifact == null && lib.Name.Contains(":natives-windows", StringComparison.OrdinalIgnoreCase))
                artifact = GetArtifact(lib);
            if (artifact == null) continue;

            var jarPath = FileDownloader.GetPath(Path.Combine(_sharedDir, "libraries"), artifact.Path);
            using var zip = ZipFile.OpenRead(jarPath);
            foreach (var entry in zip.Entries)
            {
                if (!entry.FullName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) continue;
                entry.ExtractToFile(Path.Combine(nativesDir, entry.Name), overwrite: true);
            }
        }
    }

    public static bool ShouldIncludeLibrary(Library lib)
    {
        var classifier = lib.Name.Split(':').ElementAtOrDefault(3)?.Split('@')[0];
        var arch = RuntimeInformation.OSArchitecture;
        if (classifier is "natives-windows-arm64" or "arm64" && arch != Architecture.Arm64) return false;
        if (classifier == "natives-windows-x86" && arch != Architecture.X86) return false;
        if (classifier is "natives-windows" or "natives-windows-x86_64" && arch != Architecture.X64) return false;

        if (lib.Rules == null || lib.Rules.Count == 0)
        {
            if (lib.Name.Contains("natives-linux", StringComparison.OrdinalIgnoreCase) ||
                lib.Name.Contains("natives-macos", StringComparison.OrdinalIgnoreCase) ||
                lib.Name.Contains("natives-osx", StringComparison.OrdinalIgnoreCase) ||
                lib.Name.Contains("linux-", StringComparison.OrdinalIgnoreCase) ||
                lib.Name.Contains("macos-", StringComparison.OrdinalIgnoreCase)) return false;
        }

        return LaunchRules.Evaluate(lib.Rules);
    }

    internal static LibraryArtifact? GetArtifact(Library lib)
    {
        if (lib.Downloads != null) return lib.Downloads.Artifact;
        if (lib.Natives != null) return null;
        var path = MavenToPath(lib.Name)
            ?? throw new InvalidDataException($"Invalid library coordinates: {lib.Name}");
        return new LibraryArtifact
        {
            Path = path,
            Url = string.IsNullOrEmpty(lib.Url) ? "" : lib.Url.TrimEnd('/') + "/" + path.Replace('\\', '/')
        };
    }

    internal static LibraryArtifact? GetNativeArtifact(Library lib)
    {
        if (lib.Natives == null || !lib.Natives.TryGetValue("windows", out var classifier)) return null;
        classifier = classifier.Replace("${arch}", RuntimeInformation.OSArchitecture == Architecture.X86 ? "32" : "64");
        if (lib.Downloads?.Classifiers?.TryGetValue(classifier, out var artifact) == true) return artifact;
        throw new InvalidDataException($"Native library {classifier} is missing from {lib.Name} metadata.");
    }

    internal static string? MavenToPath(string name)
    {
        var coordinate = name.Split('@', 2);
        var parts = coordinate[0].Split(':');
        if (parts.Length is < 3 or > 4 || parts.Any(string.IsNullOrEmpty)) return null;
        var extension = coordinate.Length == 2 ? coordinate[1] : "jar";
        var classifier = parts.Length == 4 ? "-" + parts[3] : "";
        return Path.Combine(parts[0].Replace('.', Path.DirectorySeparatorChar), parts[1], parts[2],
            $"{parts[1]}-{parts[2]}{classifier}.{extension}");
    }
}
