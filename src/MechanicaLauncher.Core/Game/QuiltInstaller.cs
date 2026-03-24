using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MechanicaLauncher.Core.Game;

public sealed class QuiltInstaller
{
    private static readonly HttpClient Http = new();
    private const string MetaBase = "https://meta.quiltmc.org/v3";
    private readonly string _sharedDir;
    private readonly string _instanceGameDir;

    public QuiltInstaller(string sharedDir, string instanceGameDir)
    {
        _sharedDir = sharedDir;
        _instanceGameDir = instanceGameDir;
    }

    public event Action<string, double>? ProgressChanged;

    public async Task<List<string>> GetLoaderVersionsAsync(string mcVersion)
    {
        var url = $"{MetaBase}/versions/loader/{mcVersion}";
        var entries = await Http.GetFromJsonAsync<List<QuiltLoaderEntry>>(url);
        return entries?.Select(e => e.Loader.Version).ToList() ?? [];
    }

    public async Task InstallAsync(string mcVersion, string loaderVersion)
    {
        ProgressChanged?.Invoke("Downloading Quilt profile...", 10);

        var profileUrl = $"{MetaBase}/versions/loader/{mcVersion}/{loaderVersion}/profile/json";
        var json = await Http.GetStringAsync(profileUrl);
        var profile = JsonSerializer.Deserialize<JsonElement>(json);

        var versionId = $"quilt-loader-{loaderVersion}-{mcVersion}";
        var versionDir = Path.Combine(_instanceGameDir, "versions", versionId);
        Directory.CreateDirectory(versionDir);
        await File.WriteAllTextAsync(Path.Combine(versionDir, $"{versionId}.json"), json);

        if (profile.TryGetProperty("libraries", out var libraries))
        {
            var libArray = libraries.EnumerateArray().ToList();
            for (int i = 0; i < libArray.Count; i++)
            {
                var lib = libArray[i];
                if (!lib.TryGetProperty("name", out var name) || !lib.TryGetProperty("url", out var url))
                    continue;

                var mavenPath = MavenToPath(name.GetString()!);
                if (mavenPath == null) continue;

                var libPath = Path.Combine(_sharedDir, "libraries", mavenPath);
                if (File.Exists(libPath)) continue;

                var downloadUrl = url.GetString()!.TrimEnd('/') + "/" + mavenPath.Replace('\\', '/');
                Directory.CreateDirectory(Path.GetDirectoryName(libPath)!);

                ProgressChanged?.Invoke($"Quilt libraries ({i + 1}/{libArray.Count})", 10 + (double)(i + 1) / libArray.Count * 90);

                try
                {
                    var bytes = await Http.GetByteArrayAsync(downloadUrl);
                    await File.WriteAllBytesAsync(libPath, bytes);
                }
                catch { }
            }
        }

        ProgressChanged?.Invoke("Quilt installed!", 100);
    }

    private static string? MavenToPath(string name)
    {
        var parts = name.Split(':');
        if (parts.Length < 3) return null;
        var group = parts[0].Replace('.', Path.DirectorySeparatorChar);
        var artifact = parts[1];
        var version = parts[2];
        if (parts.Length >= 4)
            return Path.Combine(group, artifact, version, $"{artifact}-{version}-{parts[3]}.jar");
        return Path.Combine(group, artifact, version, $"{artifact}-{version}.jar");
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
