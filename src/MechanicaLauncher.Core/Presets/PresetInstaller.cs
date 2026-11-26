using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using MechanicaLauncher.Core.Instances;
using MechanicaLauncher.Core.IO;
using MechanicaLauncher.Core.Models;
using MechanicaLauncher.Core.Mods;
using MechanicaLauncher.Core.Servers;

namespace MechanicaLauncher.Core.Presets;

public sealed record PresetServer(string Name, string Address, string? SyncUrl = null);
// Optional mechanica.json at the root of a .mrpack. Other launchers ignore it.
public sealed record PresetDetails(string? Author = null, string? Url = null, string? Description = null, PresetServer? Server = null);
public sealed record PresetSummary(string Name, string Minecraft, LoaderType Loader, int Mods, IReadOnlyList<string> Worlds, PresetDetails Details);
public sealed record PresetPreview(string Title, string Source, string? Description, string? IconUrl);
public sealed record PresetInstallResult(GameInstance Instance, PresetSummary Summary, PresetServer? Server);

public sealed partial class PresetInstaller(HttpClient? http = null, ModrinthClient? modrinth = null)
{
    public const long MaxPackBytes = 4L * 1024 * 1024 * 1024;
    private const int MaxDetailsBytes = 64 * 1024;
    private static readonly HttpClient DefaultHttp = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly HttpClient Http = http ?? DefaultHttp;
    private readonly ModrinthClient Modrinth = modrinth ?? new();

    public event Action<string>? StatusChanged;

    // Cheap lookup shown in the confirmation dialog, before any pack is downloaded.
    public async Task<PresetPreview> PreviewAsync(PresetLink link, CancellationToken cancellationToken = default)
    {
        if (link.Source == PresetSource.Modrinth)
        {
            var project = await Modrinth.GetProjectAsync(link.Target, cancellationToken)
                ?? throw new InvalidDataException("Modrinth project not found.");
            if (project.ProjectType != "modpack") throw new InvalidDataException("The link does not point to a Modrinth modpack.");
            return new(project.Title, link.SourceHost, project.Description, project.IconUrl);
        }
        string file = Uri.UnescapeDataString(new Uri(link.Target).Segments[^1]);
        string title = Path.GetFileNameWithoutExtension(file);
        return new(string.IsNullOrWhiteSpace(title) ? link.SourceHost : title, link.SourceHost, null, null);
    }

    public async Task<PresetInstallResult> InstallAsync(PresetLink link, InstanceManager instances, CancellationToken cancellationToken = default)
    {
        string path = Path.Combine(Path.GetTempPath(), "mechanica-preset-" + Guid.NewGuid().ToString("N") + ".mrpack");
        try
        {
            if (link.Source == PresetSource.Modrinth)
            {
                var version = await ResolveVersionAsync(link, cancellationToken);
                var file = ModInstaller.SelectFile(version, ".mrpack");
                if (!Uri.TryCreate(file.Url, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps)
                    throw new InvalidDataException("Modrinth returned a non-HTTPS download.");
                StatusChanged?.Invoke(file.Filename);
                await FileDownloader.EnsureAsync(Http, file.Url, path, file.Hashes.GetValueOrDefault("sha1"), file.Size,
                    cancellationToken, file.Hashes.GetValueOrDefault("sha512"), MaxPackBytes);
            }
            else
            {
                StatusChanged?.Invoke(link.SourceHost);
                await FileDownloader.EnsureAsync(Http, link.Target, path, null, 0, cancellationToken, link.Sha512, MaxPackBytes);
            }

            var summary = Inspect(path);
            var installer = new ModpackInstaller(Http);
            installer.ProgressChanged += (message, _) => StatusChanged?.Invoke(message);
            var instance = await installer.ImportAsync(path, instances, cancellationToken);
            return new(instance, summary, ChooseServer(link, summary));
        }
        finally { AtomicFile.TryDelete(path); }
    }

    private async Task<ModrinthVersion> ResolveVersionAsync(PresetLink link, CancellationToken cancellationToken)
    {
        if (link.Version != null)
            return await Modrinth.GetProjectVersionAsync(link.Target, link.Version, cancellationToken)
                ?? throw new InvalidDataException("Modpack version not found.");
        var versions = (await Modrinth.GetProjectVersionsAsync(link.Target, cancellationToken: cancellationToken))
            .Where(v => v.Files.Any(f => f.Filename.EndsWith(".mrpack", StringComparison.OrdinalIgnoreCase))).ToList();
        return versions.FirstOrDefault(v => v.VersionType == "release") ?? versions.FirstOrDefault()
            ?? throw new InvalidDataException("The modpack has no downloadable .mrpack.");
    }

    private static PresetServer? ChooseServer(PresetLink link, PresetSummary summary)
    {
        var packed = summary.Details.Server;
        if (link.Server == null) return packed;
        if (packed != null && SameAddress(packed.Address, link.Server)) return packed;
        return new(summary.Name, link.Server);
    }

    private static bool SameAddress(string a, string b) =>
        FavoriteServers.TryParseAddress(a, out var hostA, out int portA) && FavoriteServers.TryParseAddress(b, out var hostB, out int portB) &&
        hostA.Equals(hostB, StringComparison.OrdinalIgnoreCase) && portA == portB;

    public static PresetSummary Inspect(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var indexEntry = zip.GetEntry("modrinth.index.json") ?? throw new InvalidDataException("Invalid mrpack: missing modrinth.index.json");
        MrpackIndex index;
        using (var stream = indexEntry.Open())
            index = JsonSerializer.Deserialize<MrpackIndex>(stream) ?? throw new InvalidDataException("Invalid mrpack: empty index");
        ModpackInstaller.ValidateIndex(index);

        var loader = LoaderType.None;
        foreach (var key in index.Dependencies.Keys)
            loader = key switch
            {
                "fabric-loader" => LoaderType.Fabric, "quilt-loader" => LoaderType.Quilt,
                "forge" => LoaderType.Forge, "neoforge" => LoaderType.NeoForge, _ => loader
            };

        int mods = index.Files.Count(f => f.Env?.GetValueOrDefault("client") != "unsupported" &&
            f.Path.Replace('\\', '/').StartsWith("mods/", StringComparison.OrdinalIgnoreCase));
        var worlds = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');
            if (OverrideMod().IsMatch(name)) mods++;
            var world = OverrideWorld().Match(name);
            if (world.Success) worlds.Add(world.Groups[1].Value);
        }
        string packName = string.IsNullOrWhiteSpace(index.Name) ? Path.GetFileNameWithoutExtension(path) : index.Name;
        var details = ReadDetails(zip.GetEntry("mechanica.json") ?? zip.GetEntry("overrides/mechanica.json"), packName);
        return new(packName, index.Dependencies["minecraft"], loader, mods, worlds.ToArray(), details);
    }

    private static PresetDetails ReadDetails(ZipArchiveEntry? entry, string packName)
    {
        if (entry == null || entry.Length > MaxDetailsBytes) return new();
        try
        {
            using var stream = entry.Open();
            using var document = JsonDocument.Parse(stream);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return new();
            string? url = Text(root, "url", 512);
            if (url != null && !(Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && uri.UserInfo.Length == 0)) url = null;
            PresetServer? server = null;
            if (root.TryGetProperty("server", out var node) && node.ValueKind == JsonValueKind.Object &&
                Text(node, "address", 260) is { } address && FavoriteServers.TryParseAddress(address, out _, out _))
            {
                string? sync = Text(node, "syncUrl", 2048);
                if (sync != null && !FavoriteServers.IsValidSyncUrl(sync, false)) sync = null;
                server = new(Text(node, "name", 64) ?? packName, address, sync);
            }
            return new(Text(root, "author", 64), url, Text(root, "description", 600, multiline: true), server);
        }
        catch (JsonException) { return new(); }
        catch (InvalidDataException) { return new(); }

        static string? Text(JsonElement element, string name, int max, bool multiline = false) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String &&
            value.GetString()?.Trim() is { Length: > 0 } text && text.Length <= max &&
            !text.Any(c => char.IsControl(c) && !(multiline && c is '\n' or '\r')) ? text : null;
    }

    [GeneratedRegex("^(?:client-)?overrides/mods/[^/]+\\.jar$", RegexOptions.IgnoreCase)]
    private static partial Regex OverrideMod();
    [GeneratedRegex("^(?:client-)?overrides/saves/([^/]+)/level\\.dat$", RegexOptions.IgnoreCase)]
    private static partial Regex OverrideWorld();
}
