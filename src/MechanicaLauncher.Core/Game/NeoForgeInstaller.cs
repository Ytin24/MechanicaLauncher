using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace MechanicaLauncher.Core.Game;

public sealed class NeoForgeInstaller
{
    private static readonly HttpClient DefaultHttp = new();
    private const string MavenBase = "https://maven.neoforged.net";
    private readonly string _sharedDir;
    private readonly string _instanceGameDir;
    private readonly HttpClient _http;

    public NeoForgeInstaller(string sharedDir, string instanceGameDir, HttpClient? http = null)
    {
        _sharedDir = sharedDir;
        _instanceGameDir = instanceGameDir;
        _http = http ?? DefaultHttp;
    }

    public event Action<string, double>? ProgressChanged;

    public async Task<List<string>> GetVersionsAsync(string mcVersion, CancellationToken cancellationToken = default)
    {
        bool legacy = mcVersion == "1.20.1";
        string? prefix = legacy ? "1.20.1-47.1." : VersionPrefix(mcVersion);
        if (prefix == null) return [];
        var url = $"{MavenBase}/api/maven/versions/releases/net/neoforged/{(legacy ? "forge" : "neoforge")}";
        var response = await _http.GetFromJsonAsync<NeoForgeVersionList>(url, cancellationToken);
        if (response?.Versions == null) return [];
        return response.Versions
            .Where(v => MatchesVersion(v, prefix))
            .Select(v => legacy ? v[7..] : v)
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(v => Version.Parse(v.Split('-')[0]))
            .ThenBy(v => v.Contains('-'))
            .ThenByDescending(v => v, StringComparer.Ordinal)
            .Take(20)
            .ToList();
    }

    public Task InstallAsync(string mcVersion, string neoVersion, string? javaPath = null,
        CancellationToken cancellationToken = default)
    {
        var artifact = GetInstallArtifact(mcVersion, neoVersion);
        return LoaderInstaller.InstallAsync(_http, "NeoForge", artifact.InstallerUrl, mcVersion,
            $"neoforge-{neoVersion}", _sharedDir, _instanceGameDir, javaPath, ProgressChanged, cancellationToken,
            artifact.RequiredLibrary);
    }

    internal static (string InstallerUrl, string RequiredLibrary) GetInstallArtifact(string mcVersion, string neoVersion)
    {
        if (mcVersion == "1.20.1")
        {
            string version = neoVersion.StartsWith("1.20.1-", StringComparison.Ordinal) ? neoVersion : "1.20.1-" + neoVersion;
            if (!MatchesVersion(version, "1.20.1-47.1.")) throw new InvalidDataException("NeoForge version does not match Minecraft 1.20.1.");
            string path = $"net/neoforged/forge/{version}/forge-{version}";
            return ($"{MavenBase}/releases/{path}-installer.jar", path + "-client.jar");
        }
        string? prefix = VersionPrefix(mcVersion);
        if (prefix == null || !MatchesVersion(neoVersion, prefix))
            throw new InvalidDataException($"NeoForge version does not match Minecraft {mcVersion}.");
        string required = mcVersion.StartsWith("1.", StringComparison.Ordinal)
            ? $"net/neoforged/neoforge/{neoVersion}/neoforge-{neoVersion}-client.jar"
            : $"net/neoforged/minecraft-client-patched/{neoVersion}/minecraft-client-patched-{neoVersion}.jar";
        return ($"{MavenBase}/releases/net/neoforged/neoforge/{neoVersion}/neoforge-{neoVersion}-installer.jar", required);
    }

    private static string? VersionPrefix(string mcVersion)
    {
        if (!Version.TryParse(mcVersion, out var version) || version.Revision >= 0) return null;
        int patch = Math.Max(0, version.Build);
        if (version.Major == 1 && (version.Minor > 20 || version.Minor == 20 && patch >= 2))
            return $"{version.Minor}.{patch}.";
        return version.Major >= 26 ? $"{version.Major}.{version.Minor}.{patch}." : null;
    }

    private static bool MatchesVersion(string? version, string prefix) => version != null &&
        Regex.IsMatch(version, "^" + Regex.Escape(prefix) + @"\d+(?:-[0-9A-Za-z.-]+)?$", RegexOptions.CultureInvariant) &&
        Version.TryParse((version.StartsWith("1.20.1-", StringComparison.Ordinal) ? version[7..] : version).Split('-')[0], out _);
}

file sealed class NeoForgeVersionList
{
    [JsonPropertyName("versions")]
    public List<string>? Versions { get; set; }
}
