using System.Text.Json;
using System.Text.Json.Serialization;

namespace MechanicaLauncher.Core.Profiles;

public sealed class LauncherSettings
{
    [JsonPropertyName("username")]
    public string Username { get; set; } = "Player";

    [JsonPropertyName("authMode")]
    public string AuthMode { get; set; } = "offline";

    [JsonPropertyName("selectedVersion")]
    public string SelectedVersion { get; set; } = "";

    [JsonPropertyName("javaPath")]
    public string? JavaPath { get; set; }

    [JsonPropertyName("minMemoryMb")]
    public int MinMemoryMb { get; set; } = 2048;

    [JsonPropertyName("maxMemoryMb")]
    public int MaxMemoryMb { get; set; } = 4096;

    [JsonPropertyName("jvmArgs")]
    public string JvmArgs { get; set; } = "";

    [JsonPropertyName("gameDir")]
    public string? GameDir { get; set; }

    [JsonPropertyName("closeOnLaunch")]
    public bool CloseOnLaunch { get; set; }

    [JsonPropertyName("showSnapshots")]
    public bool ShowSnapshots { get; set; }

    [JsonPropertyName("windowWidth")]
    public int WindowWidth { get; set; } = 1920;

    [JsonPropertyName("windowHeight")]
    public int WindowHeight { get; set; } = 1080;

    [JsonPropertyName("theme")]
    public string Theme { get; set; } = "Dark";

    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MechanicaLauncher", "settings.json");

    public static LauncherSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                return JsonSerializer.Deserialize<LauncherSettings>(json) ?? new();
            }
        }
        catch { }
        return new();
    }

    public void Save()
    {
        var dir = Path.GetDirectoryName(SettingsPath)!;
        Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(SettingsPath, json);
    }
}
