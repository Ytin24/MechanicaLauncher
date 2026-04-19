using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MechanicaLauncher.Core.Instances;

public sealed partial class InstanceManager
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    private readonly string _baseDir;

    // Fires whenever any InstanceManager creates, saves, or deletes an instance — lets UI pages react live.
    public static event Action? InstancesChanged;
    private static void Raise() => InstancesChanged?.Invoke();

    public InstanceManager(string? baseDir = null)
    {
        _baseDir = baseDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MechanicaLauncher");
    }

    public string InstancesDir => Path.Combine(_baseDir, "instances");
    public string SharedDir => Path.Combine(_baseDir, "shared");
    public string SharedLibrariesDir => Path.Combine(SharedDir, "libraries");
    public string SharedAssetsDir => Path.Combine(SharedDir, "assets");
    public string SharedRuntimeDir => Path.Combine(SharedDir, "runtime");

    public string GetInstanceDir(string instanceId) => Path.Combine(InstancesDir, instanceId);
    public string GetGameDir(string instanceId) => Path.Combine(InstancesDir, instanceId, ".minecraft");

    // Resolved absolute path to the instance's custom icon, or null if not set / missing.
    public string? GetIconAbsolutePath(GameInstance inst)
    {
        if (string.IsNullOrEmpty(inst.IconPath)) return null;
        var p = Path.IsPathRooted(inst.IconPath)
            ? inst.IconPath
            : Path.Combine(GetInstanceDir(inst.Id), inst.IconPath);
        return File.Exists(p) ? p : null;
    }

    // Copies an external image into the instance directory and returns the relative filename written
    // to instance.json. Keeps only one active icon — overwrites previous.
    public string SetIconFromFile(GameInstance inst, string sourcePath)
    {
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("Icon source missing", sourcePath);
        var ext = Path.GetExtension(sourcePath);
        if (string.IsNullOrEmpty(ext)) ext = ".png";
        var dstName = "icon" + ext.ToLowerInvariant();
        var dst = Path.Combine(GetInstanceDir(inst.Id), dstName);
        Directory.CreateDirectory(GetInstanceDir(inst.Id));
        File.Copy(sourcePath, dst, overwrite: true);
        inst.IconPath = dstName;
        SaveInstance(inst);
        return dstName;
    }

    public GameInstance CreateInstance(string name, string mcVersion, LoaderType loader = LoaderType.None, string? loaderVersion = null, bool raiseChangedEvent = true)
    {
        var id = Slugify(name);
        if (Directory.Exists(GetInstanceDir(id)))
            id += "-" + Guid.NewGuid().ToString("N")[..6];

        var instance = new GameInstance
        {
            Id = id,
            Name = name,
            McVersion = mcVersion,
            Loader = loader,
            LoaderVersion = loaderVersion,
            CreatedAt = DateTime.UtcNow
        };

        var gameDir = GetGameDir(id);
        Directory.CreateDirectory(gameDir);
        Directory.CreateDirectory(Path.Combine(gameDir, "mods"));
        Directory.CreateDirectory(Path.Combine(gameDir, "config"));
        Directory.CreateDirectory(Path.Combine(gameDir, "saves"));

        SaveInstance(instance, raiseChangedEvent);
        if (raiseChangedEvent) Raise();
        return instance;
    }

    public void DeleteInstance(string instanceId)
    {
        var dir = GetInstanceDir(instanceId);
        if (Directory.Exists(dir))
            Directory.Delete(dir, true);
        Raise();
    }

    public GameInstance DuplicateInstance(string instanceId)
    {
        var src = GetInstance(instanceId) ?? throw new InvalidOperationException("Source instance not found");
        var clone = new GameInstance
        {
            Name = src.Name + " (copy)",
            McVersion = src.McVersion,
            Loader = src.Loader,
            LoaderVersion = src.LoaderVersion,
            JavaPath = src.JavaPath,
            MinMemoryMb = src.MinMemoryMb,
            MaxMemoryMb = src.MaxMemoryMb,
            JvmArgs = src.JvmArgs,
            WindowWidth = src.WindowWidth,
            WindowHeight = src.WindowHeight,
            CreatedAt = DateTime.UtcNow,
        };
        var newId = Slugify(clone.Name);
        if (Directory.Exists(GetInstanceDir(newId)))
            newId += "-" + Guid.NewGuid().ToString("N")[..6];
        clone.Id = newId;

        var dstDir = GetInstanceDir(newId);
        var srcDir = GetInstanceDir(instanceId);
        // Deep copy — reuses loader libs from shared dir, but .minecraft/mods+configs+saves are per-instance.
        CopyDirectory(srcDir, dstDir);
        // Rewrite instance.json to match new id/name.
        SaveInstance(clone);
        Raise();
        return clone;
    }

    private static void CopyDirectory(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var file in Directory.EnumerateFiles(src))
        {
            var name = Path.GetFileName(file);
            if (name == "instance.json") continue; // rewritten via SaveInstance
            File.Copy(file, Path.Combine(dst, name), overwrite: true);
        }
        foreach (var sub in Directory.EnumerateDirectories(src))
        {
            var name = Path.GetFileName(sub);
            CopyDirectory(sub, Path.Combine(dst, name));
        }
    }

    public GameInstance? GetInstance(string instanceId)
    {
        var path = Path.Combine(GetInstanceDir(instanceId), "instance.json");
        if (!File.Exists(path)) return null;
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<GameInstance>(json);
    }

    public List<GameInstance> GetAllInstances()
    {
        if (!Directory.Exists(InstancesDir)) return [];

        var result = new List<GameInstance>();
        foreach (var dir in Directory.GetDirectories(InstancesDir))
        {
            var configPath = Path.Combine(dir, "instance.json");
            if (!File.Exists(configPath)) continue;
            try
            {
                var json = File.ReadAllText(configPath);
                var inst = JsonSerializer.Deserialize<GameInstance>(json);
                if (inst != null) result.Add(inst);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Corrupted instance.json in {dir}: {ex.Message}");
            }
        }

        return result.OrderByDescending(i => i.LastPlayed ?? i.CreatedAt).ToList();
    }

    public void SaveInstance(GameInstance instance, bool raiseChangedEvent = true)
    {
        var dir = GetInstanceDir(instance.Id);
        Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(instance, JsonOpts);
        File.WriteAllText(Path.Combine(dir, "instance.json"), json);
        if (raiseChangedEvent) Raise();
    }

    public void NotifyChanged() => Raise();

    private static string Slugify(string name)
    {
        var slug = name.ToLowerInvariant().Trim();
        slug = SlugRegex().Replace(slug, "-");
        slug = MultiDash().Replace(slug, "-").Trim('-');
        return string.IsNullOrEmpty(slug) ? "instance" : slug;
    }

    [GeneratedRegex("[^a-z0-9-]")]
    private static partial Regex SlugRegex();

    [GeneratedRegex("-{2,}")]
    private static partial Regex MultiDash();
}
