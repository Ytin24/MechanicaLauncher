namespace MechanicaLauncher.Core.Instances;

public sealed record GameScreenshot(string Path, string Name, DateTime Time, long Size);

public static class InstanceMedia
{
    public static IReadOnlyList<GameScreenshot> GetScreenshots(string gameDir)
    {
        var directory = Path.Combine(gameDir, "screenshots");
        if (!Directory.Exists(directory)) return [];
        return new DirectoryInfo(directory).EnumerateFiles()
            .Where(f => IsImage(f.FullName) && (f.Attributes & FileAttributes.ReparsePoint) == 0)
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Select(f => new GameScreenshot(f.FullName, f.Name, f.LastWriteTime, f.Length)).ToArray();
    }

    public static bool IsImage(string path) => Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg";
    public static bool IsAccent(string? value) => value is { Length: 7 } && value[0] == '#' && value[1..].All(Uri.IsHexDigit);
}
