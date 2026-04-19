using System.Text.Json;
using MechanicaLauncher.Core.Instances;
using MechanicaLauncher.Core.Models;

namespace MechanicaLauncher.Core.Game;

public enum DiagnosticSeverity { Ok, Warning, Error }

public sealed class DiagnosticReport
{
    public DiagnosticSeverity Severity { get; init; }
    public string Title { get; init; } = "";
    public string Detail { get; init; } = "";
    public string? FixLabel { get; init; }
    public Func<Task>? Fix { get; init; }
}

public static class InstanceDiagnostics
{
    public static async Task<List<DiagnosticReport>> RunAsync(
        GameInstance inst,
        InstanceManager im,
        VersionManager vm,
        string? lastAccessToken)
    {
        var reports = new List<DiagnosticReport>();
        var gameDir = im.GetGameDir(inst.Id);
        var sharedLibs = Path.Combine(im.SharedDir, "libraries");
        var versionId = inst.GetEffectiveVersionId();

        // 1. Instance folder writable
        try
        {
            Directory.CreateDirectory(gameDir);
            var probe = Path.Combine(gameDir, ".write_probe");
            await File.WriteAllTextAsync(probe, "x");
            File.Delete(probe);
            reports.Add(new() { Severity = DiagnosticSeverity.Ok, Title = "Instance folder writable", Detail = gameDir });
        }
        catch (Exception ex)
        {
            reports.Add(new() { Severity = DiagnosticSeverity.Error, Title = "Instance folder not writable", Detail = ex.Message });
        }

        // 2. Vanilla client jar
        var vanillaJar = Path.Combine(gameDir, "versions", inst.McVersion, $"{inst.McVersion}.jar");
        if (File.Exists(vanillaJar) && new FileInfo(vanillaJar).Length > 1024)
        {
            reports.Add(new() { Severity = DiagnosticSeverity.Ok, Title = $"Vanilla {inst.McVersion} client.jar present", Detail = $"{new FileInfo(vanillaJar).Length / 1024} KB" });
        }
        else
        {
            reports.Add(new()
            {
                Severity = DiagnosticSeverity.Error,
                Title = $"Vanilla {inst.McVersion} client.jar missing",
                Detail = vanillaJar,
                FixLabel = "Remove and redownload",
                Fix = async () =>
                {
                    var dir = Path.GetDirectoryName(vanillaJar)!;
                    if (Directory.Exists(dir)) try { Directory.Delete(dir, true); } catch { }
                    await Task.CompletedTask;
                }
            });
        }

        // 3. Version JSON for loader
        string? versionJsonPath = null;
        VersionMeta? loaderMeta = null;
        if (inst.Loader != LoaderType.None)
        {
            versionJsonPath = Path.Combine(gameDir, "versions", versionId, $"{versionId}.json");
            if (!File.Exists(versionJsonPath))
            {
                reports.Add(new()
                {
                    Severity = DiagnosticSeverity.Error,
                    Title = $"{inst.Loader} version.json missing",
                    Detail = $"{versionJsonPath}\nInstance was built with an old/broken installer.",
                    FixLabel = "Remove instance",
                    Fix = async () =>
                    {
                        im.DeleteInstance(inst.Id);
                        await Task.CompletedTask;
                    }
                });
            }
            else
            {
                try
                {
                    loaderMeta = await vm.GetMergedMetaAsync(versionId, gameDir);
                    reports.Add(new() { Severity = DiagnosticSeverity.Ok, Title = $"{inst.Loader} version.json parsed", Detail = $"{loaderMeta.Libraries.Count} libraries merged" });
                }
                catch (Exception ex)
                {
                    reports.Add(new() { Severity = DiagnosticSeverity.Error, Title = $"{inst.Loader} version.json corrupt", Detail = ex.Message });
                }
            }
        }

        // 4. NeoForge/Forge patched client.jar
        if (inst.Loader == LoaderType.NeoForge && !string.IsNullOrEmpty(inst.LoaderVersion))
        {
            var clientJar = Path.Combine(sharedLibs, "net", "neoforged", "neoforge", inst.LoaderVersion,
                $"neoforge-{inst.LoaderVersion}-client.jar");
            if (File.Exists(clientJar))
                reports.Add(new() { Severity = DiagnosticSeverity.Ok, Title = "NeoForge patched client.jar present", Detail = $"{new FileInfo(clientJar).Length / 1024} KB" });
            else
                reports.Add(new()
                {
                    Severity = DiagnosticSeverity.Error,
                    Title = "NeoForge patched client.jar missing",
                    Detail = "Installer processors didn't run — forgeclient target will crash with ClassNotFoundException.\n" + clientJar,
                    FixLabel = "Re-run NeoForge installer",
                    Fix = async () =>
                    {
                        var ni = new NeoForgeInstaller(im.SharedDir, gameDir);
                        await ni.InstallAsync(inst.McVersion, inst.LoaderVersion!);
                    }
                });
        }

        // 5. Library presence (sample missing files)
        if (loaderMeta != null)
        {
            var missing = new List<string>();
            foreach (var lib in loaderMeta.Libraries)
            {
                if (!AssetDownloader.ShouldIncludeLibrary(lib)) continue;
                if (lib.Downloads?.Artifact is not { } artifact) continue;
                var p = Path.Combine(sharedLibs, artifact.Path.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(p)) missing.Add(artifact.Path);
                if (missing.Count >= 10) break;
            }
            if (missing.Count == 0)
                reports.Add(new() { Severity = DiagnosticSeverity.Ok, Title = "All runtime libraries present", Detail = $"checked {loaderMeta.Libraries.Count}" });
            else
                reports.Add(new()
                {
                    Severity = DiagnosticSeverity.Error,
                    Title = $"{missing.Count}+ libraries missing",
                    Detail = string.Join("\n", missing.Take(5)),
                    FixLabel = "Redownload (press Play)",
                });
        }

        // 6. Java version
        int requiredJava = 8;
        try
        {
            var vanillaEntry = (await vm.GetManifestAsync()).Versions.FirstOrDefault(v => v.Id == inst.McVersion);
            if (vanillaEntry != null)
            {
                var vmeta = await vm.GetVersionMetaAsync(vanillaEntry);
                requiredJava = vmeta.JavaVersion?.MajorVersion ?? 8;
            }
        }
        catch { }

        var javaPath = !string.IsNullOrEmpty(inst.JavaPath) && File.Exists(inst.JavaPath)
            ? inst.JavaPath
            : JavaFinder.FindJava();
        if (javaPath == null)
            reports.Add(new() { Severity = DiagnosticSeverity.Error, Title = "No Java installation found", Detail = $"Need Java {requiredJava}+" });
        else
        {
            var label = JavaFinder.GetVersionLabel(javaPath);
            var sev = DiagnosticSeverity.Ok;
            var detail = $"{label}\n{javaPath}";
            var match = System.Text.RegularExpressions.Regex.Match(label, @"\d+");
            if (match.Success && int.TryParse(match.Value, out var major) && major < requiredJava)
            {
                sev = DiagnosticSeverity.Warning;
                detail = $"Found {label}, need Java {requiredJava}+. Launcher will auto-download on Play.";
            }
            reports.Add(new() { Severity = sev, Title = $"Java: {label}", Detail = detail });
        }

        // 7. Mods
        var modsDir = Path.Combine(gameDir, "mods");
        if (Directory.Exists(modsDir))
        {
            var jars = Directory.GetFiles(modsDir, "*.jar");
            var disabled = Directory.GetFiles(modsDir, "*.jar.disabled");
            if (jars.Length > 0)
                reports.Add(new()
                {
                    Severity = DiagnosticSeverity.Ok,
                    Title = $"{jars.Length} active mod(s)",
                    Detail = disabled.Length > 0 ? $"{disabled.Length} disabled" : "",
                    FixLabel = jars.Length > 0 ? "Disable all mods" : null,
                    Fix = jars.Length > 0 ? async () =>
                    {
                        foreach (var j in jars)
                            try { File.Move(j, j + ".disabled"); } catch { }
                        await Task.CompletedTask;
                    } : null
                });
        }

        // 8. Auth token
        if (!string.IsNullOrEmpty(lastAccessToken) && lastAccessToken != "0")
        {
            reports.Add(new() { Severity = DiagnosticSeverity.Ok, Title = "Microsoft account token set", Detail = "" });
        }

        // 9. Latest crash report / latest.log / launcher-latest.log tail
        var crashDir = Path.Combine(gameDir, "crash-reports");
        string? lastCrash = null;
        if (Directory.Exists(crashDir))
            lastCrash = Directory.GetFiles(crashDir, "*.txt").OrderByDescending(File.GetLastWriteTime).FirstOrDefault();

        var latestLog = Path.Combine(gameDir, "logs", "latest.log");
        var launcherLog = Path.Combine(gameDir, "logs", "launcher-latest.log");
        string? errorCause = null;
        string? errorSource = null;

        async Task<string?> ReadTail(string path, int maxChars)
        {
            if (!File.Exists(path)) return null;
            try
            {
                var text = await File.ReadAllTextAsync(path);
                return text.Length > maxChars ? text[^maxChars..] : text;
            }
            catch { return null; }
        }

        foreach (var (source, path) in new[] { ("crash-report", lastCrash), ("latest.log", latestLog), ("launcher-latest.log", launcherLog) })
        {
            if (path == null) continue;
            var text = await ReadTail(path, 16000);
            if (text == null) continue;

            foreach (var pattern in new[]
            {
                @"Caused by:[^\n]+",
                @"Exception in thread[^\n]+",
                @"java\.lang\.\w+(Exception|Error)[^\n]*",
                @"\w+(Exception|Error):[^\n]+",
                @"at cpw\.mods\.bootstraplauncher[^\n]+",
                @"FATAL[^\n]+",
            })
            {
                var matches = System.Text.RegularExpressions.Regex.Matches(text, pattern);
                if (matches.Count == 0) continue;
                errorCause = matches[^1].Value.Trim();
                errorSource = source;
                break;
            }
            if (errorCause != null) break;
        }

        if (errorCause != null)
        {
            var openTarget = errorSource switch
            {
                "crash-report" => lastCrash!,
                "latest.log" => latestLog,
                _ => launcherLog
            };
            reports.Add(new()
            {
                Severity = DiagnosticSeverity.Error,
                Title = $"Error detected in {errorSource}",
                Detail = errorCause.Length > 500 ? errorCause[..500] + "..." : errorCause,
                FixLabel = "Open full log",
                Fix = async () =>
                {
                    if (File.Exists(openTarget))
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = openTarget, UseShellExecute = true });
                    await Task.CompletedTask;
                }
            });
        }
        else if (File.Exists(launcherLog))
        {
            var tail = await ReadTail(launcherLog, 2000);
            if (!string.IsNullOrWhiteSpace(tail))
                reports.Add(new()
                {
                    Severity = DiagnosticSeverity.Warning,
                    Title = "Last run produced no parseable error — tail below",
                    Detail = string.Join("\n", tail.Split('\n').TakeLast(15)),
                    FixLabel = "Open launcher-latest.log",
                    Fix = async () =>
                    {
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = launcherLog, UseShellExecute = true });
                        await Task.CompletedTask;
                    }
                });
        }

        // 10. hs_err dumps
        var hsErrs = Directory.Exists(gameDir)
            ? Directory.GetFiles(gameDir, "hs_err_pid*.log")
            : [];
        if (hsErrs.Length > 0)
        {
            var latest = hsErrs.OrderByDescending(File.GetLastWriteTime).First();
            string sig = "";
            try
            {
                var head = (await File.ReadAllTextAsync(latest))[..Math.Min(4096, (int)new FileInfo(latest).Length)];
                var m = System.Text.RegularExpressions.Regex.Match(head, @"SIG\w+|EXCEPTION_\w+");
                if (m.Success) sig = m.Value;
            }
            catch { }
            reports.Add(new()
            {
                Severity = DiagnosticSeverity.Error,
                Title = $"JVM crashed natively ({sig})",
                Detail = latest,
                FixLabel = "Open hs_err log",
                Fix = async () =>
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = latest, UseShellExecute = true });
                    await Task.CompletedTask;
                }
            });
        }

        return reports;
    }
}
