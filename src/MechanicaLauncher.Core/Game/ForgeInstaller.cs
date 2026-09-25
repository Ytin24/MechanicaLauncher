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

    public Task InstallAsync(string mcVersion, string forgeVersion, string? javaPath = null,
        CancellationToken cancellationToken = default)
    {
        var fullVersion = $"{mcVersion}-{forgeVersion}";
        var installerUrl = $"{MavenBase}/{fullVersion}/forge-{fullVersion}-installer.jar";
        return LoaderInstaller.InstallAsync(Http, "Forge", installerUrl, mcVersion,
            $"{mcVersion}-forge-{forgeVersion}", _sharedDir, _instanceGameDir, javaPath, ProgressChanged, cancellationToken);
    }
}
