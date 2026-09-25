namespace MechanicaLauncher.Core.IO;

public static class LauncherPaths
{
    public static bool IsPortable => File.Exists(Path.Combine(AppContext.BaseDirectory, "portable.flag"));
    public static bool HasCustomDataDirectory => IsPortable || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MECHANICA_DATA_DIR"));

    public static string DataDirectory
    {
        get => ResolveDataDirectory(AppContext.BaseDirectory, Environment.GetEnvironmentVariable("MECHANICA_DATA_DIR"),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
    }

    internal static string ResolveDataDirectory(string appDirectory, string? configured, string roaming)
    {
        if (string.IsNullOrWhiteSpace(configured))
            return File.Exists(Path.Combine(appDirectory, "portable.flag")) ? Path.Combine(appDirectory, "data") : Path.Combine(roaming, "MechanicaLauncher");
        if (!Path.IsPathFullyQualified(configured)) throw new InvalidOperationException("MECHANICA_DATA_DIR must be an absolute path.");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(configured));
    }
}
