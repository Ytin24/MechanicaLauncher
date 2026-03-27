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

        CheckPath(result, Path.Combine(appData, ".tlauncher"));
        CheckPath(result, Path.Combine(appData, ".minecraft", "TLauncher.exe"));
        CheckPath(result, Path.Combine(appData, ".minecraft", "libraries", "tlicon.ico"));
        CheckPath(result, Path.Combine(appData, ".minecraft", "libraries", "minecraft.ico"));
        CheckPath(result, @"C:\Program Files\tLauncher");
        CheckPath(result, @"C:\Program Files (x86)\tLauncher");

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Uninstall\TLauncher");
            if (key != null)
                result.FoundPaths.Add("Registry: TLauncher uninstall entry");
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

    private static void CheckPath(TLauncherScanResult result, string path)
    {
        if (File.Exists(path))
            result.FoundPaths.Add(path);
        else if (Directory.Exists(path))
            result.FoundPaths.Add(path);
    }
}
