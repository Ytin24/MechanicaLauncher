using System.Text.Json;
using System.Text.Json.Serialization;

namespace MechanicaLauncher.Core.Profiles;

public sealed class LauncherSettings
{
    private static readonly object _lock = new();

    [JsonPropertyName("username")]
    public string Username { get; set; } = "Player";

    [JsonPropertyName("authMode")]
    public string AuthMode { get; set; } = "offline";

    [JsonPropertyName("uuid")]
    public string Uuid { get; set; } = "0";

    [JsonPropertyName("accessToken")]
    public string AccessToken { get; set; } = "0";

    [JsonPropertyName("msRefreshToken")]
    public string MsRefreshToken { get; set; } = "";

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

    [JsonPropertyName("discordRpc")]
    public bool DiscordRpc { get; set; } = true;

    [JsonPropertyName("discordShowServer")]
    public bool DiscordShowServer { get; set; } = true;

    [JsonPropertyName("discordShowDimension")]
    public bool DiscordShowDimension { get; set; } = true;

    [JsonPropertyName("discordShowAchievements")]
    public bool DiscordShowAchievements { get; set; } = true;

    [JsonPropertyName("discordShowMods")]
    public bool DiscordShowMods { get; set; } = true;

    [JsonPropertyName("activeEventUrl")]
    public string? ActiveEventUrl { get; set; }

    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MechanicaLauncher", "settings.json");

    public static LauncherSettings Load()
    {
        lock (_lock)
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
    }

    public void Save()
    {
        lock (_lock)
        {
            var dir = Path.GetDirectoryName(SettingsPath)!;
            Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsPath, json);
        }
    }
}
