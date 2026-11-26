using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace MechanicaLauncher.Core.Updates;

/// <summary>
/// Downloads the release installer, verifies it and hands over to a silent Inno Setup upgrade that
/// relaunches the launcher. Only installed copies update themselves; portable copies keep the
/// manual download link.
/// </summary>
public sealed partial class LauncherUpdater(HttpClient? http = null, string? downloadDirectory = null)
{
    private static readonly HttpClient SharedHttp = CreateHttp();
    private readonly HttpClient http = http ?? SharedHttp;
    private readonly string directory = downloadDirectory ?? Path.Combine(Path.GetTempPath(), "MechanicaLauncher-update");

    /// <summary>True when <paramref name="appDirectory"/> was installed by setup.exe and is not portable.</summary>
    public static bool IsInstalledCopy(string appDirectory) =>
        !File.Exists(Path.Combine(appDirectory, "portable.flag")) && File.Exists(Path.Combine(appDirectory, "unins000.exe"));

    public static bool CanInstall(UpdateInfo? update, string appDirectory) =>
        update is { IsAvailable: true, InstallerUrl: not null, InstallerName: not null }
        && OperatingSystem.IsWindows() && IsInstalledCopy(appDirectory);

    public async Task<string> DownloadAsync(UpdateInfo update, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        if (update.InstallerUrl is not { } url || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("The release has no HTTPS installer.");
        var name = update.InstallerName ?? "";
        if (!SafeName().IsMatch(name)) throw new InvalidOperationException("Unexpected installer file name.");
        Directory.CreateDirectory(directory);
        foreach (var stale in Directory.EnumerateFiles(directory, "*.part")) TryDelete(stale);
        var target = Path.Combine(directory, name);
        var partial = target + ".part";
        try
        {
            using (var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
            {
                response.EnsureSuccessStatusCode();
                long total = response.Content.Headers.ContentLength ?? update.InstallerSize;
                if (update.InstallerSize > 0 && total > 0 && total != update.InstallerSize)
                    throw new InvalidOperationException("Installer size does not match the release.");
                await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
                using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[81920];
                long received = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    sha.AppendData(buffer, 0, read);
                    received += read;
                    if (update.InstallerSize > 0 && received > update.InstallerSize)
                        throw new InvalidOperationException("Installer is larger than the release says.");
                    if (total > 0) progress?.Report(Math.Min(100, received * 100.0 / total));
                }
                if (update.InstallerSize > 0 && received != update.InstallerSize)
                    throw new InvalidOperationException("Installer download is incomplete.");
                var actual = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
                if (update.InstallerSha256 != null && actual != update.InstallerSha256)
                    throw new InvalidOperationException("Installer checksum does not match the release.");
            }
            File.Move(partial, target, true);
            progress?.Report(100);
            return target;
        }
        catch
        {
            TryDelete(partial);
            throw;
        }
    }

    /// <summary>
    /// Silent upgrade into the running copy's folder. <c>/RELAUNCH=1</c> makes setup start the launcher
    /// again when it finishes (see installer/setup.iss).
    /// </summary>
    public static ProcessStartInfo InstallerStartInfo(string installer, string appDirectory)
    {
        var info = new ProcessStartInfo(installer) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(installer)! };
        foreach (var argument in new[] { "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/CLOSEAPPLICATIONS", "/RELAUNCH=1" })
            info.ArgumentList.Add(argument);
        info.ArgumentList.Add("/DIR=" + Path.TrimEndingDirectorySeparator(appDirectory));
        return info;
    }

    public static void StartInstaller(string installer, string appDirectory) =>
        Process.Start(InstallerStartInfo(installer, appDirectory))?.Dispose();

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static HttpClient CreateHttp()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.Add("User-Agent", "MechanicaLauncher");
        return client;
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,127}\.exe$")]
    private static partial Regex SafeName();
}
