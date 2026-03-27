using System.Text.Json;
using System.Text.Json.Serialization;

namespace MechanicaLauncher.Core.Profiles;

public sealed class LauncherSettings
{
    [JsonPropertyName("username")]
    public string Username { get; set; } = "Player";

    [JsonPropertyName("authMode")]
    public string AuthMode { get; set; } = "offline";

    [JsonPropertyName("uuid")]
    public string Uuid { get; set; } = "0";

    [JsonPropertyName("accessToken")]
    public string AccessToken { get; set; } = "0";

    [JsonPropertyName("selectedInstanceId")]
    public string? SelectedInstanceId { get; set; }

    [JsonPropertyName("closeOnLaunch")]
    public bool CloseOnLaunch { get; set; }

    [JsonPropertyName("showSnapshots")]
    public bool ShowSnapshots { get; set; }

    [JsonPropertyName("theme")]
    public string Theme { get; set; } = "Dark";

    [JsonPropertyName("language")]
    public string? Language { get; set; }

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
