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
}
