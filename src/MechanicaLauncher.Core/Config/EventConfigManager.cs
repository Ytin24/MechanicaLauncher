using System.Net.Http.Json;
using System.Text.Json;

namespace MechanicaLauncher.Core.Config;

public sealed class EventConfigManager
{
    private static readonly HttpClient Http = new();
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    private readonly string _baseDir;
    private EventConfig? _active;

    public EventConfigManager(string? baseDir = null)
    {
        _baseDir = baseDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MechanicaLauncher");
    }

    public bool IsEventMode => _active != null;
    public EventConfig? Active => _active;

    public async Task<EventConfig> LoadFromUrlAsync(string url)
    {
        var json = await Http.GetStringAsync(url);
        var config = JsonSerializer.Deserialize<EventConfig>(json)
            ?? throw new Exception("Invalid event config");

        var cacheDir = Path.Combine(_baseDir, "events", config.Id);
        Directory.CreateDirectory(cacheDir);
        await File.WriteAllTextAsync(Path.Combine(cacheDir, "event.json"), json);

        _active = config;
        return config;
    }

    public EventConfig? LoadFromCache(string eventId)
    {
        var path = Path.Combine(_baseDir, "events", eventId, "event.json");
        if (!File.Exists(path)) return null;

        try
        {
            var json = File.ReadAllText(path);
            _active = JsonSerializer.Deserialize<EventConfig>(json);
            return _active;
        }
        catch { return null; }
    }

    public void SetActive(EventConfig config) => _active = config;

    public void Clear() => _active = null;

    public string GetEventDir(string eventId) =>
        Path.Combine(_baseDir, "events", eventId);
}
