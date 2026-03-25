using System.Net.Http.Json;
using System.Text.Json;

namespace MechanicaLauncher.Core.Game;

public static class JavaFinder
{
    private static readonly HttpClient Http = new();

    public static string? FindJava(string? preferredComponent = null)
    {
        var found = FindAllJava();

        if (preferredComponent != null)
        {
            var match = found.FirstOrDefault(j => j.Path.Contains(preferredComponent, StringComparison.OrdinalIgnoreCase));
            if (match != default) return match.Path;
        }

        if (found.Count == 0) return null;
        return found.OrderByDescending(j => j.MajorVersion).First().Path;
    }

    public static List<(string Path, int MajorVersion)> FindAllJava()
    {
        var results = new List<(string Path, int MajorVersion)>();

        var javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrEmpty(javaHome))
            TryAdd(results, javaHome);

        var mcRuntimePaths = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "MechanicaLauncher", "shared", "runtime"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Packages", "Microsoft.4297127D64EC6_8wekyb3d8bbwe", "LocalCache", "Local", "runtime"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                ".minecraft", "runtime"),
        };

        foreach (var runtimeBase in mcRuntimePaths)
        {
            if (!Directory.Exists(runtimeBase)) continue;
            foreach (var componentDir in Directory.GetDirectories(runtimeBase))
            {
                var winDir = Path.Combine(componentDir, "windows-x64", Path.GetFileName(componentDir));
                TryAdd(results, winDir);
                TryAdd(results, componentDir);
            }
        }

        string[] installPaths =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Java"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Eclipse Adoptium"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Zulu"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Amazon Corretto"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".jdks"),
        ];

        foreach (var basePath in installPaths)
        {
            if (!Directory.Exists(basePath)) continue;
            foreach (var dir in Directory.GetDirectories(basePath).OrderByDescending(d => d))
                TryAdd(results, dir);
        }

        var pathDirs = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? [];
        foreach (var dir in pathDirs)
        {
            var javaw = Path.Combine(dir, "javaw.exe");
            if (File.Exists(javaw))
            {
                var parentDir = Path.GetDirectoryName(dir);
                if (parentDir != null)
                    TryAdd(results, parentDir);
            }
        }

        return results.DistinctBy(r => r.Path).ToList();
    }

    private static void TryAdd(List<(string Path, int MajorVersion)> list, string javaHome)
    {
        var javaw = Path.Combine(javaHome, "bin", "javaw.exe");
        if (!File.Exists(javaw)) return;

        var version = GuessVersion(javaHome);
        list.Add((javaw, version));
    }

    private static int GuessVersion(string javaHome)
    {
        var release = Path.Combine(javaHome, "release");
        if (File.Exists(release))
        {
            try
            {
                foreach (var line in File.ReadLines(release))
                {
                    if (line.StartsWith("JAVA_VERSION="))
                    {
                        var ver = line.Split('=')[1].Trim('"');
                        var parts = ver.Split('.');
                        if (int.TryParse(parts[0], out var major))
                            return major;
                    }
                }
            }
            catch { }
        }

        var dirName = Path.GetFileName(javaHome) ?? "";
        foreach (var part in dirName.Split('-', '.', '_'))
        {
            if (int.TryParse(part, out var num) && num >= 8 && num <= 50)
                return num;
        }

        return 0;
    }

    public static string GetVersionLabel(string javaPath)
    {
        var dir = Path.GetDirectoryName(javaPath);
        if (dir == null) return "Unknown";
        var javaHome = Path.GetDirectoryName(dir);
        if (javaHome == null) return "Unknown";

        var version = GuessVersion(javaHome);
        return version > 0 ? $"Java {version}" : Path.GetFileName(javaHome) ?? "Unknown";
    }

    public static async Task<string?> DownloadJavaAsync(string component, string gameDir, Action<string>? onProgress = null)
    {
        onProgress?.Invoke("Fetching Java runtime info...");

        var manifestUrl = "https://launchermeta.mojang.com/v1/products/java-runtime/2ec0cc96c44e5a76b9c8b7c39df7210883d12871/all.json";
        var manifestJson = await Http.GetStringAsync(manifestUrl);
        var manifest = JsonSerializer.Deserialize<JsonElement>(manifestJson);

        if (!manifest.TryGetProperty("windows-x64", out var platform)) return null;
        if (!platform.TryGetProperty(component, out var componentArr)) return null;

        string? runtimeUrl = null;
        foreach (var entry in componentArr.EnumerateArray())
        {
            if (entry.TryGetProperty("manifest", out var m) && m.TryGetProperty("url", out var u))
            {
                runtimeUrl = u.GetString();
                break;
            }
        }

        if (runtimeUrl == null) return null;

        onProgress?.Invoke("Downloading Java runtime manifest...");
        var runtimeJson = await Http.GetStringAsync(runtimeUrl);
        var runtime = JsonSerializer.Deserialize<JsonElement>(runtimeJson);

        if (!runtime.TryGetProperty("files", out var files)) return null;

        var targetDir = Path.Combine(gameDir, "runtime", component, "windows-x64", component);
        Directory.CreateDirectory(targetDir);

        var fileList = files.EnumerateObject().ToList();
        int downloaded = 0;

        foreach (var file in fileList)
        {
            var relPath = file.Name.Replace('/', Path.DirectorySeparatorChar);
            var fullPath = Path.Combine(targetDir, relPath);

            if (file.Value.TryGetProperty("type", out var type))
            {
                if (type.GetString() == "directory")
                {
                    Directory.CreateDirectory(fullPath);
                    continue;
                }

                if (type.GetString() == "file" && !File.Exists(fullPath))
                {
                    if (file.Value.TryGetProperty("downloads", out var downloads) &&
                        downloads.TryGetProperty("raw", out var raw) &&
                        raw.TryGetProperty("url", out var url))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                        var bytes = await Http.GetByteArrayAsync(url.GetString()!);
                        await File.WriteAllBytesAsync(fullPath, bytes);
                    }
                }
            }

            downloaded++;
            if (downloaded % 20 == 0)
                onProgress?.Invoke($"Java runtime ({downloaded}/{fileList.Count})...");
        }

        var javaw = Path.Combine(targetDir, "bin", "javaw.exe");
        return File.Exists(javaw) ? javaw : null;
    }
}
