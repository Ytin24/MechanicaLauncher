using System.Diagnostics;
using System.Text.Json;
using MechanicaLauncher.Core.Models;

namespace MechanicaLauncher.Core.Game;

public sealed class GameLauncher
{
    private readonly string _gameDir;

    public GameLauncher(string gameDir)
    {
        _gameDir = gameDir;
    }

    public Process Launch(VersionMeta meta, string javaPath, string username,
                          string uuid = "0", string accessToken = "0",
                          int minMem = 2048, int maxMem = 4096,
                          string? extraJvmArgs = null,
                          int windowWidth = 1920, int windowHeight = 1080)
    {
        var versionDir = Path.Combine(_gameDir, "versions", meta.Id);
        var jarPath = Path.Combine(versionDir, $"{meta.Id}.jar");
        var nativesDir = Path.Combine(versionDir, "natives");
        var assetsDir = Path.Combine(_gameDir, "assets");
        var assetIndex = meta.AssetIndex?.Id ?? meta.Assets;

        if (!File.Exists(jarPath))
            throw new FileNotFoundException($"Client jar not found: {jarPath}");

        Directory.CreateDirectory(nativesDir);

        var vars = new Dictionary<string, string>
        {
            ["${auth_player_name}"] = username,
            ["${version_name}"] = meta.Id,
            ["${game_directory}"] = _gameDir,
            ["${assets_root}"] = assetsDir,
            ["${assets_index_name}"] = assetIndex,
            ["${auth_uuid}"] = uuid,
            ["${auth_access_token}"] = accessToken,
            ["${clientid}"] = "",
            ["${auth_xuid}"] = "",
            ["${user_type}"] = accessToken == "0" ? "legacy" : "msa",
            ["${version_type}"] = meta.Type.Length > 0 ? meta.Type : "release",
            ["${natives_directory}"] = nativesDir,
            ["${launcher_name}"] = "mechanica-launcher",
            ["${launcher_version}"] = "1.0.0",
            ["${classpath}"] = BuildClasspath(meta, jarPath),
            ["${resolution_width}"] = windowWidth.ToString(),
            ["${resolution_height}"] = windowHeight.ToString(),
            ["${library_directory}"] = Path.Combine(_gameDir, "libraries"),
            ["${classpath_separator}"] = Path.PathSeparator.ToString(),
        };

        var args = new List<string>();

        args.Add($"-Xms{minMem}M");
        args.Add($"-Xmx{maxMem}M");

        if (meta.Arguments?.Jvm != null)
        {
            foreach (var jvmArg in ResolveArgs(meta.Arguments.Jvm, vars))
                args.Add(jvmArg);
        }
        else
        {
            args.Add($"-Djava.library.path={nativesDir}");
            args.Add($"-Dminecraft.launcher.brand=mechanica-launcher");
            args.Add($"-Dminecraft.launcher.version=1.0.0");
            args.Add("-cp");
            args.Add(vars["${classpath}"]);
        }

        if (!string.IsNullOrWhiteSpace(extraJvmArgs))
            args.AddRange(extraJvmArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries));

        args.Add(meta.MainClass);

        if (meta.Arguments?.Game != null)
        {
            foreach (var gameArg in ResolveArgs(meta.Arguments.Game, vars))
                args.Add(gameArg);
        }
        else
        {
            args.AddRange(["--username", username, "--version", meta.Id,
                "--gameDir", _gameDir, "--assetsDir", assetsDir,
                "--assetIndex", assetIndex, "--uuid", uuid,
                "--accessToken", accessToken,
                "--userType", accessToken == "0" ? "legacy" : "msa",
                "--versionType", "release"]);
        }

        var psi = new ProcessStartInfo
        {
            FileName = javaPath,
            WorkingDirectory = _gameDir,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };

        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        var proc = Process.Start(psi);
        if (proc == null)
            throw new Exception("Failed to start Java process");

        return proc;
    }

    private List<string> ResolveArgs(List<JsonElement> jsonArgs, Dictionary<string, string> vars)
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
                if (el.TryGetProperty("rules", out var rules))
                {
                    if (!EvaluateRules(rules)) continue;
                }

                if (el.TryGetProperty("value", out var value))
                {
                    if (value.ValueKind == JsonValueKind.String)
                    {
                        result.Add(Substitute(value.GetString()!, vars));
                    }
                    else if (value.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in value.EnumerateArray())
                        {
                            if (item.ValueKind == JsonValueKind.String)
                                result.Add(Substitute(item.GetString()!, vars));
                        }
                    }
                }
            }
        }

        return result;
    }

    private static bool EvaluateRules(JsonElement rules)
    {
        bool anyAllow = false;

        foreach (var rule in rules.EnumerateArray())
        {
            var action = rule.GetProperty("action").GetString();

            if (rule.TryGetProperty("features", out _))
                continue;

            if (rule.TryGetProperty("os", out var os))
            {
                if (os.TryGetProperty("name", out var name))
                {
                    var osName = name.GetString();
                    bool isMatch = osName == "windows";

                    if (action == "allow" && isMatch) anyAllow = true;
                    if (action == "allow" && !isMatch) continue;
                    if (action == "disallow" && isMatch) return false;
                }
                else
                {
                    if (action == "allow") anyAllow = true;
                }
            }
            else
            {
                if (action == "allow") anyAllow = true;
                if (action == "disallow") return false;
            }
        }

        return anyAllow;
    }

    private static string Substitute(string template, Dictionary<string, string> vars)
    {
        foreach (var (key, value) in vars)
            template = template.Replace(key, value);
        return template;
    }

    private string BuildClasspath(VersionMeta meta, string clientJar)
    {
        var paths = new List<string>();
        var librariesDir = Path.Combine(_gameDir, "libraries");

        foreach (var lib in meta.Libraries)
        {
            if (!AssetDownloader.ShouldIncludeLibrary(lib)) continue;

            if (lib.Downloads?.Artifact is { } artifact)
            {
                var libPath = Path.Combine(librariesDir, artifact.Path.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(libPath))
                    paths.Add(libPath);
            }
            else
            {
                var mavenPath = MavenNameToPath(lib.Name);
                if (mavenPath != null)
                {
                    var libPath = Path.Combine(librariesDir, mavenPath);
                    if (File.Exists(libPath))
                        paths.Add(libPath);
                }
            }
        }

        paths.Add(clientJar);
        return string.Join(Path.PathSeparator, paths);
    }

    private static string? MavenNameToPath(string name)
    {
        var parts = name.Split(':');
        if (parts.Length < 3) return null;
        var group = parts[0].Replace('.', Path.DirectorySeparatorChar);
        var artifact = parts[1];
        var version = parts[2];
        if (parts.Length >= 4)
            return Path.Combine(group, artifact, version, $"{artifact}-{version}-{parts[3]}.jar");
        return Path.Combine(group, artifact, version, $"{artifact}-{version}.jar");
    }
}
