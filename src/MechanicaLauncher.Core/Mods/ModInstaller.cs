using MechanicaLauncher.Core.Models;

namespace MechanicaLauncher.Core.Mods;

public sealed class ModInstaller
{
    private static readonly HttpClient Http = new();
    private readonly ModrinthClient _client = new();

    public event Action<string>? StatusChanged;

    public async Task InstallModAsync(ModrinthVersion version, string modsDir,
                                       string? mcVersion = null, string? loader = null)
    {
        Directory.CreateDirectory(modsDir);

        var file = version.Files.FirstOrDefault(f => f.Primary) ?? version.Files.FirstOrDefault();
        if (file == null) throw new Exception("No files in mod version");

        var dest = Path.Combine(modsDir, file.Filename);
        if (!File.Exists(dest))
        {
            StatusChanged?.Invoke($"Downloading {file.Filename}...");
            var bytes = await Http.GetByteArrayAsync(file.Url);
            await File.WriteAllBytesAsync(dest, bytes);
        }

        var deps = version.Dependencies.Where(d => d.DependencyType == "required").ToList();
        foreach (var dep in deps)
        {
            if (string.IsNullOrEmpty(dep.ProjectId)) continue;

            try
            {
                var versions = await _client.GetProjectVersionsAsync(dep.ProjectId, mcVersion, loader);
                var depVer = versions.FirstOrDefault();
                if (depVer == null) continue;

                var depFile = depVer.Files.FirstOrDefault(f => f.Primary) ?? depVer.Files.FirstOrDefault();
                if (depFile == null) continue;

                var depDest = Path.Combine(modsDir, depFile.Filename);
                if (!File.Exists(depDest))
                {
                    StatusChanged?.Invoke($"Dependency: {depFile.Filename}...");
                    var bytes = await Http.GetByteArrayAsync(depFile.Url);
                    await File.WriteAllBytesAsync(depDest, bytes);
                }
            }
            catch { }
        }
    }

    public static List<InstalledMod> GetInstalledMods(string modsDir)
    {
        if (!Directory.Exists(modsDir)) return [];

        var result = new List<InstalledMod>();
        foreach (var file in Directory.GetFiles(modsDir, "*.jar"))
        {
            result.Add(new InstalledMod
            {
                FileName = Path.GetFileName(file),
                FilePath = file,
                Enabled = true,
                SizeBytes = new FileInfo(file).Length
            });
        }
        foreach (var file in Directory.GetFiles(modsDir, "*.jar.disabled"))
        {
            result.Add(new InstalledMod
            {
                FileName = Path.GetFileNameWithoutExtension(file),
                FilePath = file,
                Enabled = false,
                SizeBytes = new FileInfo(file).Length
            });
        }
        return result.OrderBy(m => m.FileName).ToList();
    }

    public static void ToggleMod(string modPath)
    {
        if (modPath.EndsWith(".jar.disabled"))
            File.Move(modPath, modPath[..^".disabled".Length]);
        else if (modPath.EndsWith(".jar"))
            File.Move(modPath, modPath + ".disabled");
    }

    public static void RemoveMod(string modPath)
    {
        if (File.Exists(modPath)) File.Delete(modPath);
    }
}

public sealed class InstalledMod
{
    public string FileName { get; set; } = "";
    public string FilePath { get; set; } = "";
    public bool Enabled { get; set; }
    public long SizeBytes { get; set; }

    public string SizeFormatted => SizeBytes switch
    {
        >= 1_048_576 => $"{SizeBytes / 1_048_576.0:0.#} MB",
        >= 1024 => $"{SizeBytes / 1024.0:0.#} KB",
        _ => $"{SizeBytes} B"
    };
}
