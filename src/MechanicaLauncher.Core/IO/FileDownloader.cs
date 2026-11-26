using System.Net;
using System.Security.Cryptography;

namespace MechanicaLauncher.Core.IO;

internal static class FileDownloader
{
    public static async Task EnsureAsync(HttpClient http, string url, string path,
        string? sha1 = null, long size = 0, CancellationToken cancellationToken = default, string? sha512 = null)
    {
        if (await IsValidAsync(path, sha1, size, cancellationToken, sha512)) return;
        if (string.IsNullOrWhiteSpace(url))
            throw new FileNotFoundException($"Required file is missing or damaged: {path}. Reinstall the loader.", path);

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var job = DownloadQueue.Current;
        for (int attempt = 0; ; attempt++)
        {
            var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".part";
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromMinutes(5));
                using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                response.EnsureSuccessStatusCode();
                var expectedSize = size > 0 ? size : response.Content.Headers.ContentLength ?? 0;
                long received = 0;
                job?.Progress(path, 0, expectedSize, 0);
                await using (var input = await response.Content.ReadAsStreamAsync(timeout.Token))
                await using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                    FileShare.None, 81920, FileOptions.Asynchronous))
                {
                    var buffer = new byte[81920];
                    int count;
                    while ((count = await input.ReadAsync(buffer, timeout.Token)) > 0)
                    {
                        await output.WriteAsync(buffer.AsMemory(0, count), timeout.Token);
                        received += count;
                        job?.Progress(path, received, expectedSize, count);
                    }
                }

                if (!await IsValidAsync(temporaryPath, sha1, expectedSize, timeout.Token, sha512))
                    throw new InvalidDataException($"Downloaded file failed size/hash verification: {Path.GetFileName(path)}");

                cancellationToken.ThrowIfCancellationRequested();
                File.Move(temporaryPath, path, overwrite: true);
                if (HashKey(sha1, sha512) is { } key) VerifiedFiles.Remember(path, key);
                job?.Progress(path, received, received, 0, complete: true);
                return;
            }
            catch (Exception ex) when (attempt < 2 && !cancellationToken.IsCancellationRequested && IsTransient(ex))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250 * (attempt + 1)), cancellationToken);
            }
            finally
            {
                AtomicFile.TryDelete(temporaryPath);
            }
        }
    }

    public static async Task<bool> IsValidAsync(string path, string? sha1 = null, long size = 0,
        CancellationToken cancellationToken = default, string? sha512 = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var info = new FileInfo(path);
        if (!info.Exists) return false;
        if ((info.Length == 0 && string.IsNullOrEmpty(sha1) && string.IsNullOrEmpty(sha512)) || (size > 0 && info.Length != size)) return false;
        var key = HashKey(sha1, sha512);
        if (key == null) return true;
        bool temporary = path.EndsWith(".part", StringComparison.OrdinalIgnoreCase);
        if (!temporary && VerifiedFiles.IsKnown(path, info, key)) return true;
        await using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            if (!string.IsNullOrEmpty(sha1))
            {
                var hash = await SHA1.HashDataAsync(stream, cancellationToken);
                if (!Convert.ToHexString(hash).Equals(sha1, StringComparison.OrdinalIgnoreCase)) { VerifiedFiles.Forget(path); return false; }
            }
            if (!string.IsNullOrEmpty(sha512))
            {
                stream.Position = 0;
                var hash = await SHA512.HashDataAsync(stream, cancellationToken);
                if (!Convert.ToHexString(hash).Equals(sha512, StringComparison.OrdinalIgnoreCase)) { VerifiedFiles.Forget(path); return false; }
            }
        }
        if (!temporary) VerifiedFiles.Remember(path, key);
        return true;
    }

    private static string? HashKey(string? sha1, string? sha512) =>
        string.IsNullOrEmpty(sha1) && string.IsNullOrEmpty(sha512) ? null : (sha1 ?? "") + "|" + (sha512 ?? "");

    // Copies an already verified local file instead of downloading it again (for example the shared client jar).
    public static async Task<bool> TryCopyVerifiedAsync(string source, string path, string? sha1, long size, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(sha1) || !await IsValidAsync(source, sha1, size, cancellationToken)) return false;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".part";
        try
        {
            File.Copy(source, temporaryPath);
            if (!await IsValidAsync(temporaryPath, sha1, size, cancellationToken)) return false;
            File.Move(temporaryPath, path, overwrite: true);
            VerifiedFiles.Remember(path, HashKey(sha1, null)!);
            return true;
        }
        catch (IOException) { return false; }
        finally { AtomicFile.TryDelete(temporaryPath); }
    }

    public static string GetPath(string root, string relativePath)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath) || relativePath.Contains(':'))
            throw new InvalidDataException($"Invalid relative file path: {relativePath}");
        var path = Path.GetFullPath(Path.Combine(fullRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"File path leaves the target directory: {relativePath}");
        return path;
    }

    private static bool IsTransient(Exception ex) => ex switch
    {
        HttpRequestException http => http.StatusCode is null or HttpStatusCode.RequestTimeout or
            HttpStatusCode.TooManyRequests || (int)http.StatusCode.Value >= 500,
        HttpIOException => true,
        OperationCanceledException => true,
        InvalidDataException => true,
        _ => false
    };
}
