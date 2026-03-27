using Microsoft.Win32;

namespace MechanicaLauncher.Core.Security;

public sealed class TLauncherScanResult
{
    public bool IsDetected { get; set; }
    public List<string> FoundPaths { get; } = [];
    public string? MinecraftDir { get; set; }
    public int ThreatCount => FoundPaths.Count;
}

public static class TLauncherDetector
{
    public static TLauncherScanResult Scan()
    {
        var result = new TLauncherScanResult();
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var tlDir = Path.Combine(appData, ".tlauncher");

        if (Directory.Exists(Path.Combine(tlDir, "legacy")))
            return result;

        CheckFile(result, Path.Combine(tlDir, "tlauncher-2.0.properties"));
        CheckFile(result, Path.Combine(tlDir, "tl-uninstall.exe"));
        CheckFile(result, Path.Combine(appData, ".minecraft", "TLauncher.exe"));
        CheckFile(result, Path.Combine(appData, ".minecraft", "libraries", "tlicon.ico"));
        CheckPath(result, @"C:\Program Files\tLauncher");
        CheckPath(result, @"C:\Program Files (x86)\tLauncher");

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Uninstall");
            if (key != null)
            {
                foreach (var subKeyName in key.GetSubKeyNames())
                {
                    if (!subKeyName.Contains("TLauncher", StringComparison.OrdinalIgnoreCase)) continue;
                    using var sub = key.OpenSubKey(subKeyName);
                    var displayName = sub?.GetValue("DisplayName")?.ToString() ?? "";
                    if (displayName.Contains("Legacy", StringComparison.OrdinalIgnoreCase)) continue;
                    result.FoundPaths.Add($"Registry: {subKeyName}");
                }
            }
        }
        catch { }

        if (result.FoundPaths.Count > 0)
        {
            result.IsDetected = true;
            var mcDir = Path.Combine(appData, ".minecraft");
            if (Directory.Exists(mcDir))
                result.MinecraftDir = mcDir;
        }

        return result;
    }

    private static void CheckFile(TLauncherScanResult result, string path)
    {
        if (File.Exists(path))
            result.FoundPaths.Add(path);
    }

    private static void CheckPath(TLauncherScanResult result, string path)
    {
        if (Directory.Exists(path))
            result.FoundPaths.Add(path);
    }
}
