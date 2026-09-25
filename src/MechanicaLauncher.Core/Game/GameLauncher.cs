using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using MechanicaLauncher.Core.IO;
using MechanicaLauncher.Core.Models;

namespace MechanicaLauncher.Core.Game;

public sealed class GameLauncher
{
    private readonly string _instanceGameDir;
    private readonly string _sharedDir;

    public GameLauncher(string instanceGameDir, string sharedDir)
    {
        _instanceGameDir = instanceGameDir;
        _sharedDir = sharedDir;
    }

    public Process Launch(VersionMeta meta, string javaPath, string username,
                          string uuid = "0", string accessToken = "0",
                          int minMem = 2048, int maxMem = 4096,
                          string? extraJvmArgs = null,
                          int windowWidth = 1920, int windowHeight = 1080,
                          string? vanillaVersionId = null,
                          string? server = null, int? port = null) =>
        Process.Start(CreateStartInfo(meta, javaPath, username, uuid, accessToken, minMem, maxMem,
            extraJvmArgs, windowWidth, windowHeight, vanillaVersionId, server, port))
        ?? throw new InvalidOperationException("Failed to start Java process");

    internal ProcessStartInfo CreateStartInfo(VersionMeta meta, string javaPath, string username,
                          string uuid = "0", string accessToken = "0",
                          int minMem = 2048, int maxMem = 4096,
                          string? extraJvmArgs = null,
                          int windowWidth = 1920, int windowHeight = 1080,
                          string? vanillaVersionId = null,
                          string? server = null, int? port = null)
    {
        if (!File.Exists(javaPath))
            throw new FileNotFoundException("Selected Java executable was not found.", javaPath);
        if (minMem <= 0 || maxMem < minMem)
            throw new ArgumentException("Memory limits must be positive; maximum must be at least the minimum.");
        if (windowWidth <= 0 || windowHeight <= 0)
            throw new ArgumentException("Window width and height must be positive.");
        if (string.IsNullOrWhiteSpace(username)) throw new ArgumentException("Player name is required.");
        if (string.IsNullOrWhiteSpace(accessToken) || accessToken == "0")
        {
            accessToken = "0";
            uuid = OfflineUuid(username);
        }
        else if (!Guid.TryParse(uuid, out _))
            throw new ArgumentException("The Minecraft account UUID is invalid. Sign in again.");

        var clientVersionId = vanillaVersionId ?? meta.InheritsFrom ?? meta.Id;
        var versionDir = Path.Combine(_instanceGameDir, "versions", clientVersionId);
        var jarPath = Path.Combine(versionDir, $"{clientVersionId}.jar");
        var nativesDir = Path.Combine(versionDir, "natives");
        var assetsDir = Path.Combine(_sharedDir, "assets");
        var librariesDir = Path.Combine(_sharedDir, "libraries");
        var assetIndex = meta.AssetIndex?.Id ?? meta.Assets;

        if (!File.Exists(jarPath))
            throw new FileNotFoundException($"Client jar not found: {jarPath}");

        Directory.CreateDirectory(nativesDir);

        // Only NeoForge/Forge 1.17+ (BootstrapLauncher) and ModLauncher-based Forge 1.13–1.16 expect
        // the vanilla client jar off the classpath — they resolve minecraft via JPMS module path
        // or via -DminecraftJars launch args respectively. Fabric, Quilt, legacy LaunchWrapper Forge
        // and vanilla itself all require the vanilla jar on the classpath to find minecraft classes.
        var includeVanillaJar = meta.MainClass is not (
            "cpw.mods.bootstraplauncher.BootstrapLauncher" or
            "cpw.mods.modlauncher.Launcher");
        var classpath = BuildClasspath(meta, jarPath, librariesDir, includeVanillaJar);

        var vars = new Dictionary<string, string>
        {
            ["${auth_player_name}"] = username,
            ["${version_name}"] = meta.Id,
            ["${game_directory}"] = _instanceGameDir,
            ["${assets_root}"] = assetsDir,
            ["${assets_index_name}"] = assetIndex,
            ["${auth_uuid}"] = uuid,
            ["${auth_access_token}"] = accessToken,
            ["${clientid}"] = "",
            ["${auth_xuid}"] = "",
            ["${user_properties}"] = "{}",
            ["${user_type}"] = accessToken == "0" ? "legacy" : "msa",
            ["${version_type}"] = meta.Type.Length > 0 ? meta.Type : "release",
            ["${natives_directory}"] = nativesDir,
            ["${launcher_name}"] = "mechanica-launcher",
            ["${launcher_version}"] = "1.0.0",
            ["${classpath}"] = classpath,
            ["${resolution_width}"] = windowWidth.ToString(),
            ["${resolution_height}"] = windowHeight.ToString(),
            ["${library_directory}"] = librariesDir,
            ["${classpath_separator}"] = Path.PathSeparator.ToString(),
        };

        var args = new List<string> { $"-Xms{minMem}M", $"-Xmx{maxMem}M", "-Dminecraft.api.env.disableDiscord=true" };

        if (meta.Logging.TryGetValue("client", out var logging))
        {
            var path = FileDownloader.GetPath(_sharedDir, $"assets/log_configs/{logging.File.Id}");
            if (!File.Exists(path)) throw new FileNotFoundException("Minecraft logging configuration is missing. Run installation again.", path);
            args.Add(logging.Argument.Replace("${path}", path));
        }

        if (meta.Arguments?.Jvm != null)
        {
            foreach (var jvmArg in ResolveArgs(meta.Arguments.Jvm, vars))
                args.Add(jvmArg);
        }
        else
        {
            args.AddRange([
                $"-Djava.library.path={nativesDir}",
                "-Dminecraft.launcher.brand=mechanica-launcher",
                "-Dminecraft.launcher.version=1.0.0",
                "-cp", classpath
            ]);
        }

        if (!string.IsNullOrWhiteSpace(extraJvmArgs))
            args.AddRange(SplitArguments(extraJvmArgs));

        args.Add(meta.MainClass);

        if (meta.Arguments?.Game != null)
        {
            foreach (var gameArg in ResolveArgs(meta.Arguments.Game, vars))
                args.Add(gameArg);
        }
        else if (!string.IsNullOrEmpty(meta.MinecraftArguments))
        {
            foreach (var arg in meta.MinecraftArguments.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                args.Add(Substitute(arg, vars));
        }
        else
        {
            args.AddRange(["--username", username, "--version", meta.Id,
                "--gameDir", _instanceGameDir, "--assetsDir", assetsDir,
                "--assetIndex", assetIndex, "--uuid", uuid,
                "--accessToken", accessToken,
                "--userType", accessToken == "0" ? "legacy" : "msa",
                "--versionType", "release"]);
        }

        if (!string.IsNullOrEmpty(server))
        {
            args.Add("--server");
            args.Add(server);
            args.Add("--port");
            args.Add((port ?? 25565).ToString());
        }

        var psi = new ProcessStartInfo
        {
            FileName = javaPath,
            WorkingDirectory = _instanceGameDir,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        return psi;
    }

    internal static string OfflineUuid(string username)
    {
        var bytes = MD5.HashData(Encoding.UTF8.GetBytes("OfflinePlayer:" + username));
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x30);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return Guid.ParseExact(Convert.ToHexString(bytes), "N").ToString("D");
    }

    internal static List<string> SplitArguments(string arguments)
    {
        var result = new List<string>();
        var value = new StringBuilder();
        bool quoted = false, started = false;
        for (int i = 0; i < arguments.Length; i++)
        {
            var ch = arguments[i];
            if (char.IsWhiteSpace(ch) && !quoted)
            {
                if (started) result.Add(value.ToString());
                value.Clear();
                started = false;
                continue;
            }
            started = true;
            if (ch == '\\')
            {
                int count = 1;
                while (i + 1 < arguments.Length && arguments[i + 1] == '\\') { count++; i++; }
                if (i + 1 < arguments.Length && arguments[i + 1] == '"')
                {
                    value.Append('\\', count / 2);
                    i++;
                    if (count % 2 == 1) value.Append('"');
                    else quoted = !quoted;
                }
                else value.Append('\\', count);
            }
            else if (ch == '"') quoted = !quoted;
            else value.Append(ch);
        }
        if (quoted) throw new ArgumentException("Unclosed quote in JVM arguments.");
        if (started) result.Add(value.ToString());
        return result;
    }

    private static List<string> ResolveArgs(List<JsonElement> jsonArgs, Dictionary<string, string> vars)
    {
        var result = new List<string>();
        foreach (var el in jsonArgs)
        {
            if (el.ValueKind == JsonValueKind.String)
            {
                result.Add(Substitute(el.GetString()!, vars));
            }
            else if (el.ValueKind == JsonValueKind.Object)
            {
                if (el.TryGetProperty("rules", out var rules) && !EvaluateRules(rules))
                    continue;
                if (el.TryGetProperty("value", out var value))
                {
                    if (value.ValueKind == JsonValueKind.String)
                        result.Add(Substitute(value.GetString()!, vars));
                    else if (value.ValueKind == JsonValueKind.Array)
                        foreach (var item in value.EnumerateArray())
                            if (item.ValueKind == JsonValueKind.String)
                                result.Add(Substitute(item.GetString()!, vars));
                }
            }
        }
        return result;
    }

    private static bool EvaluateRules(JsonElement rules) => LaunchRules.Evaluate(
        rules.Deserialize<List<Rule>>(), new Dictionary<string, bool> { ["has_custom_resolution"] = true });

    private static string Substitute(string template, Dictionary<string, string> vars)
    {
        foreach (var (key, value) in vars)
            template = template.Replace(key, value);
        return template;
    }

    internal static string BuildClasspath(VersionMeta meta, string clientJar, string librariesDir, bool includeClientJar)
    {
        var paths = new List<string>();
        foreach (var lib in meta.Libraries)
        {
            if (!AssetDownloader.ShouldIncludeLibrary(lib)) continue;
            if (AssetDownloader.GetArtifact(lib) is { } artifact)
            {
                var p = FileDownloader.GetPath(librariesDir, artifact.Path);
                if (!File.Exists(p) || new FileInfo(p).Length == 0)
                    throw new FileNotFoundException($"Required library is missing: {lib.Name}. Run installation again.", p);
                paths.Add(p);
            }
        }
        if (includeClientJar) paths.Add(clientJar);
        return string.Join(Path.PathSeparator, paths.Distinct(StringComparer.OrdinalIgnoreCase));
    }
}
