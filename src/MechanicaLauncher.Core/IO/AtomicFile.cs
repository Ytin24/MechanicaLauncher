namespace MechanicaLauncher.Core.IO;

internal static class AtomicFile
{
    public static void WriteText(string path, string text, bool keepBackup = false)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporaryPath, text);
            Commit(temporaryPath, path, keepBackup);
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    public static async Task WriteTextAsync(string path, string text, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporaryPath, text, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            Commit(temporaryPath, path, false);
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    private static void Commit(string temporaryPath, string path, bool keepBackup)
    {
        if (keepBackup && File.Exists(path))
            File.Replace(temporaryPath, path, path + ".bak");
        else
            File.Move(temporaryPath, path, overwrite: true);
    }

    internal static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
