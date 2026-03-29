using System.IO.Compression;
using System.Text.Json;
using System.Xml.Linq;

namespace MechanicaLauncher.Core.Game;

public sealed class ForgeInstaller
{
    private static readonly HttpClient Http = new();
    private const string MavenBase = "https://maven.minecraftforge.net/net/minecraftforge/forge";
    private readonly string _sharedDir;
    private readonly string _instanceGameDir;

    public ForgeInstaller(string sharedDir, string instanceGameDir)
    {
        _sharedDir = sharedDir;
        _instanceGameDir = instanceGameDir;
    }

    public event Action<string, double>? ProgressChanged;

    public async Task<List<string>> GetVersionsAsync(string mcVersion)
    {
        var metaUrl = $"{MavenBase}/maven-metadata.xml";
        var xml = await Http.GetStringAsync(metaUrl);
        var doc = XDocument.Parse(xml);

        return doc.Descendants("version")
            .Select(v => v.Value)
            .Where(v => v.StartsWith($"{mcVersion}-"))
            .Select(v => v.Replace($"{mcVersion}-", ""))
            .Reverse()
            .Take(20)
            .ToList();
    }

    public async Task InstallAsync(string mcVersion, string forgeVersion)
    {
        ProgressChanged?.Invoke("Downloading Forge installer...", 10);

        var fullVersion = $"{mcVersion}-{forgeVersion}";
        var installerUrl = $"{MavenBase}/{fullVersion}/forge-{fullVersion}-installer.jar";

        var tmpPath = Path.Combine(Path.GetTempPath(), $"forge-{fullVersion}-installer.jar");
        var bytes = await Http.GetByteArrayAsync(installerUrl);
        await File.WriteAllBytesAsync(tmpPath, bytes);

        ProgressChanged?.Invoke("Extracting version profile...", 30);

        var versionId = $"{mcVersion}-forge-{forgeVersion}";
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
        ProgressChanged?.Invoke("Forge installed!", 100);
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
                var baseUrl = "https://maven.minecraftforge.net/";
                if (lib.TryGetProperty("url", out var customUrl) && !string.IsNullOrEmpty(customUrl.GetString()))
                    baseUrl = customUrl.GetString()!;
                url = baseUrl.TrimEnd('/') + "/" + mavenPath.Replace('\\', '/');
            }

            ProgressChanged?.Invoke($"Forge libraries ({i + 1}/{libArray.Count})", 30 + (double)(i + 1) / libArray.Count * 60);

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
