using System.Text.Json.Serialization;

namespace MechanicaLauncher.Core.Instances;

public sealed class GameInstance
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("mcVersion")]
    public string McVersion { get; set; } = "";

    [JsonPropertyName("loader")]
    public LoaderType Loader { get; set; } = LoaderType.None;

    [JsonPropertyName("loaderVersion")]
    public string? LoaderVersion { get; set; }

    [JsonPropertyName("javaPath")]
    public string? JavaPath { get; set; }

    [JsonPropertyName("minMemoryMb")]
    public int MinMemoryMb { get; set; } = 2048;

    [JsonPropertyName("maxMemoryMb")]
    public int MaxMemoryMb { get; set; } = 4096;

    [JsonPropertyName("jvmArgs")]
    public string JvmArgs { get; set; } = "";

    [JsonPropertyName("windowWidth")]
    public int WindowWidth { get; set; } = 1920;

    [JsonPropertyName("windowHeight")]
    public int WindowHeight { get; set; } = 1080;

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("lastPlayed")]
    public DateTime? LastPlayed { get; set; }

    public string GetEffectiveVersionId() => Loader switch
    {
        LoaderType.Fabric when !string.IsNullOrEmpty(LoaderVersion)
            => $"fabric-loader-{LoaderVersion}-{McVersion}",
        LoaderType.Quilt when !string.IsNullOrEmpty(LoaderVersion)
            => $"quilt-loader-{LoaderVersion}-{McVersion}",
        LoaderType.Forge when !string.IsNullOrEmpty(LoaderVersion)
            => $"{McVersion}-forge-{LoaderVersion}",
        _ => McVersion
    };
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LoaderType
{
    None,
    Fabric,
    Forge,
    Quilt
}
