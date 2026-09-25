using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using MechanicaLauncher.Core.IO;
using MechanicaLauncher.Core.Security;

internal static class TLauncherTests
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> check, string root)
    {
        string Area(string name) { string path = Path.Combine(root, "tlauncher-tests", name); Directory.CreateDirectory(path); return path; }
        await check("Legacy names and historical brands cannot be treated as TLauncher", () =>
        {
            foreach (string name in new[] { "TLegacy", "TLauncher Legacy", "TL Legacy", "Legacy Launcher", "LegacyLauncher", "TLauncher-Legacy" })
                Require(!TLauncherDetector.IsTLauncherIdentity(name, "TLauncher Inc.", "https://tlauncher.org " + name));
            Require(TLauncherDetector.IsTLauncherIdentity("TLauncher", "TLauncher Inc.", ""));
            Require(!TLauncherDetector.IsTLauncherIdentity("TLauncher", "", "https://evil-tlauncher.org"));
            Require(!TLauncherDetector.IsTLauncherIdentity("TLauncher", "", "https://tlauncher.org.example.test"));
            return Task.CompletedTask;
        });
        await check("TLauncher and Legacy coexist without disabling the entire scan", async () =>
        {
            string area = Area("coexist"), app = Path.Combine(area, ".tlauncher");
            string config = Config(app);
            string ownJar = Jar(app, "TLauncher-2.99.jar");
            string legacyJar = Jar(Path.Combine(app, "legacy"), "TLauncher.jar", legacy: true);
            string renamed = Jar(app, "TLauncher.jar", legacy: true);
            var scan = TLauncherDetector.ScanFolder(app);
            Require(scan.IsDetected && scan.Files.Count == 2 && scan.ProtectedPaths.Contains(renamed));
            var result = await TLauncherCleaner.CleanAsync(scan, Path.Combine(area, "backups"));
            Require(result.Removed.Count == 2 && result.Skipped.Count == 0 && !File.Exists(config) && !File.Exists(ownJar));
            Require(File.Exists(legacyJar) && File.Exists(renamed));
            using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(result.BackupDirectory!, "manifest.json")));
            foreach (var file in manifest.RootElement.GetProperty("files").EnumerateArray())
            {
                string backup = Path.Combine(result.BackupDirectory!, file.GetProperty("backupFile").GetString()!);
                Require(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(backup))) == file.GetProperty("Sha256").GetString());
            }
        });
        await check("Cleanup preserves worlds, mods, runtimes, icons and unrelated executables", async () =>
        {
            string area = Area("game-data"), app = Path.Combine(area, ".minecraft");
            Config(app);
            string[] preserved = [Write(app, "OtherLauncher.exe", "executable"), Write(app, "minecraft.ico", "icon"),
                Write(app, "TLauncher.exe", "unverified name"), Write(app, "tlauncher-user-notes.json", "notes"),
                Jar(Path.Combine(app, "mods"), "TLauncher.jar"), Config(Path.Combine(app, "saves", "world")),
                Jar(Path.Combine(app, "runtime", "jre-legacy"), "TLauncher.jar"), Write(app, "servers.dat", "servers")];
            var hashes = preserved.ToDictionary(path => path, path => SHA256.HashData(File.ReadAllBytes(path)));
            var scan = TLauncherDetector.ScanFolder(app);
            Require(scan.Files.Count == 1);
            await TLauncherCleaner.CleanAsync(scan, Path.Combine(area, "backups"));
            foreach (var file in hashes) Require(File.ReadAllBytes(file.Key) is var bytes && SHA256.HashData(bytes).SequenceEqual(file.Value));
            Require(TLauncherDetector.ScanFolder(Path.Combine(app, "mods")).Files.Count == 0);
        });
        await check("Exclusive TLauncher roots, private Java and caches are removed with a complete backup", async () =>
        {
            string area = Area("complete-root"), app = Path.Combine(area, ".tlauncher");
            Config(app); Jar(app, "TLauncher.jar");
            Write(Path.Combine(app, "jre", "bin"), "javaw.exe", "private runtime");
            string cache = Write(Path.Combine(app, "cache", "nested"), "cache.bin", "cached data");
            File.SetAttributes(cache, FileAttributes.ReadOnly);
            Write(Path.Combine(app, "logs"), "launcher.log", "log");
            string legacy = Config(Path.Combine(area, "TLegacy"), "brand=legacy");
            string world = Write(Path.Combine(area, ".minecraft", "saves", "world"), "level.dat", "world");
            var plan = TLauncherDetector.ScanFolder(app);
            Require(plan.OwnedDirectories.SequenceEqual([app]) && plan.Files.Count == 5);
            var result = await TLauncherCleaner.CleanAsync(plan, Path.Combine(area, "backup"));
            Require(!Directory.Exists(app) && result.Skipped.Count == 0 && File.Exists(legacy) && File.Exists(world));
            using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(result.BackupDirectory!, "manifest.json")));
            Require(manifest.RootElement.GetProperty("files").GetArrayLength() == 5);
        });
        await check("Legacy in a shared launcher root prevents broad deletion of shared caches and Java", async () =>
        {
            string area = Area("shared-root"), app = Path.Combine(area, ".tlauncher");
            Config(app); Jar(app, "TLauncher.jar");
            string legacy = Jar(Path.Combine(app, "legacy"), "TLauncher.jar", legacy: true);
            string java = Write(Path.Combine(app, "jre"), "javaw.exe", "shared Java");
            string cache = Write(Path.Combine(app, "cache"), "cache.bin", "shared data");
            var plan = TLauncherDetector.ScanFolder(app);
            Require(plan.OwnedDirectories.Count == 0);
            await TLauncherCleaner.CleanAsync(plan, Path.Combine(area, "backup"));
            Require(File.Exists(legacy) && File.Exists(java) && File.Exists(cache));
        });
        await check("A locked renamed Legacy launcher cannot authorize cleanup of shared Java and caches", async () =>
        {
            foreach (bool locked in new[] { false, true })
            {
                string area = Area("renamed-shared-root-" + locked), app = Path.Combine(area, "TLauncher");
                string config = Config(app), ownJar = Jar(app, "TLauncher.jar");
                string legacy = Jar(app, "launcher.jar", legacy: true);
                string java = Write(Path.Combine(app, "jre", "bin"), "javaw.exe", "shared Java");
                string cache = Write(Path.Combine(app, "cache"), "cache.bin", "shared data");
                var hashes = new[] { legacy, java, cache }.ToDictionary(path => path, path => SHA256.HashData(File.ReadAllBytes(path)));
                using (var lease = locked ? new FileStream(legacy, FileMode.Open, FileAccess.Read, FileShare.None) : null)
                {
                    var plan = TLauncherDetector.ScanFolder(app);
                    Require(plan.OwnedDirectories.Count == 0 && plan.Directories.Count == 0);
                    Require(plan.Files.Count == 2 && plan.Files.Any(f => f.Path == config) && plan.Files.Any(f => f.Path == ownJar));
                    Require(plan.ProtectedPaths.Contains(legacy) && plan.ProtectedPaths.Contains(Path.Combine(app, "jre")) &&
                        plan.ProtectedPaths.Contains(Path.Combine(app, "cache")));
                    Require(!locked || plan.Warnings.Any(w => w.Contains(legacy)));
                    var result = await TLauncherCleaner.CleanAsync(plan, Path.Combine(area, "backup"));
                    Require(result.Removed.Count == 2 && result.Skipped.Count == 0 && !File.Exists(config) && !File.Exists(ownJar));
                }
                foreach (var file in hashes) Require(SHA256.HashData(File.ReadAllBytes(file.Key)).SequenceEqual(file.Value));
            }
        });
        await check("Legacy appearing after folder approval prevents whole-folder cleanup", async () =>
        {
            foreach (bool locked in new[] { false, true })
            {
                string area = Area("changed-root-" + locked), app = Path.Combine(area, "TLauncher");
                string config = Config(app), cache = Write(Path.Combine(app, "cache"), "shared.bin", "data");
                string java = Write(Path.Combine(app, "jre", "bin"), "javaw.exe", "shared Java");
                var plan = TLauncherDetector.ScanFolder(app);
                Require(plan.OwnedDirectories.Contains(app) && plan.Files.Any(f => f.Path == cache) && plan.Files.Any(f => f.Path == java));
                string legacy = Jar(app, "launcher.jar", legacy: true);
                var hashes = new[] { legacy, java, cache }.ToDictionary(path => path, path => SHA256.HashData(File.ReadAllBytes(path)));
                using (var lease = locked ? new FileStream(legacy, FileMode.Open, FileAccess.Read, FileShare.None) : null)
                {
                    var result = await TLauncherCleaner.CleanAsync(plan, Path.Combine(area, "backup"));
                    Require(result.Removed.Count == 1 && result.Skipped.Count == 0 && !File.Exists(config));
                }
                foreach (var file in hashes) Require(SHA256.HashData(File.ReadAllBytes(file.Key)).SequenceEqual(file.Value));
            }
        });
        await check("A whole-folder approval backs up newly written launcher cache before removal", async () =>
        {
            string area = Area("folder-approval"), app = Path.Combine(area, "TLauncher");
            Config(app); var plan = TLauncherDetector.ScanFolder(app);
            Write(Path.Combine(app, "cache"), "new.bin", "new launcher data");
            var result = await TLauncherCleaner.CleanAsync(plan, Path.Combine(area, "backup"));
            Require(!Directory.Exists(app) && result.Skipped.Count == 0);
            using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(result.BackupDirectory!, "manifest.json")));
            Require(manifest.RootElement.GetProperty("files").GetArrayLength() == 2);
        });
        await check("Game data left in a launcher folder does not retain the installation lock", async () =>
        {
            string area = Area("protected-game-root"), app = Path.Combine(area, "TLauncher");
            Config(app); string world = Write(Path.Combine(app, "saves"), "level.dat", "world");
            var plan = TLauncherDetector.ScanFolder(app);
            await TLauncherCleaner.CleanAsync(plan, Path.Combine(area, "backup"));
            var scan = TLauncherDetector.Rescan(plan, CancellationToken.None);
            Require(File.Exists(world) && !scan.HasCleanupItems);
        });
        if (OperatingSystem.IsWindows()) await TLauncherWindowsTests.RunAsync(check, Area("windows-artifacts"));
        await check("Legacy-only install, renamed jar and brand settings are excluded", () =>
        {
            string app = Area("legacy-only");
            Jar(app, "TLauncher.jar", legacy: true);
            Config(app, "bootstrap.brand=legacy\nserver=https://tlauncher.org\n");
            Write(app, "TlauncherProfiles.json", "{\"brand\":\"legacy\",\"server\":\"https://tlauncher.org\"}");
            Require(!TLauncherDetector.ScanFolder(app).IsDetected);
            return Task.CompletedTask;
        });
        await check("Names and broken archives alone never authorize cleanup", () =>
        {
            string app = Area("ambiguous");
            Config(app, "unknown=true"); Write(app, "TLauncher.jar", "not a zip");
            Write(app, "TLauncher.exe", "not an executable"); Write(app, "TlauncherProfiles.json", "{}");
            var scan = TLauncherDetector.ScanFolder(app);
            Require(!scan.IsDetected && scan.Files.Count == 0 && scan.Warnings.Count == 4);
            return Task.CompletedTask;
        });
        await check("Additional launcher folders are combined instead of replacing prior scan roots", () =>
        {
            string first = Area("additional-first"), second = Area("additional-second");
            string a = Config(first), b = Config(second);
            var scan = TLauncherDetector.Scan([first, second]);
            Require(scan.Files.Any(f => f.Path == a) && scan.Files.Any(f => f.Path == b));
            return Task.CompletedTask;
        });
        await check("Files changed since preview and files added later are not removed", async () =>
        {
            string area = Area("stale-plan"), app = Path.Combine(area, "app");
            string file = Config(app); var scan = TLauncherDetector.ScanFolder(app);
            File.AppendAllText(file, "changed=true\n"); Jar(app, "TLauncher.jar");
            var result = await TLauncherCleaner.CleanAsync(scan, Path.Combine(area, "backups"));
            Require(result.Removed.Count == 0 && result.Skipped.Count == 1 && result.BackupDirectory == null);
            Require(File.Exists(file) && File.Exists(Path.Combine(app, "TLauncher.jar")));
        });
        await check("A new matching file is outside the approved cleanup list", async () =>
        {
            string area = Area("new-file"), app = Path.Combine(area, "app");
            Config(app); var scan = TLauncherDetector.ScanFolder(app); string added = Jar(app, "TLauncher.jar");
            var result = await TLauncherCleaner.CleanAsync(scan, Path.Combine(area, "backups"));
            Require(result.Removed.Count == 1 && File.Exists(added));
        });
        await check("Unsafe or unavailable backup blocks removal", async () =>
        {
            string area = Area("backup-failure"), app = Path.Combine(area, "app");
            string file = Config(app); var scan = TLauncherDetector.ScanFolder(app);
            await Throws<InvalidOperationException>(() => TLauncherCleaner.CleanAsync(scan, Path.Combine(app, "backups")));
            string blocked = Write(area, "blocked", "file");
            await Throws<IOException>(() => TLauncherCleaner.CleanAsync(scan, Path.Combine(blocked, "backups")));
            Require(File.Exists(file));
        });
        await check("Cancelled backup never removes originals", async () =>
        {
            string area = Area("cancel"), app = Path.Combine(area, "app");
            string file = Config(app); Jar(app, "TLauncher.jar");
            using var cancel = new CancellationTokenSource();
            var progress = new CallbackProgress(_ => cancel.Cancel());
            await Throws<OperationCanceledException>(() => TLauncherCleaner.CleanAsync(TLauncherDetector.ScanFolder(app), Path.Combine(area, "backups"), progress, cancel.Token));
            Require(File.Exists(file) && File.Exists(Path.Combine(app, "TLauncher.jar")));
        });
        await check("Locked files are reported and left intact without killing processes", async () =>
        {
            string area = Area("locked"), app = Path.Combine(area, "app");
            string file = Config(app); var scan = TLauncherDetector.ScanFolder(app);
            using var locked = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            var result = await TLauncherCleaner.CleanAsync(scan, Path.Combine(area, "backups"));
            Require(result.Removed.Count == 0 && result.Skipped.Count == 1 && File.Exists(file) && result.BackupDirectory != null);
        });
        await check("Last-moment file replacement is rechecked before removal", async () =>
        {
            string area = Area("replacement"), app = Path.Combine(area, "app");
            string file = Config(app); var scan = TLauncherDetector.ScanFolder(app);
            var progress = new CallbackProgress(message => { if (message.StartsWith("Удаление:")) File.WriteAllText(file, "Legacy Launcher"); });
            var result = await TLauncherCleaner.CleanAsync(scan, Path.Combine(area, "backups"), progress);
            Require(result.Removed.Count == 0 && result.Skipped.Count == 1 && File.ReadAllText(file) == "Legacy Launcher");
        });
        await check("Directory junctions cannot extend the cleanup boundary", async () =>
        {
            string area = Area("junction"), app = Path.Combine(area, "app"), outside = Path.Combine(area, "outside");
            Config(app); string protectedFile = Config(outside);
            var plan = TLauncherDetector.ScanFolder(app);
            Directory.Move(app, Path.Combine(area, "original"));
            await Junction(app, outside, area);
            try
            {
                Require(TLauncherDetector.ScanFolder(app).Files.Count == 0);
                var result = await TLauncherCleaner.CleanAsync(plan, Path.Combine(area, "backups"));
                Require(result.Removed.Count == 0 && File.Exists(protectedFile));
            }
            finally { Directory.Delete(app); }
        });
        await check("Portable data stays beside the app while explicit overrides retain priority", () =>
        {
            string app = Area("portable"), roaming = Area("roaming"), custom = Area("custom-data");
            Require(LauncherPaths.ResolveDataDirectory(app, null, roaming) == Path.Combine(roaming, "MechanicaLauncher"));
            Write(app, "portable.flag", "");
            Require(LauncherPaths.ResolveDataDirectory(app, null, roaming) == Path.Combine(app, "data"));
            Require(LauncherPaths.ResolveDataDirectory(app, custom, roaming) == custom);
            return Task.CompletedTask;
        });
    }

    private static string Config(string directory, string content = "update=https://tlauncher.org/client\nminecraft.gamedir=game\n") => Write(directory, "tlauncher-2.0.properties", content);
    private static string Write(string directory, string name, string text)
    {
        Directory.CreateDirectory(directory); string path = Path.Combine(directory, name); File.WriteAllText(path, text); return path;
    }
    private static string Jar(string directory, string name, bool legacy = false)
    {
        Directory.CreateDirectory(directory); string path = Path.Combine(directory, name);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        zip.CreateEntry("org/tlauncher/tlauncher/rmo/TLauncher.class");
        zip.CreateEntry("org/tlauncher/modpack/domain/client/ModpackDTO.class");
        if (legacy) zip.CreateEntry("net/legacylauncher/LegacyLauncher.class");
        return path;
    }
    private static async Task Junction(string link, string target, string root)
    {
        string script = Write(root, "junction.ps1", "param($Link, $Target)\n$ErrorActionPreference = 'Stop'\nNew-Item -ItemType Junction -Path $Link -Value $Target | Out-Null\n");
        var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
        foreach (string arg in new[] { "-NoProfile", "-NonInteractive", "-File", script, "-Link", link, "-Target", target }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!; await process.WaitForExitAsync(); Require(process.ExitCode == 0);
    }
    private static void Require(bool condition) { if (!condition) throw new InvalidOperationException("TLauncher safety check failed."); }
    private static async Task Throws<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private sealed class CallbackProgress(Action<string> action) : IProgress<string> { public void Report(string value) => action(value); }
}
