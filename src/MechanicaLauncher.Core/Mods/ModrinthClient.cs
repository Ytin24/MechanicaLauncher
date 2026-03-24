using System.Net.Http.Json;
using System.Web;
using MechanicaLauncher.Core.Models;

namespace MechanicaLauncher.Core.Mods;

public sealed class ModrinthClient
{
    private static readonly HttpClient Http = new();
    private const string ApiBase = "https://api.modrinth.com/v2";

    static ModrinthClient()
    {
        Http.DefaultRequestHeaders.Add("User-Agent", "MechanicaLauncher/1.0.0 (mechanica@launcher)");
    }

    public async Task<ModrinthSearchResult> SearchAsync(string query, string? mcVersion = null,
                                                         string? loader = null, int offset = 0, int limit = 20)
    {
        var facets = new List<string> { "[\"project_type:mod\"]" };
        if (!string.IsNullOrEmpty(mcVersion))
            facets.Add($"[\"versions:{mcVersion}\"]");
        if (!string.IsNullOrEmpty(loader))
            facets.Add($"[\"categories:{loader}\"]");

        var facetsStr = $"[{string.Join(",", facets)}]";
        var url = $"{ApiBase}/search?query={Uri.EscapeDataString(query)}&facets={facetsStr}&offset={offset}&limit={limit}";

        return await Http.GetFromJsonAsync<ModrinthSearchResult>(url) ?? new();
    }

    public async Task<List<ModrinthVersion>> GetProjectVersionsAsync(string projectId,
                                                                       string? mcVersion = null, string? loader = null)
    {
        var query = new List<string>();
        if (!string.IsNullOrEmpty(mcVersion))
            query.Add($"game_versions=[\"{mcVersion}\"]");
        if (!string.IsNullOrEmpty(loader))
            query.Add($"loaders=[\"{loader}\"]");

        var qs = query.Count > 0 ? "?" + string.Join("&", query) : "";
        var url = $"{ApiBase}/project/{projectId}/version{qs}";

        return await Http.GetFromJsonAsync<List<ModrinthVersion>>(url) ?? [];
    }

    public async Task<ModrinthVersion?> GetVersionAsync(string versionId)
    {
        return await Http.GetFromJsonAsync<ModrinthVersion>($"{ApiBase}/version/{versionId}");
    }
}
