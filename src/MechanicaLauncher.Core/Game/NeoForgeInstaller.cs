using System.IO.Compression;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MechanicaLauncher.Core.Game;

public sealed class NeoForgeInstaller
{
    private static readonly HttpClient Http = new();
    private const string ApiBase = "https://maven.neoforged.net";
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
        var url = $"{ApiBase}/api/maven/versions/releases/net/neoforged/neoforge";
        var response = await Http.GetFromJsonAsync<NeoForgeVersionList>(url);
        if (response?.Versions == null) return [];

        var mcMinor = mcVersion.Replace("1.", "");

        return response.Versions
            .Where(v => v.StartsWith(mcMinor + ".") || v.StartsWith(mcMinor + "-"))
            .Reverse()
            .Take(20)
            .ToList();
    }

    public async Task InstallAsync(string mcVersion, string neoVersion)
    {
        ProgressChanged?.Invoke("Downloading NeoForge installer...", 10);

        var installerUrl = $"{ApiBase}/releases/net/neoforged/neoforge/{neoVersion}/neoforge-{neoVersion}-installer.jar";
        var tmpPath = Path.Combine(Path.GetTempPath(), $"neoforge-{neoVersion}-installer.jar");
        var bytes = await Http.GetByteArrayAsync(installerUrl);
        await File.WriteAllBytesAsync(tmpPath, bytes);

        ProgressChanged?.Invoke("Extracting version profile...", 30);

        var versionId = $"neoforge-{neoVersion}";
        var versionDir = Path.Combine(_instanceGameDir, "versions", versionId);
        Directory.CreateDirectory(versionDir);

        using (var zip = ZipFile.OpenRead(tmpPath))
        {
            var versionJson = zip.GetEntry("version.json");
            if (versionJson != null)
            {
                using var stream = versionJson.Open();
                using var reader = new StreamReader(stream);
                var json = await reader.ReadToEndAsync();
                await File.WriteAllTextAsync(Path.Combine(versionDir, $"{versionId}.json"), json);
            }

            var installProfile = zip.GetEntry("install_profile.json");
            if (installProfile != null)
            {
                using var stream = installProfile.Open();
                var profile = await JsonSerializer.DeserializeAsync<JsonElement>(stream);

                if (profile.TryGetProperty("libraries", out var libs))
                    await DownloadLibrariesAsync(libs);
            }
        }

        try { File.Delete(tmpPath); } catch { }
        ProgressChanged?.Invoke("NeoForge installed!", 100);
    }

    private async Task DownloadLibrariesAsync(JsonElement libs)
    {
        var libArray = libs.EnumerateArray().ToList();
        for (int i = 0; i < libArray.Count; i++)
        {
            var lib = libArray[i];
            if (!lib.TryGetProperty("name", out var nameProp)) continue;
            var name = nameProp.GetString();
            if (name == null) continue;

            var mavenPath = MavenToPath(name);
            if (mavenPath == null) continue;

            var libPath = Path.Combine(_sharedDir, "libraries", mavenPath);
            if (File.Exists(libPath)) continue;

            string? url = null;
            if (lib.TryGetProperty("downloads", out var downloads) &&
                downloads.TryGetProperty("artifact", out var artifact) &&
                artifact.TryGetProperty("url", out var urlProp))
            {
                url = urlProp.GetString();
            }

            if (string.IsNullOrEmpty(url))
            {
                url = $"https://maven.neoforged.net/releases/{mavenPath.Replace('\\', '/')}";
            }

            ProgressChanged?.Invoke($"NeoForge libraries ({i + 1}/{libArray.Count})", 30 + (double)(i + 1) / libArray.Count * 60);

            Directory.CreateDirectory(Path.GetDirectoryName(libPath)!);
            try
            {
                var data = await Http.GetByteArrayAsync(url);
                await File.WriteAllBytesAsync(libPath, data);
            }
            catch { }
        }
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

file sealed class NeoForgeVersionList
{
    [JsonPropertyName("versions")]
    public List<string>? Versions { get; set; }
}
