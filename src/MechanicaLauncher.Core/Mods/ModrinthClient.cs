using System.Net.Http.Json;
using System.Text.Json;
using MechanicaLauncher.Core.Models;

namespace MechanicaLauncher.Core.Mods;

public sealed class ModrinthClient
{
    private static readonly HttpClient DefaultHttp = new() { BaseAddress = new Uri("https://api.modrinth.com"), Timeout = TimeSpan.FromSeconds(30) };
    private readonly HttpClient Http;

    public ModrinthClient(HttpClient? http = null) => Http = http ?? DefaultHttp;

    public async Task<Dictionary<string, ModrinthVersion>> GetVersionsFromHashesAsync(IEnumerable<string> hashes,
        CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, ModrinthVersion>(StringComparer.OrdinalIgnoreCase);
        foreach (var batch in hashes.Distinct(StringComparer.OrdinalIgnoreCase).Chunk(100))
        {
            using var response = await Http.PostAsJsonAsync("/v2/version_files", new { hashes = batch, algorithm = "sha1" }, cancellationToken);
            response.EnsureSuccessStatusCode();
            var versions = await response.Content.ReadFromJsonAsync<Dictionary<string, ModrinthVersion>>(cancellationToken);
            if (versions == null || versions.Values.Any(v => v == null)) throw new JsonException("Invalid version lookup response.");
            foreach (var entry in versions) result[entry.Key] = entry.Value;
        }
        return result;
    }

    public async Task<Dictionary<string, ModrinthVersion>> GetUpdatesFromHashesAsync(IEnumerable<string> hashes,
        string mcVersion, string loader, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mcVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(loader);
        var result = new Dictionary<string, ModrinthVersion>(StringComparer.OrdinalIgnoreCase);
        foreach (var batch in hashes.Distinct(StringComparer.OrdinalIgnoreCase).Chunk(100))
        {
            using var response = await Http.PostAsJsonAsync("/v2/version_files/update", new
            {
                hashes = batch, algorithm = "sha1", game_versions = new[] { mcVersion },
                loaders = new[] { loader }, version_types = new[] { "release" }
            }, cancellationToken);
            response.EnsureSuccessStatusCode();
            var versions = await response.Content.ReadFromJsonAsync<Dictionary<string, ModrinthVersion>>(cancellationToken);
            if (versions == null || versions.Any(p => p.Value == null || !batch.Contains(p.Key, StringComparer.OrdinalIgnoreCase)))
                throw new JsonException("Invalid mod update response.");
            foreach (var entry in versions) result[entry.Key] = entry.Value;
        }
        return result;
    }

    static ModrinthClient()
    {
        DefaultHttp.DefaultRequestHeaders.Add("User-Agent", "MechanicaLauncher/1.0.0 (mechanica@launcher)");
    }

    public async Task<ModrinthSearchResult> SearchAsync(string query, string? mcVersion = null,
                                                         string? loader = null, string? projectType = null,
                                                         string? category = null,
                                                         int offset = 0, int limit = 20,
                                                         string sortBy = "relevance", CancellationToken cancellationToken = default)
    {
        var type = projectType ?? "mod";
        if (type == "datapack") loader = "datapack";
        var facets = new List<string[]> { new[] { $"project_type:{type}" } };
        if (!string.IsNullOrEmpty(mcVersion))
            facets.Add([$"versions:{mcVersion}"]);
        if (!string.IsNullOrEmpty(loader))
            facets.Add([$"categories:{loader}"]);
        if (!string.IsNullOrEmpty(category))
            facets.Add([$"categories:{category}"]);

        var facetsStr = JsonSerializer.Serialize(facets);

        var uri = new Uri($"/v2/search?query={Uri.EscapeDataString(query)}&facets={Uri.EscapeDataString(facetsStr)}&offset={offset}&limit={limit}&index={Uri.EscapeDataString(sortBy)}", UriKind.Relative);
        return await Http.GetFromJsonAsync<ModrinthSearchResult>(uri, cancellationToken) ?? new();
    }

    public Task<ModrinthProjectInfo?> GetProjectAsync(string projectId, CancellationToken cancellationToken = default) =>
        Http.GetFromJsonAsync<ModrinthProjectInfo>($"/v2/project/{Uri.EscapeDataString(projectId)}", cancellationToken);

    public async Task<List<ModrinthProjectInfo>> GetProjectsAsync(IEnumerable<string> projectIds,
        CancellationToken cancellationToken = default)
    {
        var projects = new List<ModrinthProjectInfo>();
        foreach (var batch in projectIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).Chunk(100))
        {
            var ids = Uri.EscapeDataString(JsonSerializer.Serialize(batch));
            var found = await Http.GetFromJsonAsync<List<ModrinthProjectInfo>>($"/v2/projects?ids={ids}", cancellationToken);
            if (found == null || found.Any(p => p == null)) throw new JsonException("Invalid project lookup response.");
            projects.AddRange(found);
        }
        return projects;
    }

    public async Task<List<ModrinthVersion>> GetProjectVersionsAsync(string projectId,
                                                                       string? mcVersion = null, string? loader = null, CancellationToken cancellationToken = default)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(mcVersion))
            parts.Add("game_versions=" + Uri.EscapeDataString(JsonSerializer.Serialize(new[] { mcVersion })));
        if (!string.IsNullOrEmpty(loader))
            parts.Add("loaders=" + Uri.EscapeDataString(JsonSerializer.Serialize(new[] { loader })));

        var qs = parts.Count > 0 ? "?" + string.Join("&", parts) : "";
        return await Http.GetFromJsonAsync<List<ModrinthVersion>>($"/v2/project/{Uri.EscapeDataString(projectId)}/version{qs}", cancellationToken) ?? [];
    }

    public async Task<ModrinthVersion?> GetVersionAsync(string versionId, CancellationToken cancellationToken = default)
    {
        return await Http.GetFromJsonAsync<ModrinthVersion>($"/v2/version/{Uri.EscapeDataString(versionId)}", cancellationToken);
    }

    // Reverse-lookup a mod by its jar's sha1 hash → returns the owning project info (title + icon).
    // Modrinth caches aggressively; our own process-memory dict dedupes calls across the UI session.
    private static readonly Dictionary<string, ModrinthProjectInfo?> _hashCache = new();

    public async Task<ModrinthProjectInfo?> LookupProjectByHashAsync(string sha1, CancellationToken cancellationToken = default)
    {
        if (_hashCache.TryGetValue(sha1, out var cached)) return cached;
        try
        {
            var ver = await Http.GetFromJsonAsync<ModrinthVersion>($"/v2/version_file/{sha1}?algorithm=sha1", cancellationToken);
            if (ver?.ProjectId == null) { _hashCache[sha1] = null; return null; }
            var proj = await GetProjectAsync(ver.ProjectId, cancellationToken);
            _hashCache[sha1] = proj;
            return proj;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return null; }
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
    [System.Text.Json.Serialization.JsonPropertyName("slug")]
    public string Slug { get; set; } = "";
    [System.Text.Json.Serialization.JsonPropertyName("body")]
    public string Body { get; set; } = "";
    [System.Text.Json.Serialization.JsonPropertyName("project_type")]
    public string ProjectType { get; set; } = "";
    [System.Text.Json.Serialization.JsonPropertyName("downloads")]
    public long Downloads { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("updated")]
    public DateTimeOffset? Updated { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("game_versions")]
    public List<string> GameVersions { get; set; } = [];
    [System.Text.Json.Serialization.JsonPropertyName("loaders")]
    public List<string> Loaders { get; set; } = [];
    [System.Text.Json.Serialization.JsonPropertyName("gallery")]
    public List<ModrinthGalleryImage> Gallery { get; set; } = [];
    [System.Text.Json.Serialization.JsonPropertyName("source_url")]
    public string? SourceUrl { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("issues_url")]
    public string? IssuesUrl { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("wiki_url")]
    public string? WikiUrl { get; set; }
}
