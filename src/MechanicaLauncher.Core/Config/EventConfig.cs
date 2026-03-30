using System.Text.Json.Serialization;

namespace MechanicaLauncher.Core.Config;

public sealed class EventConfig
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("version")]
    public string Version { get; set; } = "1.0";

    [JsonPropertyName("secret")]
    public string? Secret { get; set; }

    [JsonPropertyName("branding")]
    public EventBranding? Branding { get; set; }

    [JsonPropertyName("ui")]
    public EventUi? Ui { get; set; }

    [JsonPropertyName("minecraft")]
    public EventMinecraft? Minecraft { get; set; }

    [JsonPropertyName("server")]
    public EventServer? Server { get; set; }

    [JsonPropertyName("mods")]
    public EventMods? Mods { get; set; }

    [JsonPropertyName("integrity")]
    public EventIntegrity? Integrity { get; set; }

    [JsonPropertyName("discord")]
    public EventDiscord? Discord { get; set; }
}

public sealed class EventBranding
{
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("subtitle")]
    public string? Subtitle { get; set; }

    [JsonPropertyName("accentColor")]
    public string? AccentColor { get; set; }

    [JsonPropertyName("accentColorHover")]
    public string? AccentColorHover { get; set; }

    [JsonPropertyName("backgroundColor")]
    public string? BackgroundColor { get; set; }

    [JsonPropertyName("textColor")]
    public string? TextColor { get; set; }

    [JsonPropertyName("logoUrl")]
    public string? LogoUrl { get; set; }

    [JsonPropertyName("splashTexts")]
    public List<string>? SplashTexts { get; set; }
}

public sealed class EventUi
{
    [JsonPropertyName("showInstances")]
    public bool ShowInstances { get; set; } = true;

    [JsonPropertyName("showMods")]
    public bool ShowMods { get; set; } = true;

    [JsonPropertyName("showAccount")]
    public bool ShowAccount { get; set; } = true;

    [JsonPropertyName("showSettings")]
    public bool ShowSettings { get; set; } = true;

    [JsonPropertyName("showLogPanel")]
    public bool ShowLogPanel { get; set; } = true;

    [JsonPropertyName("allowModInstall")]
    public bool AllowModInstall { get; set; } = true;

    [JsonPropertyName("allowInstanceCreate")]
    public bool AllowInstanceCreate { get; set; } = true;

    [JsonPropertyName("allowInstanceDelete")]
    public bool AllowInstanceDelete { get; set; } = true;

    [JsonPropertyName("allowModToggle")]
    public bool AllowModToggle { get; set; } = true;

    [JsonPropertyName("playButtonText")]
    public string? PlayButtonText { get; set; }
}

public sealed class EventMinecraft
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = "";

    [JsonPropertyName("loader")]
    public string? Loader { get; set; }

    [JsonPropertyName("loaderVersion")]
    public string? LoaderVersion { get; set; }

    [JsonPropertyName("jvmArgs")]
    public string? JvmArgs { get; set; }

    [JsonPropertyName("minMemory")]
    public int MinMemory { get; set; } = 2048;

    [JsonPropertyName("maxMemory")]
    public int MaxMemory { get; set; } = 4096;
}

public sealed class EventServer
{
    [JsonPropertyName("host")]
    public string Host { get; set; } = "";

    [JsonPropertyName("port")]
    public int Port { get; set; } = 25565;

    [JsonPropertyName("autoConnect")]
    public bool AutoConnect { get; set; }
}

public sealed class EventMod
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("url")]
    public string Url { get; set; } = "";

    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = "";

    [JsonPropertyName("filename")]
    public string Filename { get; set; } = "";
}

public sealed class EventMods
{
    [JsonPropertyName("required")]
    public List<EventMod> Required { get; set; } = [];

    [JsonPropertyName("optional")]
    public List<EventMod> Optional { get; set; } = [];

    [JsonPropertyName("forbidden")]
    public List<string> Forbidden { get; set; } = [];
}

public sealed class EventIntegrity
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("strict")]
    public bool Strict { get; set; }

    [JsonPropertyName("checkBeforeLaunch")]
    public bool CheckBeforeLaunch { get; set; } = true;

    [JsonPropertyName("blockOnFailure")]
    public bool BlockOnFailure { get; set; } = true;
}

public sealed class EventDiscord
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("appId")]
    public string? AppId { get; set; }

    [JsonPropertyName("details")]
    public string? Details { get; set; }

    [JsonPropertyName("state")]
    public string? State { get; set; }

    [JsonPropertyName("largeImageKey")]
    public string? LargeImageKey { get; set; }

    [JsonPropertyName("buttonLabel")]
    public string? ButtonLabel { get; set; }

    [JsonPropertyName("buttonUrl")]
    public string? ButtonUrl { get; set; }
}
