using System.Diagnostics;
using Microsoft.Win32;

namespace MechanicaLauncher.Core.Security;

public static class TLauncherCleaner
{
    public static event Action<string>? StatusChanged;

    public static void Clean()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var startMenu = Path.Combine(appData, "Microsoft", "Windows", "Start Menu", "Programs");

        KillProcesses();

        DeleteDir(Path.Combine(appData, ".tlauncher"), "TLauncher config");
        DeleteFile(Path.Combine(appData, ".minecraft", "TLauncher.exe"), "TLauncher executable");
        DeleteFile(Path.Combine(appData, ".minecraft", "libraries", "tlicon.ico"), "TLauncher icon");
        DeleteFile(Path.Combine(appData, ".minecraft", "libraries", "minecraft.ico"), "TLauncher icon");
        DeleteDir(Path.Combine(appData, ".tlauncher", "starter", "jre_default"), "TLauncher bundled Java");

        VerifyMinecraftJava(Path.Combine(appData, ".minecraft", "runtime"));

        CleanMinecraftDir(Path.Combine(appData, ".minecraft"));

        DeleteDir(@"C:\Program Files\tLauncher", "TLauncher Program Files");
        DeleteDir(@"C:\Program Files (x86)\tLauncher", "TLauncher Program Files x86");

        try
        {
            foreach (var lnk in Directory.GetFiles(desktop, "*.lnk"))
                if (Path.GetFileNameWithoutExtension(lnk).Contains("TLauncher", StringComparison.OrdinalIgnoreCase))
                    DeleteFile(lnk, "Desktop shortcut");
        }
        catch { }

        DeleteDir(Path.Combine(startMenu, "TLauncher"), "Start Menu folder");

        try
        {
            foreach (var lnk in Directory.GetFiles(startMenu, "*.lnk"))
                if (Path.GetFileNameWithoutExtension(lnk).Contains("TLauncher", StringComparison.OrdinalIgnoreCase))
                    DeleteFile(lnk, "Start Menu shortcut");
        }
        catch { }

        CleanRegistry();
        CleanPrefetch();

        ForceDeleteRemaining();
        StatusChanged?.Invoke("Cleanup complete!");
    }

    private static void ForceDeleteRemaining()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var paths = new[]
        {
            Path.Combine(appData, ".tlauncher"),
            @"C:\Program Files\tLauncher",
            @"C:\Program Files (x86)\tLauncher"
        };

        foreach (var path in paths)
        {
            if (!Directory.Exists(path) && !File.Exists(path)) continue;
            StatusChanged?.Invoke($"Force removing {Path.GetFileName(path)}...");
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c rd /s /q \"{path}\"",
                    UseShellExecute = true,
                    Verb = "runas",
                    WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
                    CreateNoWindow = true
                };
                var proc = System.Diagnostics.Process.Start(psi);
                proc?.WaitForExit(10000);
            }
            catch { }
        }
    }

    private static void KillProcesses()
    {
        StatusChanged?.Invoke("Stopping TLauncher processes...");

        foreach (var name in new[] { "TLauncher", "tlauncher" })
        {
            try
            {
                foreach (var proc in Process.GetProcessesByName(name))
                {
                    try { proc.Kill(); proc.WaitForExit(3000); } catch { }
                }
            }
            catch { }
        }

        try
        {
            foreach (var proc in Process.GetProcessesByName("javaw"))
            {
                try
                {
                    var path = proc.MainModule?.FileName ?? "";
                    if (path.Contains(".tlauncher", StringComparison.OrdinalIgnoreCase) ||
                        path.Contains("TLauncher", StringComparison.OrdinalIgnoreCase))
                    {
                        proc.Kill();
                        proc.WaitForExit(3000);
                    }
                }
                catch { }
            }
        }
        catch { }

        Thread.Sleep(500);
    }

    private static void CleanMinecraftDir(string mcDir)
    {
        if (!Directory.Exists(mcDir)) return;

        StatusChanged?.Invoke("Cleaning .minecraft from TLauncher traces...");

        DeleteDir(Path.Combine(mcDir, "versions"), "TLauncher versions");
        DeleteDir(Path.Combine(mcDir, "libraries"), "TLauncher libraries");
        DeleteDir(Path.Combine(mcDir, "assets"), "TLauncher assets");
        DeleteDir(Path.Combine(mcDir, "logs"), "TLauncher logs");

        foreach (var file in Directory.GetFiles(mcDir, "*.exe"))
            DeleteFile(file, Path.GetFileName(file));
        foreach (var file in Directory.GetFiles(mcDir, "*.ico"))
            DeleteFile(file, Path.GetFileName(file));
        foreach (var file in Directory.GetFiles(mcDir, "*.log"))
            DeleteFile(file, Path.GetFileName(file));
        foreach (var file in Directory.GetFiles(mcDir, "*.json"))
        {
            var name = Path.GetFileName(file).ToLowerInvariant();
            if (name.Contains("tlauncher") || name == "usercache.json")
                DeleteFile(file, name);
        }

        var remaining = Directory.GetFiles(mcDir).Length + Directory.GetDirectories(mcDir).Length;
        if (remaining == 0)
            DeleteDir(mcDir, ".minecraft (empty)");
    }

    private static void VerifyMinecraftJava(string runtimeDir)
    {
        if (!Directory.Exists(runtimeDir)) return;
        StatusChanged?.Invoke("Verifying Java runtimes...");

        foreach (var componentDir in Directory.GetDirectories(runtimeDir))
        {
            var component = Path.GetFileName(componentDir);
            var result = JavaVerifier.VerifyAsync(runtimeDir, component,
                s => StatusChanged?.Invoke(s)).GetAwaiter().GetResult();

            if (!result.IsVerified && result.FailedFiles > 0)
            {
                StatusChanged?.Invoke($"Java {component}: {result.FailedFiles} modified files — deleting...");
                DeleteDir(componentDir, $"Compromised Java ({component})");
            }
            else if (result.IsVerified)
            {
                StatusChanged?.Invoke($"Java {component}: OK ({result.VerifiedFiles} files verified)");
            }
        }
    }

    private static void CleanRegistry()
    {
        StatusChanged?.Invoke("Cleaning registry...");
        try
        {
            Registry.LocalMachine.DeleteSubKeyTree(
                @"Software\Microsoft\Windows\CurrentVersion\Uninstall\TLauncher", false);
        }
        catch { }

        try
        {
            using var muiCache = Registry.CurrentUser.OpenSubKey(
                @"Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\MuiCache", true);
            if (muiCache != null)
            {
                foreach (var name in muiCache.GetValueNames())
                {
                    if (name.Contains("tlauncher", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains(".tlauncher", StringComparison.OrdinalIgnoreCase))
                    {
                        muiCache.DeleteValue(name, false);
                    }
                }
            }
        }
        catch { }
    }

    private static void CleanPrefetch()
    {
        StatusChanged?.Invoke("Cleaning prefetch...");
        try
        {
            var prefetchDir = @"C:\Windows\Prefetch";
            if (Directory.Exists(prefetchDir))
            {
                foreach (var file in Directory.GetFiles(prefetchDir, "TLAUNCHER*.pf"))
                    try { File.Delete(file); } catch { }
            }
        }
        catch { }
    }

    private static void DeleteDir(string path, string label)
    {
        if (!Directory.Exists(path)) return;
        StatusChanged?.Invoke($"Removing {label}...");
        try
        {
            foreach (var file in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
            {
                try { File.SetAttributes(file, FileAttributes.Normal); File.Delete(file); }
                catch { StatusChanged?.Invoke($"Skipped: {Path.GetFileName(file)}"); }
            }
            foreach (var dir in Directory.GetDirectories(path, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
                try { Directory.Delete(dir); } catch { }
            try { Directory.Delete(path); } catch { }
        }
        catch { }
    }

    private static void DeleteFile(string path, string label)
    {
        if (!File.Exists(path)) return;
        StatusChanged?.Invoke($"Removing {label}...");
        try { File.SetAttributes(path, FileAttributes.Normal); File.Delete(path); } catch { }
    }
}
