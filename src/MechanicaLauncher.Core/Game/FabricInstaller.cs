using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using MechanicaLauncher.Core.IO;

namespace MechanicaLauncher.Core.Game;

public sealed class FabricInstaller
{
    private static readonly HttpClient DefaultHttp = new();
    private readonly HttpClient Http;
    private const string MetaBase = "https://meta.fabricmc.net/v2";
    private readonly string _sharedDir;
    private readonly string _instanceGameDir;

    public FabricInstaller(string sharedDir, string instanceGameDir, HttpClient? http = null)
    {
        _sharedDir = sharedDir;
        _instanceGameDir = instanceGameDir;
        Http = http ?? DefaultHttp;
    }

    public event Action<string, double>? ProgressChanged;

    public async Task<List<FabricLoaderVersion>> GetLoaderVersionsAsync(string mcVersion)
    {
        var url = $"{MetaBase}/versions/loader/{mcVersion}";
        var result = await Http.GetFromJsonAsync<List<FabricLoaderEntry>>(url);
        return result?.Select(e => new FabricLoaderVersion
        {
            Version = e.Loader.Version,
            Stable = e.Loader.Stable
        }).ToList() ?? [];
    }

    public async Task InstallAsync(string mcVersion, string loaderVersion, CancellationToken cancellationToken = default)
    {
        ProgressChanged?.Invoke("Downloading Fabric profile...", 10);
        var profileUrl = $"{MetaBase}/versions/loader/{mcVersion}/{loaderVersion}/profile/json";
        var json = await Http.GetStringAsync(profileUrl, cancellationToken);
        var profile = JsonSerializer.Deserialize<Models.VersionMeta>(json);
        if (profile == null || string.IsNullOrEmpty(profile.MainClass) || profile.InheritsFrom != mcVersion)
            throw new InvalidDataException("Fabric profile does not match the requested Minecraft version.");

        var versionId = $"fabric-loader-{loaderVersion}-{mcVersion}";
        var versionDir = FileDownloader.GetPath(_instanceGameDir, $"versions/{versionId}");
        var downloader = new AssetDownloader(_sharedDir, _instanceGameDir, Http);
        downloader.ProgressChanged += (status, progress) => ProgressChanged?.Invoke(status, progress);
        await downloader.DownloadVersionAsync(new Models.VersionMeta { Id = mcVersion, Libraries = profile.Libraries }, cancellationToken);

        await AtomicFile.WriteTextAsync(Path.Combine(versionDir, $"{versionId}.json"), json, cancellationToken);
        await AtomicFile.WriteTextAsync(Path.Combine(versionDir, ".complete"), "1", cancellationToken);
        ProgressChanged?.Invoke("Fabric installed!", 100);
    }
}

public sealed class FabricLoaderVersion
{
    public string Version { get; set; } = "";
    public bool Stable { get; set; }
}

file sealed class FabricLoaderEntry
{
    [JsonPropertyName("loader")]
    public FabricLoaderInfo Loader { get; set; } = new();
}

file sealed class FabricLoaderInfo
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = "";

    [JsonPropertyName("stable")]
    public bool Stable { get; set; }
}
