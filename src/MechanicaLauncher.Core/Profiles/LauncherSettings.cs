using System.Text.Json;
using System.Text.Json.Serialization;
using MechanicaLauncher.Core.IO;

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

    [JsonPropertyName("msClientId")]
    public string MsClientId { get; set; } = "";

    [JsonPropertyName("selectedInstanceId")]
    public string? SelectedInstanceId { get; set; }

    [JsonPropertyName("compactInstances")]
    public bool CompactInstances { get; set; }

    [JsonPropertyName("closeOnLaunch")]
    public bool CloseOnLaunch { get; set; }

    [JsonPropertyName("closeToTray")]
    public bool CloseToTray { get; set; } = true;

    [JsonPropertyName("minimizeToTray")]
    public bool MinimizeToTray { get; set; }

    [JsonPropertyName("showSnapshots")]
    public bool ShowSnapshots { get; set; }

    [JsonPropertyName("theme")]
    public string Theme { get; set; } = "Dark";

    [JsonPropertyName("animations")]
    public bool Animations { get; set; } = true;

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

    [JsonPropertyName("tlauncherScanDirectories")]
    public string[] TLauncherScanDirectories { get; set; } = [];

    [JsonPropertyName("knownTLauncherFiles")]
    public string[] KnownTLauncherFiles { get; set; } = [];

    [JsonPropertyName("knownTLauncherDirectories")]
    public string[] KnownTLauncherDirectories { get; set; } = [];

    private static string SettingsPath => Path.Combine(LauncherPaths.DataDirectory, "settings.json");

    public static LauncherSettings Load()
    {
        lock (_lock)
        {
            foreach (var path in new[] { SettingsPath, SettingsPath + ".bak" })
            {
                try
                {
                    if (!File.Exists(path)) continue;
                    var settings = JsonSerializer.Deserialize<LauncherSettings>(File.ReadAllText(path));
                    if (settings != null) return settings;
                }
                catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
            }
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
            AtomicFile.WriteText(SettingsPath, json, keepBackup: true);
        }
    }
}
