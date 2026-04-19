using System.Net.Http.Json;
using MechanicaLauncher.Core.Models;

namespace MechanicaLauncher.Core.Mods;

public sealed class ModrinthClient
{
    private static readonly HttpClient Http = new() { BaseAddress = new Uri("https://api.modrinth.com") };

    static ModrinthClient()
    {
        Http.DefaultRequestHeaders.Add("User-Agent", "MechanicaLauncher/1.0.0 (mechanica@launcher)");
    }

    public async Task<ModrinthSearchResult> SearchAsync(string query, string? mcVersion = null,
                                                         string? loader = null, string? projectType = null,
                                                         string? category = null,
                                                         int offset = 0, int limit = 20,
                                                         string sortBy = "relevance")
    {
        var facets = new List<string>();
        facets.Add($"[\"project_type:{projectType ?? "mod"}\"]");
        if (!string.IsNullOrEmpty(mcVersion))
            facets.Add($"[\"versions:{mcVersion}\"]");
        if (!string.IsNullOrEmpty(loader))
            facets.Add($"[\"categories:{loader}\"]");
        if (!string.IsNullOrEmpty(category))
            facets.Add($"[\"categories:{category}\"]");

        var facetsStr = $"[{string.Join(",", facets)}]";

        var uri = new Uri($"/v2/search?query={Uri.EscapeDataString(query)}&facets={facetsStr}&offset={offset}&limit={limit}&index={sortBy}", UriKind.Relative);
        return await Http.GetFromJsonAsync<ModrinthSearchResult>(uri) ?? new();
    }

    public async Task<List<ModrinthVersion>> GetProjectVersionsAsync(string projectId,
                                                                       string? mcVersion = null, string? loader = null)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(mcVersion))
            parts.Add($"game_versions=%5B%22{mcVersion}%22%5D");
        if (!string.IsNullOrEmpty(loader))
            parts.Add($"loaders=%5B%22{loader}%22%5D");

        var qs = parts.Count > 0 ? "?" + string.Join("&", parts) : "";
        return await Http.GetFromJsonAsync<List<ModrinthVersion>>($"/v2/project/{projectId}/version{qs}") ?? [];
    }

    public async Task<ModrinthVersion?> GetVersionAsync(string versionId)
    {
        return await Http.GetFromJsonAsync<ModrinthVersion>($"/v2/version/{versionId}");
    }

    // Reverse-lookup a mod by its jar's sha1 hash → returns the owning project info (title + icon).
    // Modrinth caches aggressively; our own process-memory dict dedupes calls across the UI session.
    private static readonly Dictionary<string, ModrinthProjectInfo?> _hashCache = new();

    public async Task<ModrinthProjectInfo?> LookupProjectByHashAsync(string sha1)
    {
        if (_hashCache.TryGetValue(sha1, out var cached)) return cached;
        try
        {
            var ver = await Http.GetFromJsonAsync<ModrinthVersion>($"/v2/version_file/{sha1}?algorithm=sha1");
            if (ver?.ProjectId == null) { _hashCache[sha1] = null; return null; }
            var proj = await Http.GetFromJsonAsync<ModrinthProjectInfo>($"/v2/project/{ver.ProjectId}");
            _hashCache[sha1] = proj;
            return proj;
        }
        catch { _hashCache[sha1] = null; return null; }
    }
}

public sealed class ModrinthProjectInfo
{
    [System.Text.Json.Serialization.JsonPropertyName("id")]
    public string Id { get; set; } = "";
    [System.Text.Json.Serialization.JsonPropertyName("title")]
    public string Title { get; set; } = "";
    [System.Text.Json.Serialization.JsonPropertyName("icon_url")]
    public string? IconUrl { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("description")]
    public string? Description { get; set; }
}
