using System.Text.Json;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using MechanicaLauncher.Core.IO;

namespace MechanicaLauncher.Core.Game;

public static class JavaFinder
{
    private static readonly HttpClient Http = new();

    public static string? FindJava(string? preferredComponent = null, int? requiredMajor = null)
    {
        return SelectJava(FindAllJava(), preferredComponent, requiredMajor);
    }

    internal static string? SelectJava(IEnumerable<(string Path, int MajorVersion)> installations,
        string? preferredComponent, int? requiredMajor)
    {
        var found = installations.Where(j => j.MajorVersion > 0 &&
            (requiredMajor == null || j.MajorVersion == requiredMajor)).ToList();

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
            Path.Combine(LauncherPaths.DataDirectory, "shared", "runtime"),
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
                if (runtimeBase == mcRuntimePaths[0] && !File.Exists(Path.Combine(winDir, ".complete"))) continue;
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
        if (GetArchitecture(javaw) != RuntimeInformation.OSArchitecture) return;

        var version = GuessVersion(javaHome);
        list.Add((javaw, version));
    }

    internal static int GuessVersion(string javaHome)
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
                        return ParseMajorVersion(line.Split('=', 2)[1].Trim().Trim('"'));
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

    internal static int ParseMajorVersion(string version)
    {
        var parts = version.Split('.', '-', '+', '_');
        if (parts.Length == 0 || !int.TryParse(parts[0], out var major)) return 0;
        return major == 1 && parts.Length > 1 && int.TryParse(parts[1], out var legacy) ? legacy : major;
    }

    public static void ValidateJava(string javaPath, int requiredMajor)
    {
        if (!File.Exists(javaPath))
            throw new FileNotFoundException("Selected Java executable was not found. Choose Java again or use automatic selection.", javaPath);
        var javaHome = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(javaPath))!);
        var major = javaHome == null ? 0 : GuessVersion(javaHome);
        if (major != requiredMajor)
            throw new InvalidOperationException($"This version of Minecraft requires Java {requiredMajor}. Selected: {GetVersionLabel(javaPath)}. Choose the required Java or use automatic selection.");
        var architecture = GetArchitecture(javaPath);
        if (architecture != RuntimeInformation.OSArchitecture)
            throw new InvalidOperationException($"Java architecture must be {RuntimeInformation.OSArchitecture}. Selected: {architecture?.ToString() ?? "unknown or damaged executable"}.");
    }

    internal static Architecture? GetArchitecture(string javaPath)
    {
        try
        {
            using var stream = File.OpenRead(javaPath);
            using var pe = new PEReader(stream);
            return pe.PEHeaders.CoffHeader.Machine switch
            {
                Machine.Amd64 => Architecture.X64,
                Machine.I386 => Architecture.X86,
                Machine.Arm64 => Architecture.Arm64,
                _ => null
            };
        }
        catch (Exception ex) when (ex is IOException or BadImageFormatException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static async Task<string?> DownloadJavaAsync(string component, string gameDir, Action<string>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        onProgress?.Invoke("Fetching Java runtime info...");

        var manifestUrl = "https://launchermeta.mojang.com/v1/products/java-runtime/2ec0cc96c44e5a76b9c8b7c39df7210883d12871/all.json";
        var manifestJson = await Http.GetStringAsync(manifestUrl, cancellationToken);
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
        var runtimeJson = await Http.GetStringAsync(runtimeUrl, cancellationToken);
        var runtime = JsonSerializer.Deserialize<JsonElement>(runtimeJson);

        if (!runtime.TryGetProperty("files", out var files)) return null;

        var targetDir = FileDownloader.GetPath(gameDir, $"runtime/{component}/windows-x64/{component}");
        Directory.CreateDirectory(targetDir);

        var fileList = files.EnumerateObject().ToList();
        int downloaded = 0;

        foreach (var file in fileList)
        {
            var relPath = file.Name.Replace('/', Path.DirectorySeparatorChar);
            var fullPath = FileDownloader.GetPath(targetDir, relPath);

            if (file.Value.TryGetProperty("type", out var type))
            {
                if (type.GetString() == "directory")
                {
                    Directory.CreateDirectory(fullPath);
                    continue;
                }

                if (type.GetString() == "file")
                {
                    if (file.Value.TryGetProperty("downloads", out var downloads) &&
                        downloads.TryGetProperty("raw", out var raw) &&
                        raw.TryGetProperty("url", out var url))
                    {
                        var sha1 = raw.TryGetProperty("sha1", out var hash) ? hash.GetString() : null;
                        var size = raw.TryGetProperty("size", out var length) ? length.GetInt64() : 0;
                        await FileDownloader.EnsureAsync(Http, url.GetString()!, fullPath, sha1, size, cancellationToken);
                    }
                }
            }

            downloaded++;
            if (downloaded % 20 == 0)
                onProgress?.Invoke($"Java runtime ({downloaded}/{fileList.Count})...");
        }

        var javaw = Path.Combine(targetDir, "bin", "javaw.exe");
        if (!File.Exists(javaw)) return null;
        await AtomicFile.WriteTextAsync(Path.Combine(targetDir, ".complete"), component, cancellationToken);
        return javaw;
    }
}
