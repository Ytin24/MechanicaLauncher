using System.Text.Json.Serialization;

namespace MechanicaLauncher.Core.Models;

public sealed class VersionMeta
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("mainClass")]
    public string MainClass { get; set; } = "";

    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("assets")]
    public string Assets { get; set; } = "";

    [JsonPropertyName("assetIndex")]
    public AssetIndex? AssetIndex { get; set; }

    [JsonPropertyName("downloads")]
    public Dictionary<string, DownloadInfo> Downloads { get; set; } = [];

    [JsonPropertyName("libraries")]
    public List<Library> Libraries { get; set; } = [];

    [JsonPropertyName("arguments")]
    public GameArguments? Arguments { get; set; }

    [JsonPropertyName("inheritsFrom")]
    public string? InheritsFrom { get; set; }
}

public sealed class AssetIndex
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("url")]
    public string Url { get; set; } = "";

    [JsonPropertyName("totalSize")]
    public long TotalSize { get; set; }
}

public sealed class DownloadInfo
{
    [JsonPropertyName("url")]
    public string Url { get; set; } = "";

    [JsonPropertyName("sha1")]
    public string Sha1 { get; set; } = "";

    [JsonPropertyName("size")]
    public long Size { get; set; }
}

public sealed class Library
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("downloads")]
    public LibraryDownloads? Downloads { get; set; }

    [JsonPropertyName("rules")]
    public List<Rule>? Rules { get; set; }
}

public sealed class LibraryDownloads
{
    [JsonPropertyName("artifact")]
    public LibraryArtifact? Artifact { get; set; }
}

public sealed class LibraryArtifact
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    [JsonPropertyName("url")]
    public string Url { get; set; } = "";

    [JsonPropertyName("sha1")]
    public string Sha1 { get; set; } = "";

    [JsonPropertyName("size")]
    public long Size { get; set; }
}

public sealed class Rule
{
    [JsonPropertyName("action")]
    public string Action { get; set; } = "";

    [JsonPropertyName("os")]
    public OsRule? Os { get; set; }
}

public sealed class OsRule
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }
}

public sealed class GameArguments
{
    [JsonPropertyName("game")]
    public List<System.Text.Json.JsonElement> Game { get; set; } = [];

    [JsonPropertyName("jvm")]
    public List<System.Text.Json.JsonElement> Jvm { get; set; } = [];
}

public sealed class AssetIndexData
{
    [JsonPropertyName("objects")]
    public Dictionary<string, AssetObject> Objects { get; set; } = [];
}

public sealed class AssetObject
{
    [JsonPropertyName("hash")]
    public string Hash { get; set; } = "";

    [JsonPropertyName("size")]
    public long Size { get; set; }
}
