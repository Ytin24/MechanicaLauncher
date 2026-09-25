using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using MechanicaLauncher.Core.IO;

namespace MechanicaLauncher.Core.Game;

public sealed class QuiltInstaller
{
    private static readonly HttpClient DefaultHttp = new();
    private readonly HttpClient Http;
    private const string MetaBase = "https://meta.quiltmc.org/v3";
    private readonly string _sharedDir;
    private readonly string _instanceGameDir;

    public QuiltInstaller(string sharedDir, string instanceGameDir, HttpClient? http = null)
    {
        _sharedDir = sharedDir;
        _instanceGameDir = instanceGameDir;
        Http = http ?? DefaultHttp;
    }

    public event Action<string, double>? ProgressChanged;

    public async Task<List<string>> GetLoaderVersionsAsync(string mcVersion)
    {
        var url = $"{MetaBase}/versions/loader/{mcVersion}";
        var entries = await Http.GetFromJsonAsync<List<QuiltLoaderEntry>>(url);
        return OrderVersions(entries?.Select(e => e.Loader.Version) ?? []);
    }

    internal static List<string> OrderVersions(IEnumerable<string> versions) => versions.Distinct()
        .OrderBy(v => v.Contains('-'))
        .ThenByDescending(v => Version.TryParse(v.Split('-')[0], out var version) ? version : new Version(0, 0))
        .ThenByDescending(v => int.TryParse(v.Split('.').Last(), out var build) ? build : 0)
        .ToList();

    public async Task InstallAsync(string mcVersion, string loaderVersion, CancellationToken cancellationToken = default)
    {
        ProgressChanged?.Invoke("Downloading Quilt profile...", 10);
        var profileUrl = $"{MetaBase}/versions/loader/{mcVersion}/{loaderVersion}/profile/json";
        var json = await Http.GetStringAsync(profileUrl, cancellationToken);
        var profile = JsonSerializer.Deserialize<Models.VersionMeta>(json);
        if (profile == null || string.IsNullOrEmpty(profile.MainClass) || profile.InheritsFrom != mcVersion)
            throw new InvalidDataException("Quilt profile does not match the requested Minecraft version.");

        var versionId = $"quilt-loader-{loaderVersion}-{mcVersion}";
        var versionDir = FileDownloader.GetPath(_instanceGameDir, $"versions/{versionId}");
        var downloader = new AssetDownloader(_sharedDir, _instanceGameDir, Http);
        downloader.ProgressChanged += (status, progress) => ProgressChanged?.Invoke(status, progress);
        await downloader.DownloadVersionAsync(new Models.VersionMeta { Id = mcVersion, Libraries = profile.Libraries }, cancellationToken);

        await AtomicFile.WriteTextAsync(Path.Combine(versionDir, $"{versionId}.json"), json, cancellationToken);
        await AtomicFile.WriteTextAsync(Path.Combine(versionDir, ".complete"), "1", cancellationToken);
        ProgressChanged?.Invoke("Quilt installed!", 100);
    }
}

file sealed class QuiltLoaderEntry
{
    [JsonPropertyName("loader")]
    public QuiltLoaderInfo Loader { get; set; } = new();
}

file sealed class QuiltLoaderInfo
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = "";
}
