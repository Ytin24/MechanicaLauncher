using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MechanicaLauncher.Core.Game;

public sealed class FabricInstaller
{
    private static readonly HttpClient Http = new();
    private const string MetaBase = "https://meta.fabricmc.net/v2";

    private readonly string _gameDir;

    public FabricInstaller(string gameDir)
    {
        _gameDir = gameDir;
    }

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

    public async Task InstallAsync(string mcVersion, string loaderVersion)
    {
        var profileUrl = $"{MetaBase}/versions/loader/{mcVersion}/{loaderVersion}/profile/json";
        var json = await Http.GetStringAsync(profileUrl);
        var profile = JsonSerializer.Deserialize<JsonElement>(json);

        var versionId = $"fabric-loader-{loaderVersion}-{mcVersion}";
        var versionDir = Path.Combine(_gameDir, "versions", versionId);
        Directory.CreateDirectory(versionDir);

        var versionJsonPath = Path.Combine(versionDir, $"{versionId}.json");
        await File.WriteAllTextAsync(versionJsonPath, json);

        if (profile.TryGetProperty("libraries", out var libraries))
        {
            foreach (var lib in libraries.EnumerateArray())
            {
                if (lib.TryGetProperty("name", out var name) && lib.TryGetProperty("url", out var url))
                {
                    var mavenPath = MavenToPath(name.GetString()!);
                    if (mavenPath == null) continue;

                    var libPath = Path.Combine(_gameDir, "libraries", mavenPath);
                    if (File.Exists(libPath)) continue;

                    var downloadUrl = url.GetString()!.TrimEnd('/') + "/" + mavenPath.Replace('\\', '/');
                    Directory.CreateDirectory(Path.GetDirectoryName(libPath)!);
                    try
                    {
                        var bytes = await Http.GetByteArrayAsync(downloadUrl);
                        await File.WriteAllBytesAsync(libPath, bytes);
                    }
                    catch { }
                }
            }
        }
    }

    private static string? MavenToPath(string name)
    {
        var parts = name.Split(':');
        if (parts.Length < 3) return null;
        var group = parts[0].Replace('.', Path.DirectorySeparatorChar);
        var artifact = parts[1];
        var version = parts[2];
        return Path.Combine(group, artifact, version, $"{artifact}-{version}.jar");
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
