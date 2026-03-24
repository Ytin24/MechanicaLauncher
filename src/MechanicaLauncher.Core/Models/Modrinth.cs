using System.Text.Json.Serialization;

namespace MechanicaLauncher.Core.Models;

public sealed class ModrinthSearchResult
{
    [JsonPropertyName("hits")]
    public List<ModrinthProject> Hits { get; set; } = [];

    [JsonPropertyName("total_hits")]
    public int TotalHits { get; set; }
}

public sealed class ModrinthProject
{
    [JsonPropertyName("project_id")]
    public string ProjectId { get; set; } = "";

    [JsonPropertyName("slug")]
    public string Slug { get; set; } = "";

    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    [JsonPropertyName("icon_url")]
    public string? IconUrl { get; set; }

    [JsonPropertyName("downloads")]
    public long Downloads { get; set; }

    [JsonPropertyName("categories")]
    public List<string> Categories { get; set; } = [];

    [JsonPropertyName("project_type")]
    public string ProjectType { get; set; } = "";

    public string DownloadsFormatted => Downloads switch
    {
        >= 1_000_000 => $"{Downloads / 1_000_000.0:0.#}M",
        >= 1_000 => $"{Downloads / 1_000.0:0.#}K",
        _ => Downloads.ToString()
    };
}

public sealed class ModrinthVersion
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("version_number")]
    public string VersionNumber { get; set; } = "";

    [JsonPropertyName("game_versions")]
    public List<string> GameVersions { get; set; } = [];

    [JsonPropertyName("loaders")]
    public List<string> Loaders { get; set; } = [];

    [JsonPropertyName("files")]
    public List<ModrinthFile> Files { get; set; } = [];

    [JsonPropertyName("dependencies")]
    public List<ModrinthDependency> Dependencies { get; set; } = [];
}

public sealed class ModrinthFile
{
    [JsonPropertyName("url")]
    public string Url { get; set; } = "";

    [JsonPropertyName("filename")]
    public string Filename { get; set; } = "";

    [JsonPropertyName("primary")]
    public bool Primary { get; set; }

    [JsonPropertyName("size")]
    public long Size { get; set; }
}

public sealed class ModrinthDependency
{
    [JsonPropertyName("project_id")]
    public string? ProjectId { get; set; }

    [JsonPropertyName("version_id")]
    public string? VersionId { get; set; }

    [JsonPropertyName("dependency_type")]
    public string DependencyType { get; set; } = "";
}
