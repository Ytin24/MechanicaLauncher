using System.Diagnostics;
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
                          string? extraJvmArgs = null)
    {
        var versionDir = Path.Combine(_gameDir, "versions", meta.Id);
        var jarPath = Path.Combine(versionDir, $"{meta.Id}.jar");
        var nativesDir = Path.Combine(versionDir, "natives");
        var assetsDir = Path.Combine(_gameDir, "assets");
        var assetIndex = meta.AssetIndex?.Id ?? meta.Assets;

        Directory.CreateDirectory(nativesDir);

        var classpath = BuildClasspath(meta, jarPath);

        var args = new List<string>();

        // JVM args
        args.Add($"-Xms{minMem}M");
        args.Add($"-Xmx{maxMem}M");
        args.Add($"-Djava.library.path={nativesDir}");
        args.Add("-Dminecraft.launcher.brand=mechanica-launcher");
        args.Add("-Dminecraft.launcher.version=1.0.0");

        if (!string.IsNullOrWhiteSpace(extraJvmArgs))
        {
            args.AddRange(extraJvmArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        }

        args.Add("-cp");
        args.Add(classpath);

        // Main class
        args.Add(meta.MainClass);

        // Game args
        args.Add("--username"); args.Add(username);
        args.Add("--version"); args.Add(meta.Id);
        args.Add("--gameDir"); args.Add(_gameDir);
        args.Add("--assetsDir"); args.Add(assetsDir);
        args.Add("--assetIndex"); args.Add(assetIndex);
        args.Add("--uuid"); args.Add(uuid);
        args.Add("--accessToken"); args.Add(accessToken);
        args.Add("--userType"); args.Add(accessToken == "0" ? "legacy" : "msa");
        args.Add("--versionType"); args.Add("release");

        var psi = new ProcessStartInfo
        {
            FileName = javaPath,
            WorkingDirectory = _gameDir,
            UseShellExecute = false,
        };

        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        return Process.Start(psi)!;
    }

    private string BuildClasspath(VersionMeta meta, string clientJar)
    {
        var paths = new List<string>();
        var librariesDir = Path.Combine(_gameDir, "libraries");

        foreach (var lib in meta.Libraries)
        {
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
        return Path.Combine(group, artifact, version, $"{artifact}-{version}.jar");
    }
}
