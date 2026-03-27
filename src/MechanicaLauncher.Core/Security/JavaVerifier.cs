using System.Security.Cryptography;
using System.Text.Json;

namespace MechanicaLauncher.Core.Security;

public sealed class JavaVerifyResult
{
    public bool IsVerified { get; set; }
    public int TotalFiles { get; set; }
    public int VerifiedFiles { get; set; }
    public int FailedFiles { get; set; }
    public List<string> FailedPaths { get; } = [];
}

public static class JavaVerifier
{
    private static readonly HttpClient Http = new();

    public static async Task<JavaVerifyResult> VerifyAsync(string runtimeDir, string component, Action<string>? onProgress = null)
    {
        var result = new JavaVerifyResult();

        var manifestUrl = "https://launchermeta.mojang.com/v1/products/java-runtime/2ec0cc96c44e5a76b9c8b7c39df7210883d12871/all.json";

        try
        {
            onProgress?.Invoke("Fetching Mojang manifest...");
            var allJson = await Http.GetStringAsync(manifestUrl);
            var all = JsonSerializer.Deserialize<JsonElement>(allJson);

            if (!all.TryGetProperty("windows-x64", out var platform)) { result.IsVerified = false; return result; }
            if (!platform.TryGetProperty(component, out var componentArr)) { result.IsVerified = false; return result; }

            string? runtimeManifestUrl = null;
            foreach (var entry in componentArr.EnumerateArray())
            {
                if (entry.TryGetProperty("manifest", out var m) && m.TryGetProperty("url", out var u))
                {
                    runtimeManifestUrl = u.GetString();
                    break;
                }
            }

            if (runtimeManifestUrl == null) { result.IsVerified = false; return result; }

            onProgress?.Invoke("Downloading file hashes...");
            var runtimeJson = await Http.GetStringAsync(runtimeManifestUrl);
            var runtime = JsonSerializer.Deserialize<JsonElement>(runtimeJson);

            if (!runtime.TryGetProperty("files", out var files)) { result.IsVerified = false; return result; }

            var targetDir = Path.Combine(runtimeDir, component, "windows-x64", component);

            foreach (var file in files.EnumerateObject())
            {
                if (!file.Value.TryGetProperty("type", out var type) || type.GetString() != "file") continue;
                if (!file.Value.TryGetProperty("downloads", out var downloads)) continue;
                if (!downloads.TryGetProperty("raw", out var raw)) continue;
                if (!raw.TryGetProperty("sha1", out var sha1Prop)) continue;

                var expectedHash = sha1Prop.GetString();
                if (string.IsNullOrEmpty(expectedHash)) continue;

                var relPath = file.Name.Replace('/', Path.DirectorySeparatorChar);
                var fullPath = Path.Combine(targetDir, relPath);

                result.TotalFiles++;

                if (!File.Exists(fullPath))
                {
                    result.FailedFiles++;
                    result.FailedPaths.Add($"MISSING: {relPath}");
                    continue;
                }

                var actualHash = ComputeSha1(fullPath);
                if (actualHash == expectedHash)
                {
                    result.VerifiedFiles++;
                }
                else
                {
                    result.FailedFiles++;
                    result.FailedPaths.Add($"MODIFIED: {relPath}");
                }
            }

            result.IsVerified = result.FailedFiles == 0;
            onProgress?.Invoke(result.IsVerified
                ? $"Verified: {result.VerifiedFiles}/{result.TotalFiles} files OK"
                : $"WARNING: {result.FailedFiles} files modified or missing!");
        }
        catch (Exception ex)
        {
            onProgress?.Invoke($"Verification failed: {ex.Message}");
            result.IsVerified = false;
        }

        return result;
    }

    private static string ComputeSha1(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        var hash = SHA1.HashData(stream);
        return Convert.ToHexStringLower(hash);
    }
}
