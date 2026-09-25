using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MechanicaLauncher.Core.IO;
using MechanicaLauncher.Core.Profiles;
using MechanicaLauncher.Core.Security;
using MechanicaLauncher.Desktop;
using Nitidus;
using Nitidus.Native;

internal static partial class Program
{
    private static void TLauncherChecks(Pump context)
    {
        string fixture = Path.Combine(output, "tlauncher-fixture");
        Directory.CreateDirectory(Path.Combine(fixture, "legacy"));
        string file = Path.Combine(fixture, "tlauncher-2.0.properties");
        string legacy = Path.Combine(fixture, "legacy", "TLauncher.jar");
        File.WriteAllText(file, "update=https://tlauncher.org/client\n");
        File.WriteAllText(legacy, "Legacy Launcher");
        using var model = new LauncherModel(new LauncherSettings { Username = "CleanupTest", Language = "ru", Animations = false, DiscordRpc = false });
        model.TLauncherScanner = (_, token) => TLauncherDetector.ScanFolder(fixture, token);
        model.PrepareStartupCommand("SHOW");
        using var presentation = new LauncherView(model);
        using var host = new HeadlessHost(presentation.View, 840, 620);
        var view = presentation.View;
        double time = 0;
        void Render() { context.Drain(); host.Render(time += 1); host.Render(time += 1); }
        void Until(Func<bool> ready)
        {
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (!ready()) { Render(); if (DateTime.UtcNow > deadline) throw new TimeoutException("TLauncher UI operation timed out"); Thread.Sleep(10); }
            Render();
        }
        void Scan()
        {
            var task = model.ScanTLauncherAsync(fixture); Until(() => task.IsCompleted); task.GetAwaiter().GetResult();
        }
        void Click(string id)
        {
            Render(); var node = view.Find(id);
            Check(node.Get(Ui.Enabled) && node.Bounds.Y >= 40 && node.Bounds.Y + node.Bounds.Height <= 620, "cleanup action is visible: " + id);
            host.Click(node.Bounds.X + node.Bounds.Width / 2, node.Bounds.Y + node.Bounds.Height / 2); Render();
        }
        Render();
        Check(model.TLauncherBlocked && !view.Find("body").Get(Ui.Visible), "startup stays locked before the first scan finishes");
        Scan();
        Check(model.TLauncherDetected && model.TLauncherBlocked && File.Exists(file), "scan locks the launcher without deleting anything");
        Capture(host, "tlauncher-lock-dark");
        for (int i = 0; i < 14; i++)
        {
            host.Key(9, i % 2 == 0 ? KeyModifiers.None : KeyModifiers.Shift); Render();
            for (var node = host.FocusedElement; node != null; node = node.Parent)
                Check(node.Name != "body", "keyboard focus cannot enter the locked launcher");
        }
        model.Library(); model.Catalog(); model.Downloads(); model.Preferences(); model.Play(); Render();
        Check(model.Page == "home" && !model.Sessions.Preparing && !model.Sessions.Downloads.HasPending, "navigation, tray routes and play cannot bypass the lock");
        model.HandleCommand("MRPACK|C:\\blocked-missing-pack.mrpack").GetAwaiter().GetResult();
        model.ResumePendingCommand().GetAwaiter().GetResult();
        Check(model.TLauncherBlocked && !model.DialogOpen && !model.Sessions.Downloads.HasPending, "protocol import is deferred while the launcher is locked");
        model.PrepareStartupCommand("SHOW"); Scan();
        int exits = 0; model.ExitRequested = () => exits++;
        Click("tlauncherGateExit");
        Check(exits == 1 && File.Exists(file), "locked launcher offers an immediate exit without cleanup");
        Click("tlauncherGateReview");
        Check(model.DialogOpen && model.DialogDanger && model.DialogChoices.Count == 1 && model.DialogBody.Length == 0 && !model.DialogSearch, "cleanup shows the removal list without search or explanatory filler");
        Capture(host, "tlauncher-review-dark");
        Click("dialogCancel");
        Check(File.Exists(file) && File.Exists(legacy) && model.TLauncherBlocked && !view.Find("body").Get(Ui.Visible), "cancelling cleanup returns to the lock and preserves both launchers");
        model.LightTheme = true; Render(); Capture(host, "tlauncher-lock-light");
        model.ReviewTLauncher(); Render();
        Capture(host, "tlauncher-review-light");
        Click("dialogAccept"); Until(() => !model.DialogOpen);
        Check(!File.Exists(file) && File.Exists(legacy) && model.HasTLauncherBackup && !model.TLauncherDetected && !model.TLauncherBlocked, "cleanup unlocks only after rechecking and preserves Legacy with a backup");
        model.ResumePendingCommand().GetAwaiter().GetResult();
        model.Library(); Render();
        Check(model.Page == "instances" && view.Find("body").Get(Ui.Visible), "normal navigation resumes after a successful cleanup and recheck");
        Check(Directory.GetFiles(Path.Combine(LauncherPaths.DataDirectory, "backups", "tlauncher"), "manifest.json", SearchOption.AllDirectories).Length > 0, "cleanup records its backup manifest");
        model.ShowTLauncherReport(); Render();
        Check(model.DialogText.Contains("legacy", StringComparison.OrdinalIgnoreCase), "scan report explains protected Legacy paths");
        model.CloseDialog();
        File.WriteAllText(file, "update=https://tlauncher.org/client\n"); Scan(); model.ReviewTLauncher(); Render();
        File.AppendAllText(file, "changed=true\n");
        Click("dialogAccept"); Until(() => !model.DialogOpen);
        Check(File.Exists(file) && model.TLauncherStatus.Contains("Пропущено: 1") && model.TLauncherBlocked, "changed files are skipped and cannot unlock the launcher");
        model.ShowTLauncherReport(); Render();
        Check(model.DialogText.Contains(file + ": файл изменился"), "cleanup skip reasons remain available even without a new backup");
        model.CloseDialog();
        string empty = Path.Combine(output, "empty-launcher-folder"); Directory.CreateDirectory(empty);
        var otherScan = model.ScanTLauncherAsync(empty); Until(() => otherScan.IsCompleted); otherScan.GetAwaiter().GetResult();
        Check(model.TLauncherBlocked && model.Settings.TLauncherScanDirectories.Contains(fixture) && model.Settings.TLauncherScanDirectories.Contains(empty), "choosing another folder retains previously scanned paths and the lock");
        File.WriteAllText(file, "unverified replacement"); Scan();
        Check(model.TLauncherBlocked && !model.HasTLauncherFiles, "unverified replacement of a detected file keeps the lock without authorizing removal");
        using (var reopened = new LauncherModel(LauncherSettings.Load()))
        {
            reopened.TLauncherScanner = (_, token) => TLauncherDetector.ScanFolder(fixture, token);
            reopened.PrepareStartupCommand("SHOW");
            var recheck = reopened.ScanTLauncherAsync(); Until(() => recheck.IsCompleted); recheck.GetAwaiter().GetResult();
            Check(reopened.TLauncherBlocked && !reopened.HasTLauncherFiles, "restarting preserves the need to recheck previously confirmed files");
        }
        File.WriteAllText(file, "bootstrap.brand=legacy\nserver=https://tlauncher.org\n"); Scan();
        Check(!model.TLauncherBlocked && File.Exists(file) && File.Exists(legacy), "Legacy-only files never keep the launcher locked");
        model.TLauncherScanner = (_, _) => throw new IOException("test scanner unavailable");
        Scan();
        Check(model.TLauncherBlocked && model.TLauncherStatus.Contains("test scanner unavailable"), "scan failure keeps the gate closed and exposes a retryable error");
        model.TLauncherScanner = (_, token) => TLauncherDetector.ScanFolder(fixture, token);
        Scan();
        Check(!model.TLauncherBlocked, "a successful retry recovers from a scanner failure");

        string installation = Path.Combine(output, "full-installation");
        string owned = Path.Combine(installation, ".tlauncher");
        string siblingLegacy = Path.Combine(installation, "TLegacy", "tlauncher.properties");
        Directory.CreateDirectory(Path.Combine(owned, "jre"));
        Directory.CreateDirectory(Path.Combine(owned, "cache"));
        Directory.CreateDirectory(Path.GetDirectoryName(siblingLegacy)!);
        File.WriteAllText(Path.Combine(owned, "tlauncher-2.0.properties"), "server=https://tlauncher.org/client\n");
        File.WriteAllText(Path.Combine(owned, "jre", "release"), "private Java");
        File.WriteAllText(Path.Combine(owned, "cache", "data.bin"), "private cache");
        File.WriteAllText(siblingLegacy, "bootstrap.brand=legacy");
        model.TLauncherScanner = (_, token) => TLauncherDetector.ScanFolder(owned, token);
        var fullScan = model.ScanTLauncherAsync(owned); Until(() => fullScan.IsCompleted); fullScan.GetAwaiter().GetResult();
        Check(model.TLauncherBlocked && model.Settings.KnownTLauncherDirectories.Contains(owned), "exclusive launcher roots persist as directories");
        Check(!model.Settings.KnownTLauncherFiles.Any(f => f.StartsWith(owned, StringComparison.OrdinalIgnoreCase)), "owned folder contents do not flood settings with file paths");
        model.LightTheme = false; model.ReviewTLauncher(); Render();
        Check(model.DialogChoices.Count == 1 && model.DialogChoices[0].Title == ".tlauncher" && model.DialogChoices[0].Meta == owned,
            "full installation is presented as one folder to remove");
        Check(model.DialogBody.Length == 0 && !model.DialogSearch, "folder cleanup has no explanatory filler");
        Capture(host, "tlauncher-folder-review-dark");
        model.LightTheme = true; Render(); Capture(host, "tlauncher-folder-review-light");
        model.TLauncherRemover = (scan, backup, progress, token) => TLauncherCleanupProcess.RunElevatedAsync(scan, backup, progress, token,
            _ => throw new System.ComponentModel.Win32Exception(1223));
        Click("dialogAccept"); Until(() => !model.DialogBusy);
        Check(model.DialogOpen && model.DialogError == "Удаление отменено." && model.DialogBody.Length == 0,
            "cancelled Windows elevation clears progress and leaves a retryable dialog");
        Check(Directory.Exists(owned) && File.Exists(siblingLegacy) && model.TLauncherBlocked,
            "cancelled Windows elevation preserves both launchers and the gate");
        var errorNode = view.Find("dialogError");
        Check(errorNode.Bounds.Height > 0 && errorNode.Bounds.Y >= 40 && errorNode.Bounds.Y + errorNode.Bounds.Height <= 620,
            "elevation cancellation stays visible above the retry action");
        Capture(host, "tlauncher-admin-cancel-light");
        model.TLauncherRemover = null;
        Click("dialogAccept"); Until(() => !model.DialogOpen);
        Check(!Directory.Exists(owned) && File.Exists(siblingLegacy), "confirmed folder cleanup removes private runtime and cache while preserving sibling Legacy");
        Check(!model.TLauncherBlocked && model.Settings.KnownTLauncherDirectories.Length == 0, "removed installation clears persisted roots and unlocks the launcher");

        string helperRoot = Path.Combine(output, "cleanup-helper");
        string helperApp = Path.Combine(helperRoot, ".tlauncher"); Directory.CreateDirectory(helperApp);
        string helperFile = Path.Combine(helperApp, "tlauncher-2.0.properties");
        File.WriteAllText(helperFile, "server=https://tlauncher.org/client");
        string requestPath = Path.Combine(helperRoot, "request.json");
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(TLauncherCleanupRequest.Create(TLauncherDetector.ScanFolder(helperApp), Path.Combine(helperRoot, "backups")));
        File.WriteAllBytes(requestPath, payload);
        string hash = Convert.ToHexString(SHA256.HashData(payload));
        var startInfo = TLauncherCleanupProcess.CreateStartInfo(Path.Combine(helperRoot, "Mechanica Launcher.exe"), requestPath, hash);
        Check(startInfo.UseShellExecute && startInfo.Verb == "runas" && startInfo.WindowStyle == System.Diagnostics.ProcessWindowStyle.Hidden,
            "cleanup requests Windows elevation only for its helper");
        Check(startInfo.ArgumentList.Count == 3 && startInfo.ArgumentList[0] == "--tlauncher-cleanup" && startInfo.ArgumentList[1] == requestPath,
            "helper arguments preserve paths containing spaces");
        var tampered = TLauncherCleanupProcess.RunHelperAsync(requestPath, new string('0', 64));
        Until(() => tampered.IsCompleted);
        Check(tampered.IsFaulted && File.Exists(helperFile) && !File.Exists(requestPath + ".result.json"), "changed cleanup request cannot remove files");
        var helper = TLauncherCleanupProcess.RunHelperAsync(requestPath, hash); Until(() => helper.IsCompleted);
        Check(helper.GetAwaiter().GetResult() == 0 && !Directory.Exists(helperApp), "cleanup helper completes an approved folder removal");
        var response = JsonSerializer.Deserialize<TLauncherCleanupProcess.Response>(File.ReadAllText(requestPath + ".result.json"));
        Check(response?.Result?.Skipped.Count == 0 && response.Result.BackupDirectory != null,
            "helper returns its verified backup and cleanup result");
        Directory.CreateDirectory(Path.Combine(helperApp, "cache"));
        File.WriteAllText(helperFile, "server=https://tlauncher.org/client");
        string cacheFile = Path.Combine(helperApp, "cache", "keep.bin"); File.WriteAllText(cacheFile, "shared cache");
        payload = JsonSerializer.SerializeToUtf8Bytes(TLauncherCleanupRequest.Create(TLauncherDetector.ScanFolder(helperApp), Path.Combine(helperRoot, "backups")));
        requestPath = Path.Combine(helperRoot, "legacy-request.json"); File.WriteAllBytes(requestPath, payload);
        string newLegacy = Path.Combine(helperApp, "tlauncher.properties"); File.WriteAllText(newLegacy, "bootstrap.brand=legacy");
        helper = TLauncherCleanupProcess.RunHelperAsync(requestPath, Convert.ToHexString(SHA256.HashData(payload))); Until(() => helper.IsCompleted);
        Check(helper.GetAwaiter().GetResult() == 0 && File.Exists(newLegacy) && File.Exists(cacheFile),
            "helper rechecks newly appeared Legacy before removing a whole folder");
    }
}
