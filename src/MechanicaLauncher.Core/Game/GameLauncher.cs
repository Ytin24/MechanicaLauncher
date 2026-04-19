using System.Diagnostics;
using System.Text.Json;
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
                          string? server = null, int? port = null)
    {
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

        // For modded launches (inheritsFrom = vanilla), the unpatched vanilla jar must NOT be on the
        // classpath — NeoForge/Forge/Fabric ship patched or remapped Minecraft classes as libraries
        // (net.minecraft:client:<ver>-<neoform>:slim for NeoForge, intermediary-mapped jars for Fabric),
        // and including the raw vanilla jar makes fancymodloader/Knot load the wrong minecraft classes,
        // producing ClassNotFoundException on transformed symbols at runtime.
        var includeVanillaJar = string.IsNullOrEmpty(meta.InheritsFrom);
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
            args.AddRange(extraJvmArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries));

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
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        return Process.Start(psi) ?? throw new Exception("Failed to start Java process");
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

    private static bool EvaluateRules(JsonElement rules)
    {
        bool anyAllow = false;
        foreach (var rule in rules.EnumerateArray())
        {
            var action = rule.GetProperty("action").GetString();
            if (rule.TryGetProperty("features", out _)) continue;
            if (rule.TryGetProperty("os", out var os))
            {
                if (os.TryGetProperty("name", out var name))
                {
                    bool isWin = name.GetString() == "windows";
                    if (action == "allow" && isWin) anyAllow = true;
                    if (action == "disallow" && isWin) return false;
                }
                else if (action == "allow") anyAllow = true;
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

    private static string BuildClasspath(VersionMeta meta, string clientJar, string librariesDir, bool includeClientJar)
    {
        var paths = new List<string>();
        foreach (var lib in meta.Libraries)
        {
            if (!AssetDownloader.ShouldIncludeLibrary(lib)) continue;
            if (lib.Downloads?.Artifact is { } artifact)
            {
                var p = Path.Combine(librariesDir, artifact.Path.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(p)) paths.Add(p);
            }
            else
            {
                var maven = MavenToPath(lib.Name);
                if (maven != null)
                {
                    var p = Path.Combine(librariesDir, maven);
                    if (File.Exists(p)) paths.Add(p);
                }
            }
        }
        if (includeClientJar) paths.Add(clientJar);
        return string.Join(Path.PathSeparator, paths);
    }

    private static string? MavenToPath(string name)
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
