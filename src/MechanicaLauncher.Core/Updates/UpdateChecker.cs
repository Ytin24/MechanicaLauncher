using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace MechanicaLauncher.Core.Updates;

public sealed class UpdateInfo
{
    public bool IsAvailable { get; set; }
    public string CurrentVersion { get; set; } = "";
    public string LatestVersion { get; set; } = "";
    public string? DownloadUrl { get; set; }
    public string? ReleaseUrl { get; set; }
}

public static class UpdateChecker
{
    private static readonly HttpClient Http = new();
    private const string CurrentVersion = "2.1.1";
    private const string RepoApi = "https://api.github.com/repos/Ytin24/MechanicaLauncher/releases/latest";

    static UpdateChecker()
    {
        Http.DefaultRequestHeaders.Add("User-Agent", "MechanicaLauncher");
    }

    public static async Task<UpdateInfo> CheckAsync()
    {
        var result = new UpdateInfo { CurrentVersion = CurrentVersion };

        try
        {
            var release = await Http.GetFromJsonAsync<GitHubRelease>(RepoApi);
            if (release == null) return result;

            var latest = release.TagName?.TrimStart('v') ?? "";
            result.LatestVersion = latest;
            result.ReleaseUrl = release.HtmlUrl;

            var asset = release.Assets?.FirstOrDefault(a =>
                a.Name?.Contains("setup", StringComparison.OrdinalIgnoreCase) == true);
            result.DownloadUrl = asset?.DownloadUrl;

            if (IsNewer(latest, CurrentVersion))
                result.IsAvailable = true;
        }
        catch { }

        return result;
    }

    private static bool IsNewer(string latest, string current)
    {
        var lParts = latest.Split('.').Select(s => int.TryParse(s, out var n) ? n : 0).ToArray();
        var cParts = current.Split('.').Select(s => int.TryParse(s, out var n) ? n : 0).ToArray();

        for (int i = 0; i < Math.Max(lParts.Length, cParts.Length); i++)
        {
            var l = i < lParts.Length ? lParts[i] : 0;
            var c = i < cParts.Length ? cParts[i] : 0;
            if (l > c) return true;
            if (l < c) return false;
        }

        return false;
    }
}

file sealed class GitHubRelease
{
    [JsonPropertyName("tag_name")]
    public string? TagName { get; set; }

    [JsonPropertyName("html_url")]
    public string? HtmlUrl { get; set; }

    [JsonPropertyName("assets")]
    public List<GitHubAsset>? Assets { get; set; }
}

file sealed class GitHubAsset
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("browser_download_url")]
    public string? DownloadUrl { get; set; }
}
