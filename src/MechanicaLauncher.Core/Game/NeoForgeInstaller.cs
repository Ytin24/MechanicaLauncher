using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace MechanicaLauncher.Core.Game;

public sealed class NeoForgeInstaller
{
    private static readonly HttpClient Http = new();
    private const string MavenBase = "https://maven.neoforged.net";
    private readonly string _sharedDir;
    private readonly string _instanceGameDir;

    public NeoForgeInstaller(string sharedDir, string instanceGameDir)
    {
        _sharedDir = sharedDir;
        _instanceGameDir = instanceGameDir;
    }

    public event Action<string, double>? ProgressChanged;

    public async Task<List<string>> GetVersionsAsync(string mcVersion)
    {
        var url = $"{MavenBase}/api/maven/versions/releases/net/neoforged/neoforge";
        var response = await Http.GetFromJsonAsync<NeoForgeVersionList>(url);
        if (response?.Versions == null) return [];

        // NeoForge artifact version is "<minor>.<patch>.<build>" derived from MC "1.<minor>.<patch>".
        // For bare "1.X" the patch component is implicitly 0 — must pin that so we don't pick up
        // 21.1.* / 21.2.* loaders when the instance targets 1.21 specifically.
        var mcMinor = mcVersion.StartsWith("1.") ? mcVersion[2..] : mcVersion;
        if (!mcMinor.Contains('.')) mcMinor += ".0";
        var prefix = mcMinor + ".";

        return response.Versions
            .Where(v => v.StartsWith(prefix))
            .Reverse()
            .Take(20)
            .ToList();
    }

    public Task InstallAsync(string mcVersion, string neoVersion, string? javaPath = null,
        CancellationToken cancellationToken = default)
    {
        var installerUrl = $"{MavenBase}/releases/net/neoforged/neoforge/{neoVersion}/neoforge-{neoVersion}-installer.jar";
        return LoaderInstaller.InstallAsync(Http, "NeoForge", installerUrl, mcVersion,
            $"neoforge-{neoVersion}", _sharedDir, _instanceGameDir, javaPath, ProgressChanged, cancellationToken,
            $"net/neoforged/neoforge/{neoVersion}/neoforge-{neoVersion}-client.jar");
    }
}

file sealed class NeoForgeVersionList
{
    [JsonPropertyName("versions")]
    public List<string>? Versions { get; set; }
}
