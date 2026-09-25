using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using MechanicaLauncher.Core.Security;
using Microsoft.Win32;

[SupportedOSPlatform("windows")]
internal static class TLauncherWindowsTests
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> check, string area)
    {
        await check("Windows cleanup removes verified shortcuts, startup values and registration while preserving Legacy", async () =>
        {
            string testKey = @"Software\MechanicaLauncher.Tests\" + Guid.NewGuid().ToString("N");
            string app = Path.Combine(area, "TLauncher"), shortcuts = Path.Combine(area, "shortcuts");
            Directory.CreateDirectory(app); Directory.CreateDirectory(shortcuts);
            string jar = Path.Combine(app, "TLauncher.jar");
            using (var zip = ZipFile.Open(jar, ZipArchiveMode.Create))
            { zip.CreateEntry("org/tlauncher/tlauncher/rmo/TLauncher.class"); zip.CreateEntry("org/tlauncher/modpack/domain/client/ModpackDTO.class"); }
            string command = "javaw.exe -jar \"" + jar + "\"";
            var scope = new TLauncherWindows.RegistryArea(RegistryHive.CurrentUser, RegistryView.Registry64,
                testKey + @"\Uninstall", testKey + @"\Run", testKey + @"\RunOnce", testKey + @"\TLauncher");
            using var root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
            try
            {
                using (var key = root.CreateSubKey(scope.Uninstall + @"\TLauncher"))
                {
                    key.SetValue("DisplayName", "TLauncher"); key.SetValue("Publisher", "TLauncher Inc."); key.SetValue("InstallLocation", app);
                    key.SetValue("Binary", new byte[] { 0, 1, 255 }, RegistryValueKind.Binary);
                }
                using (var key = root.CreateSubKey(scope.Uninstall + @"\Legacy"))
                { key.SetValue("DisplayName", "TLauncher Legacy"); key.SetValue("Publisher", "TLauncher Inc."); }
                using (var key = root.CreateSubKey(scope.Run))
                { key.SetValue("TLauncher", command); key.SetValue("Other", "other.exe"); key.SetValue("Legacy", command + " --brand legacy"); }
                using (var key = root.CreateSubKey(scope.Settings)) key.SetValue("server", "https://tlauncher.org");
                string menu = Path.Combine(shortcuts, "TLauncher"); Directory.CreateDirectory(menu);
                string link = Path.Combine(menu, "Play.lnk"), legacyLink = Path.Combine(shortcuts, "Legacy.lnk");
                Shortcut(link, jar); Shortcut(legacyLink, jar, legacy: true);
                var scan = TLauncherWindows.AddArtifacts(TLauncherDetector.ScanFolder(app), [scope], [shortcuts]);
                Require(scan.RegistryEntries.Count == 3 && scan.Files.Any(f => f.Path == link && f.Shortcut));
                Require(!TLauncherCleaner.RequiresElevation(scan));
                var result = await TLauncherCleaner.CleanAsync(scan, Path.Combine(area, "backups"));
                Require(result.Skipped.Count == 0 && !Directory.Exists(app) && !Directory.Exists(menu) && !File.Exists(link) && File.Exists(legacyLink));
                using var remaining = root.OpenSubKey(scope.Run);
                Require(remaining?.GetValue("TLauncher") == null && remaining?.GetValue("Other") as string == "other.exe" && remaining?.GetValue("Legacy") != null);
                using var legacy = root.OpenSubKey(scope.Uninstall + @"\Legacy");
                using var removed = root.OpenSubKey(scope.Uninstall + @"\TLauncher");
                Require(legacy != null && removed == null);
                using var backup = JsonDocument.Parse(File.ReadAllText(Path.Combine(result.BackupDirectory!, "registry.json")));
                Require(backup.RootElement.GetArrayLength() == 3 && backup.RootElement.ToString().Contains("Binary"));
            }
            finally
            {
                Require(testKey.StartsWith(@"Software\MechanicaLauncher.Tests\", StringComparison.Ordinal));
                root.DeleteSubKeyTree(testKey, throwOnMissingSubKey: false);
            }
        });
        await check("Changed Windows entries cannot be removed from an old approval", async () =>
        {
            string testKey = @"Software\MechanicaLauncher.Tests\" + Guid.NewGuid().ToString("N");
            using var root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
            var scope = new TLauncherWindows.RegistryArea(RegistryHive.CurrentUser, RegistryView.Registry64,
                testKey + @"\Uninstall", testKey + @"\Run", testKey + @"\RunOnce", testKey + @"\TLauncher");
            try
            {
                using (var key = root.CreateSubKey(scope.Uninstall + @"\TLauncher"))
                { key.SetValue("DisplayName", "TLauncher"); key.SetValue("Publisher", "TLauncher Inc."); }
                var scan = TLauncherWindows.AddArtifacts(TLauncherDetector.ScanFolder(area), [scope], []);
                using (var key = root.OpenSubKey(scope.Uninstall + @"\TLauncher", writable: true)) key!.SetValue("DisplayName", "TLegacy");
                var result = await TLauncherCleaner.CleanAsync(scan, Path.Combine(Path.GetDirectoryName(area)!, "registry-backups"));
                using var preserved = root.OpenSubKey(scope.Uninstall + @"\TLauncher");
                Require(result.Removed.Count == 0 && result.Skipped.Count == 1 && preserved?.GetValue("DisplayName") as string == "TLegacy");
            }
            finally
            {
                Require(testKey.StartsWith(@"Software\MechanicaLauncher.Tests\", StringComparison.Ordinal));
                root.DeleteSubKeyTree(testKey, throwOnMissingSubKey: false);
            }
        });
        await check("Java process matching distinguishes launcher entry points from Minecraft and Legacy", () =>
        {
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\Apps\TLauncher\TLauncher.jar", @"C:\Apps\TLauncher\TLauncher.exe" };
            Require(TLauncherWindows.ReferencesLauncher("javaw.exe -jar \"C:\\Apps\\TLauncher\\TLauncher.jar\"", paths));
            Require(!TLauncherWindows.ReferencesLauncher("javaw.exe -cp \"C:\\Apps\\TLauncher\\TLauncher.jar\" net.minecraft.client.main.Main", paths));
            Require(!TLauncherWindows.ReferencesLauncher("javaw.exe -jar \"C:\\Apps\\TLauncher\\TLauncher.jar\" --brand legacy", paths));
            Require(!TLauncherWindows.ReferencesLauncher("other.exe -jar \"C:\\Apps\\TLauncher\\TLauncher.jar\"", paths));
            return Task.CompletedTask;
        });
        await check("Cleanup request preserves the registry view and removes an approved application registration without files", async () =>
        {
            string testKey = @"Software\MechanicaLauncher.Tests\" + Guid.NewGuid().ToString("N");
            string folder = Path.Combine(area, "registry-only"); Directory.CreateDirectory(folder);
            using var root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry32);
            var scope = new TLauncherWindows.RegistryArea(RegistryHive.CurrentUser, RegistryView.Registry32,
                testKey + @"\Uninstall", testKey + @"\Run", testKey + @"\RunOnce", testKey + @"\TLauncher");
            try
            {
                using (var key = root.CreateSubKey(scope.Uninstall + @"\TLauncher"))
                { key.SetValue("DisplayName", "TLauncher"); key.SetValue("Publisher", "TLauncher"); }
                var scan = TLauncherWindows.AddArtifacts(TLauncherDetector.ScanFolder(folder), [scope], []);
                Require(scan.Files.Count == 0 && scan.RegistryEntries.Count == 1);
                var request = TLauncherCleanupRequest.Create(scan, Path.Combine(area, "elevated-backups"));
                request = JsonSerializer.Deserialize<TLauncherCleanupRequest>(JsonSerializer.Serialize(request))!;
                var approved = request.Resolve(scan, out var skipped);
                Require(skipped.Count == 0 && approved.RegistryEntries.Single().View == RegistryView.Registry32);
                var entry = scan.RegistryEntries.Single();
                var otherUser = request with { Registry = new() { [entry.Path + "|" + entry.View + "|S-1-0-0"] = entry.Snapshot } };
                Require(otherUser.Resolve(scan, out _).RegistryEntries.Count == 0);
                var result = await TLauncherCleaner.CleanAsync(approved, request.BackupRoot);
                using var remaining = root.OpenSubKey(scope.Uninstall + @"\TLauncher");
                Require(remaining == null && result.Removed.Count == 1 && result.Skipped.Count == 0);
                Require(File.Exists(Path.Combine(result.BackupDirectory!, "registry.json")));
            }
            finally
            {
                Require(testKey.StartsWith(@"Software\MechanicaLauncher.Tests\", StringComparison.Ordinal));
                root.DeleteSubKeyTree(testKey, throwOnMissingSubKey: false);
            }
        });
        await check("Elevated cleanup cannot add new registrations or delete a registration changed to Legacy", () =>
        {
            string testKey = @"Software\MechanicaLauncher.Tests\" + Guid.NewGuid().ToString("N");
            string folder = Path.Combine(area, "registry-request"); Directory.CreateDirectory(folder);
            using var root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry32);
            var scope = new TLauncherWindows.RegistryArea(RegistryHive.CurrentUser, RegistryView.Registry32,
                testKey + @"\Uninstall", testKey + @"\Run", testKey + @"\RunOnce", testKey + @"\TLauncher");
            try
            {
                using (var key = root.CreateSubKey(scope.Uninstall + @"\TLauncher"))
                { key.SetValue("DisplayName", "TLauncher"); key.SetValue("Publisher", "TLauncher"); }
                var scan = TLauncherWindows.AddArtifacts(TLauncherDetector.ScanFolder(folder), [scope], []);
                var request = TLauncherCleanupRequest.Create(scan, Path.Combine(area, "elevated-backups"));
                using (var key = root.CreateSubKey(scope.Uninstall + @"\Second"))
                { key.SetValue("DisplayName", "TLauncher"); key.SetValue("Publisher", "TLauncher"); }
                var current = TLauncherWindows.AddArtifacts(TLauncherDetector.ScanFolder(folder), [scope], []);
                Require(current.RegistryEntries.Count == 2 && request.Resolve(current, out _).RegistryEntries.Count == 1);
                File.WriteAllText(Path.Combine(folder, "tlauncher-2.0.properties"), "server=https://tlauncher.org");
                current = TLauncherWindows.AddArtifacts(TLauncherDetector.ScanFolder(folder), [scope], []);
                Require(request.Resolve(current, out _).RegistryEntries.Count == 0);
                using (var key = root.OpenSubKey(scope.Uninstall + @"\TLauncher", writable: true)) key!.SetValue("DisplayName", "TLegacy");
                current = TLauncherWindows.AddArtifacts(TLauncherDetector.ScanFolder(folder), [scope], []);
                Require(request.Resolve(current, out var skipped).RegistryEntries.Count == 0 && skipped.Count == 1);
            }
            finally
            {
                Require(testKey.StartsWith(@"Software\MechanicaLauncher.Tests\", StringComparison.Ordinal));
                root.DeleteSubKeyTree(testKey, throwOnMissingSubKey: false);
            }
            return Task.CompletedTask;
        });
    }
    private static void Shortcut(string path, string jar, bool legacy = false)
    {
        object shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", throwOnError: true)!)!;
        object shortcut = ((dynamic)shell).CreateShortcut(path);
        try
        {
            ((dynamic)shortcut).TargetPath = Path.Combine(Environment.SystemDirectory, "javaw.exe");
            ((dynamic)shortcut).Arguments = "-jar \"" + jar + "\"" + (legacy ? " --brand legacy" : "");
            ((dynamic)shortcut).Save();
        }
        finally { Marshal.FinalReleaseComObject(shortcut); Marshal.FinalReleaseComObject(shell); }
    }
    private static void Require(bool condition) { if (!condition) throw new InvalidOperationException("Windows cleanup check failed."); }
}
