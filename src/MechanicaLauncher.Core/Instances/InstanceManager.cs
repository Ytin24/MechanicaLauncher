using System.Text.Json;
using System.Text.RegularExpressions;

namespace MechanicaLauncher.Core.Instances;

public sealed partial class InstanceManager
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    private readonly string _baseDir;

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

    public GameInstance CreateInstance(string name, string mcVersion, LoaderType loader = LoaderType.None, string? loaderVersion = null)
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

        SaveInstance(instance);
        return instance;
    }

    public void DeleteInstance(string instanceId)
    {
        var dir = GetInstanceDir(instanceId);
        if (Directory.Exists(dir))
            Directory.Delete(dir, true);
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
            catch { }
        }

        return result.OrderByDescending(i => i.LastPlayed ?? i.CreatedAt).ToList();
    }

    public void SaveInstance(GameInstance instance)
    {
        var dir = GetInstanceDir(instance.Id);
        Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(instance, JsonOpts);
        File.WriteAllText(Path.Combine(dir, "instance.json"), json);
    }

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
