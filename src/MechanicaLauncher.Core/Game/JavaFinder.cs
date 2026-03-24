namespace MechanicaLauncher.Core.Game;

public static class JavaFinder
{
    public static string? FindJava()
    {
        var javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrEmpty(javaHome))
        {
            var javaw = Path.Combine(javaHome, "bin", "javaw.exe");
            if (File.Exists(javaw)) return javaw;
            var java = Path.Combine(javaHome, "bin", "java.exe");
            if (File.Exists(java)) return java;
        }

        string[] searchPaths =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Java"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Eclipse Adoptium"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".jdks"),
        ];

        foreach (var basePath in searchPaths)
        {
            if (!Directory.Exists(basePath)) continue;
            foreach (var dir in Directory.GetDirectories(basePath).OrderByDescending(d => d))
            {
                var javaw = Path.Combine(dir, "bin", "javaw.exe");
                if (File.Exists(javaw)) return javaw;
            }
        }

        var pathDirs = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? [];
        foreach (var dir in pathDirs)
        {
            var javaw = Path.Combine(dir, "javaw.exe");
            if (File.Exists(javaw)) return javaw;
        }

        return null;
    }

    public static string GetJavaVersion(string javaPath)
    {
        try
        {
            var dir = Path.GetDirectoryName(Path.GetDirectoryName(javaPath));
            return Path.GetFileName(dir) ?? "Unknown";
        }
        catch
        {
            return "Unknown";
        }
    }
}
