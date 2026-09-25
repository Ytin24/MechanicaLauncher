using System.ComponentModel;
using System.Security.Cryptography;
using System.Text.Json;
using MechanicaLauncher.Core.Security;

namespace MechanicaLauncher.Desktop;

internal static class TLauncherCleanupProcess
{
    internal const string Command = "--tlauncher-cleanup";
    internal sealed record Response(TLauncherCleanupResult? Result, string? Error);

    internal static async Task<TLauncherCleanupResult> CleanAsync(TLauncherScanResult scan, string backupRoot,
        IProgress<string> progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TLauncherCleaner.RequiresElevation(scan))
            return await TLauncherCleaner.CleanAsync(scan, backupRoot, progress, cancellationToken).ConfigureAwait(false);
        return await RunElevatedAsync(scan, backupRoot, progress, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<TLauncherCleanupResult> RunElevatedAsync(TLauncherScanResult scan, string backupRoot,
        IProgress<string> progress, CancellationToken cancellationToken, Func<ProcessStartInfo, Process?>? startProcess = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string directory = Path.Combine(Path.GetTempPath(), "MechanicaLauncher", "cleanup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        RequireRegularPath(directory);
        string requestPath = Path.Combine(directory, "request.json");
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(TLauncherCleanupRequest.Create(scan, backupRoot));
        using (var output = new FileStream(requestPath, FileMode.CreateNew, FileAccess.Write, FileShare.None)) output.Write(payload);
        var start = CreateStartInfo(Environment.ProcessPath ?? throw new IOException("Не найден файл лаунчера."), requestPath, Convert.ToHexString(SHA256.HashData(payload)));
        Process? helper = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress.Report("Ожидание разрешения Windows…");
            helper = (startProcess ?? Process.Start)(start) ?? throw new IOException("Не удалось запустить очистку.");
            progress.Report("Удаление TLauncher…");
            await helper.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            string resultPath = requestPath + ".result.json";
            if (!File.Exists(resultPath)) throw new IOException("Очистка не завершена.");
            RequireRegularPath(resultPath);
            var response = JsonSerializer.Deserialize<Response>(await File.ReadAllTextAsync(resultPath, cancellationToken).ConfigureAwait(false));
            if (response?.Result == null) throw new IOException(response?.Error ?? "Очистка не завершена.");
            return response.Result;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        { throw new InvalidOperationException("Удаление отменено.", ex); }
        finally
        {
            if (helper == null || helper.HasExited)
            {
                try
                {
                    RequireRegularPath(directory);
                    File.Delete(requestPath);
                    File.Delete(requestPath + ".result.json");
                    Directory.Delete(directory, recursive: false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            helper?.Dispose();
        }
    }

    internal static ProcessStartInfo CreateStartInfo(string executable, string request, string hash)
    {
        var start = new ProcessStartInfo(Path.GetFullPath(executable))
        {
            UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(executable))!
        };
        start.ArgumentList.Add(Command);
        start.ArgumentList.Add(Path.GetFullPath(request));
        start.ArgumentList.Add(hash);
        return start;
    }

    internal static async Task<int> RunHelperAsync(string requestPath, string expectedHash)
    {
        RequireRegularPath(requestPath);
        if (new FileInfo(requestPath).Length > 64 * 1024 * 1024) throw new IOException("Слишком большой запрос очистки.");
        byte[] payload = await File.ReadAllBytesAsync(requestPath).ConfigureAwait(false);
        if (!Convert.ToHexString(SHA256.HashData(payload)).Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Запрос очистки изменился.");
        var request = JsonSerializer.Deserialize<TLauncherCleanupRequest>(payload) ?? throw new IOException("Некорректный запрос очистки.");
        Response response;
        try { response = new(await request.ExecuteAsync().ConfigureAwait(false), null); }
        catch (Exception ex) { response = new(null, ex.Message); }
        RequireRegularPath(Path.GetDirectoryName(requestPath)!);
        await using var output = new FileStream(requestPath + ".result.json", FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await JsonSerializer.SerializeAsync(output, response).ConfigureAwait(false);
        return response.Result == null ? 1 : 0;
    }

    private static void RequireRegularPath(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal)) throw new IOException("Некорректный путь запроса очистки.");
        for (string? current = Path.GetFullPath(path); current != null; current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("Путь запроса очистки изменился.");
    }
}
