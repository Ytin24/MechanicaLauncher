using MechanicaLauncher.Core.Instances;
using MechanicaLauncher.Core.Mods;

namespace MechanicaLauncher.Core.Security;

public sealed class TLauncherMigrator
{
    private readonly InstanceManager _im;
    private readonly ModrinthClient _modrinth = new();

    public event Action<string>? StatusChanged;

    public TLauncherMigrator(InstanceManager im)
    {
        _im = im;
    }

    public async Task<List<GameInstance>> MigrateAsync(string minecraftDir)
    {
        var instances = new List<GameInstance>();
        if (!Directory.Exists(minecraftDir)) return instances;

        var versions = DetectAllVersions(minecraftDir);
        if (versions.Count == 0)
            versions.Add(("1.21.1", LoaderType.None, null));

        foreach (var (mcVersion, loader, loaderVersion) in versions)
        {
            StatusChanged?.Invoke($"Creating instance: {mcVersion} {loader}...");
            var instance = _im.CreateInstance($"Migrated ({mcVersion})", mcVersion, loader, loaderVersion);
            var gameDir = _im.GetGameDir(instance.Id);

            CopyDir(Path.Combine(minecraftDir, "saves"), Path.Combine(gameDir, "saves"), "worlds");
            CopyDir(Path.Combine(minecraftDir, "resourcepacks"), Path.Combine(gameDir, "resourcepacks"), "resource packs");
            CopyDir(Path.Combine(minecraftDir, "shaderpacks"), Path.Combine(gameDir, "shaderpacks"), "shaders");
            CopyDir(Path.Combine(minecraftDir, "config"), Path.Combine(gameDir, "config"), "configs");
            CopyDir(Path.Combine(minecraftDir, "screenshots"), Path.Combine(gameDir, "screenshots"), "screenshots");

            await MigrateModsAsync(minecraftDir, gameDir, mcVersion, loader);
            instances.Add(instance);
        }

        StatusChanged?.Invoke("Migration complete!");
        return instances;
    }

    private async Task MigrateModsAsync(string srcDir, string destDir, string mcVersion, LoaderType loader)
    {
        var srcMods = Path.Combine(srcDir, "mods");
        var destMods = Path.Combine(destDir, "mods");
        Directory.CreateDirectory(destMods);

        if (!Directory.Exists(srcMods)) return;

        var jars = Directory.GetFiles(srcMods, "*.jar");
        var loaderName = loader != LoaderType.None ? loader.ToString().ToLowerInvariant() : null;

        for (int i = 0; i < jars.Length; i++)
        {
            var fileName = Path.GetFileName(jars[i]);
            StatusChanged?.Invoke($"Mods ({i + 1}/{jars.Length}): {fileName}");

            var cleanName = ExtractModName(fileName);
            bool found = false;

            if (!string.IsNullOrEmpty(cleanName))
            {
                try
                {
                    var search = await _modrinth.SearchAsync(cleanName, mcVersion, loaderName, limit: 3);
                    if (search.Hits.Count > 0)
                    {
                        var versions = await _modrinth.GetProjectVersionsAsync(
                            search.Hits[0].ProjectId, mcVersion, loaderName);
                        if (versions.Count > 0)
                        {
                            var file = versions[0].Files.FirstOrDefault(f => f.Filename.EndsWith(".jar"));
                            if (file != null)
                            {
                                var destPath = Path.Combine(destMods, file.Filename);
                                if (!File.Exists(destPath))
                                {
                                    var bytes = await new HttpClient().GetByteArrayAsync(file.Url);
                                    await File.WriteAllBytesAsync(destPath, bytes);
                                    found = true;
                                }
                                else found = true;
                            }
                        }
                    }
                }
                catch { }
            }

            if (!found)
                File.Copy(jars[i], Path.Combine(destMods, fileName), true);
        }
    }

    private static string ExtractModName(string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName);
        var parts = name.Split(['-', '_', '+']);
        if (parts.Length > 0)
            return parts[0].Replace(".", " ").Trim();
        return name;
    }

    private static List<(string version, LoaderType loader, string? loaderVersion)> DetectAllVersions(string mcDir)
    {
        var result = new List<(string, LoaderType, string?)>();
        var versionsDir = Path.Combine(mcDir, "versions");
        if (!Directory.Exists(versionsDir)) return result;

        var seen = new HashSet<string>();

        foreach (var dir in Directory.GetDirectories(versionsDir))
        {
            var name = Path.GetFileName(dir);
            if (name == null) continue;

            if (name.Contains("fabric-loader-"))
            {
                var parts = name.Replace("fabric-loader-", "").Split('-');
                if (parts.Length >= 2)
                {
                    var loaderVer = parts[0];
                    var mcVer = string.Join("-", parts.Skip(1));
                    var key = $"{mcVer}-fabric";
                    if (seen.Add(key))
                        result.Add((mcVer, LoaderType.Fabric, loaderVer));
                }
            }
            else if (name.Contains("-forge-"))
            {
                var parts = name.Split("-forge-");
                if (parts.Length == 2 && seen.Add($"{parts[0]}-forge"))
                    result.Add((parts[0], LoaderType.Forge, parts[1]));
            }
            else if (name.Contains("quilt-loader-"))
            {
                var parts = name.Replace("quilt-loader-", "").Split('-');
                if (parts.Length >= 2)
                {
                    var loaderVer = parts[0];
                    var mcVer = string.Join("-", parts.Skip(1));
                    if (seen.Add($"{mcVer}-quilt"))
                        result.Add((mcVer, LoaderType.Quilt, loaderVer));
                }
            }
            else if (!name.Contains("optifine", StringComparison.OrdinalIgnoreCase))
            {
                if (seen.Add(name))
                    result.Add((name, LoaderType.None, null));
            }
        }

        return result;
    }

    private static string DetectVersion(string mcDir)
    {
        var versionsDir = Path.Combine(mcDir, "versions");
        if (!Directory.Exists(versionsDir)) return "1.21.1";

        var dirs = Directory.GetDirectories(versionsDir)
            .Select(Path.GetFileName)
            .Where(n => n != null && !n.Contains("fabric") && !n.Contains("forge") && !n.Contains("optifine"))
            .OrderByDescending(n => n)
            .ToList();

        return dirs.FirstOrDefault() ?? "1.21.1";
    }

    private static LoaderType DetectLoader(string mcDir, string mcVersion)
    {
        var versionsDir = Path.Combine(mcDir, "versions");
        if (!Directory.Exists(versionsDir)) return LoaderType.None;

        var dirs = Directory.GetDirectories(versionsDir).Select(Path.GetFileName).ToList();

        if (dirs.Any(d => d != null && d.Contains("fabric-loader")))
            return LoaderType.Fabric;
        if (dirs.Any(d => d != null && d.Contains("forge")))
            return LoaderType.Forge;
        if (dirs.Any(d => d != null && d.Contains("quilt")))
            return LoaderType.Quilt;

        return LoaderType.None;
    }

    private static string? DetectLoaderVersion(string mcDir, string mcVersion, LoaderType loader)
    {
        var versionsDir = Path.Combine(mcDir, "versions");
        if (!Directory.Exists(versionsDir)) return null;

        var dirs = Directory.GetDirectories(versionsDir).Select(Path.GetFileName).ToList();

        foreach (var dir in dirs)
        {
            if (dir == null) continue;
            if (loader == LoaderType.Fabric && dir.StartsWith("fabric-loader-"))
            {
                var parts = dir.Replace("fabric-loader-", "").Split('-');
                if (parts.Length > 0) return parts[0];
            }
        }

        return null;
    }

    private void CopyDir(string src, string dest, string label)
    {
        if (!Directory.Exists(src)) return;
        StatusChanged?.Invoke($"Copying {label}...");
        Directory.CreateDirectory(dest);

        foreach (var file in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(src, file);
            var destFile = Path.Combine(dest, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destFile)!);
            if (!File.Exists(destFile))
                File.Copy(file, destFile);
        }
    }
}
