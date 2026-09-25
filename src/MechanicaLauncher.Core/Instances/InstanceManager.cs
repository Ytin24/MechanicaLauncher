using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using MechanicaLauncher.Core.IO;

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
        _baseDir = baseDir ?? LauncherPaths.DataDirectory;
    }

    public string InstancesDir => Path.Combine(_baseDir, "instances");
    public string SharedDir => Path.Combine(_baseDir, "shared");
    public string SharedLibrariesDir => Path.Combine(SharedDir, "libraries");
    public string SharedAssetsDir => Path.Combine(SharedDir, "assets");
    public string SharedRuntimeDir => Path.Combine(SharedDir, "runtime");

    public string GetInstanceDir(string instanceId) => FileDownloader.GetPath(InstancesDir, instanceId);
    public string GetGameDir(string instanceId) => Path.Combine(GetInstanceDir(instanceId), ".minecraft");

    public string? GetCoverAbsolutePath(GameInstance instance)
    {
        if (string.IsNullOrEmpty(instance.CoverPath)) return null;
        try
        {
            var path = FileDownloader.GetPath(GetInstanceDir(instance.Id), instance.CoverPath);
            return File.Exists(path) ? path : null;
        }
        catch (InvalidDataException) { return null; }
    }

    public void SaveAppearance(GameInstance instance, string? coverSource, string? accent)
    {
        if (accent != null && !InstanceMedia.IsAccent(accent)) throw new InvalidDataException("Invalid accent color.");
        var cover = instance.CoverPath;
        if (coverSource == null) cover = null;
        else if (!string.Equals(GetCoverAbsolutePath(instance), Path.GetFullPath(coverSource), StringComparison.OrdinalIgnoreCase))
        {
            if (!InstanceMedia.IsImage(coverSource) || new FileInfo(coverSource).Length > 20 * 1024 * 1024)
                throw new InvalidDataException("Choose a PNG or JPEG image smaller than 20 MB.");
            using (var stream = File.OpenRead(coverSource))
            {
                Span<byte> signature = stackalloc byte[8];
                var count = stream.Read(signature);
                bool png = count == 8 && signature.SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
                bool jpeg = count >= 3 && signature[0] == 255 && signature[1] == 216 && signature[2] == 255;
                if (!png && !jpeg) throw new InvalidDataException("The file is not a PNG or JPEG image.");
            }
            cover = "cover-" + Guid.NewGuid().ToString("N") + Path.GetExtension(coverSource).ToLowerInvariant();
            File.Copy(coverSource, FileDownloader.GetPath(GetInstanceDir(instance.Id), cover));
        }
        instance.CoverPath = cover;
        instance.AccentColor = accent;
        SaveInstance(instance);
    }

    // Resolved absolute path to the instance's custom icon, or null if not set / missing.
    public string? GetIconAbsolutePath(GameInstance inst)
    {
        if (string.IsNullOrEmpty(inst.IconPath)) return null;
        var p = Path.IsPathRooted(inst.IconPath)
            ? inst.IconPath
            : Path.Combine(GetInstanceDir(inst.Id), inst.IconPath);
        return File.Exists(p) ? p : null;
    }

    public string SetIconFromFile(GameInstance inst, string sourcePath, bool save = true)
    {
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("Icon source missing", sourcePath);
        var ext = Path.GetExtension(sourcePath);
        if (string.IsNullOrEmpty(ext)) ext = ".png";
        var dstName = "icon-" + Guid.NewGuid().ToString("N") + ext.ToLowerInvariant();
        var dst = Path.Combine(GetInstanceDir(inst.Id), dstName);
        Directory.CreateDirectory(GetInstanceDir(inst.Id));
        File.Copy(sourcePath, dst);
        inst.IconPath = dstName;
        if (save) SaveInstance(inst);
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
            IconPath = src.IconPath,
            CoverPath = src.CoverPath,
            AccentColor = src.AccentColor,
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
        foreach (var candidate in new[] { path, path + ".bak" })
        {
            try
            {
                if (!File.Exists(candidate)) continue;
                var instance = JsonSerializer.Deserialize<GameInstance>(File.ReadAllText(candidate));
                if (instance?.Id == instanceId) return instance;
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                Debug.WriteLine($"Could not read {candidate}: {ex.Message}");
            }
        }
        return null;
    }

    public List<GameInstance> GetAllInstances()
    {
        if (!Directory.Exists(InstancesDir)) return [];

        var result = new List<GameInstance>();
        foreach (var dir in Directory.GetDirectories(InstancesDir))
        {
            try
            {
                var inst = GetInstance(Path.GetFileName(dir));
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
        AtomicFile.WriteText(Path.Combine(dir, "instance.json"), json, keepBackup: true);
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
