using System.Net;
using System.Security.Cryptography;
using MechanicaLauncher.Core.Updates;

internal static class LauncherUpdaterTests
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> check, string root)
    {
        var payload = RandomNumberGenerator.GetBytes(200_000);
        var sha = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        UpdateInfo Info(string? hash = null, long size = -1, string name = "MechanicaLauncher-2.0.0-setup.exe") => new()
        {
            IsAvailable = true, LatestVersion = "2.0.0", InstallerName = name,
            InstallerUrl = "https://github.com/Ytin24/MechanicaLauncher/releases/download/v2.0.0/" + name,
            InstallerSize = size < 0 ? payload.Length : size, InstallerSha256 = hash ?? sha
        };

        await check("Launcher update downloads and verifies the installer", async () =>
        {
            var dir = Path.Combine(root, "update-ok");
            double last = 0;
            var path = await new LauncherUpdater(new HttpClient(new Bytes(payload)), dir)
                .DownloadAsync(Info(), new SyncProgress(value => last = value));
            Require(File.ReadAllBytes(path).SequenceEqual(payload) && Path.GetFileName(path) == "MechanicaLauncher-2.0.0-setup.exe" && last == 100);
            Require(!Directory.EnumerateFiles(dir, "*.part").Any());
        });
        await check("Launcher update rejects a checksum mismatch and leaves no file", async () =>
        {
            var dir = Path.Combine(root, "update-bad-hash");
            await Fails(() => new LauncherUpdater(new HttpClient(new Bytes(payload)), dir).DownloadAsync(Info(hash: new string('0', 64))));
            Require(!Directory.EnumerateFiles(dir).Any());
        });
        await check("Launcher update rejects a size mismatch", async () =>
        {
            await Fails(() => new LauncherUpdater(new HttpClient(new Bytes(payload)), Path.Combine(root, "update-size")).DownloadAsync(Info(size: payload.Length - 1)));
        });
        await check("Launcher update rejects unsafe installer names and plain HTTP", async () =>
        {
            var updater = new LauncherUpdater(new HttpClient(new Bytes(payload)), Path.Combine(root, "update-name"));
            await Fails(() => updater.DownloadAsync(Info(name: "..\\evil.exe")));
            await Fails(() => updater.DownloadAsync(Info(name: "setup.msi")));
            var http = Info(); http.InstallerUrl = "http://example.test/setup.exe";
            await Fails(() => updater.DownloadAsync(http));
        });
        await check("Launcher update only self-installs from an installed, non-portable copy", async () =>
        {
            var app = Directory.CreateDirectory(Path.Combine(root, "update-app")).FullName;
            Require(!LauncherUpdater.IsInstalledCopy(app));
            File.WriteAllText(Path.Combine(app, "unins000.exe"), "");
            Require(LauncherUpdater.IsInstalledCopy(app));
            File.WriteAllText(Path.Combine(app, "portable.flag"), "");
            Require(!LauncherUpdater.IsInstalledCopy(app) && !LauncherUpdater.CanInstall(Info(), app));
            Require(!LauncherUpdater.CanInstall(null, app));
            await Task.CompletedTask;
        });
        await check("Launcher update runs setup silently into the current folder and relaunches", async () =>
        {
            var info = LauncherUpdater.InstallerStartInfo(Path.Combine(root, "setup.exe"), Path.Combine(root, "app") + Path.DirectorySeparatorChar);
            var args = info.ArgumentList.ToList();
            Require(args.Contains("/VERYSILENT") && args.Contains("/RELAUNCH=1") && args.Contains("/CLOSEAPPLICATIONS")
                && args.Contains("/DIR=" + Path.Combine(root, "app")) && !info.UseShellExecute);
            await Task.CompletedTask;
        });
        await check("Launcher update compares versions numerically", async () =>
        {
            Require(UpdateChecker.IsNewer("1.10.0", "1.9.9") && !UpdateChecker.IsNewer("1.2.0", "1.2.0") && !UpdateChecker.IsNewer("1.2", "1.2.1"));
            await Task.CompletedTask;
        });
    }

    private sealed class Bytes(byte[] payload) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) });
    }

    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }

    private static async Task Fails(Func<Task> action)
    {
        try { await action(); }
        catch (Exception) { return; }
        throw new InvalidOperationException("Expected failure");
    }

    private static void Require(bool condition)
    {
        if (!condition) throw new InvalidOperationException("Requirement failed");
    }
}
