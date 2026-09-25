using System.Diagnostics;
using System.Text.Json;
using System.Runtime.InteropServices;
using MechanicaLauncher.Core.Game;
using MechanicaLauncher.Core.Instances;
using MechanicaLauncher.Core.Models;

internal static class SmokeTests
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("--smoke <isolated directory> <Minecraft version|latest> <None|Fabric|Quilt|Forge|NeoForge> [loader version]");
            return 2;
        }
        var root = Path.GetFullPath(args[0]);
        var marker = Path.Combine(root, ".mechanica-smoke");
        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any() && !File.Exists(marker))
            throw new InvalidOperationException("Smoke tests require an empty directory or a previous smoke-test directory.");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(marker, "Isolated integration test data. Not used by the launcher.");
        var report = new SmokeReport { StartedAt = DateTimeOffset.UtcNow, Minecraft = args[1], Loader = args[2] };
        var reportPath = Path.Combine(root, $"{args[1]}-{args[2]}-result.json");
        try
        {
            var manager = new InstanceManager(root);
            var versions = new VersionManager(manager.SharedDir);
            if (report.Minecraft == "latest") report.Minecraft = (await versions.GetManifestAsync()).Latest.Release;
            var vanilla = await versions.GetVersionMetaAsync(report.Minecraft);
            var loader = Enum.Parse<LoaderType>(report.Loader, ignoreCase: true);
            var instance = manager.GetAllInstances().FirstOrDefault(i => i.McVersion == report.Minecraft && i.Loader == loader)
                ?? manager.CreateInstance($"smoke-{report.Minecraft}-{loader.ToString().ToLowerInvariant()}", report.Minecraft, loader);
            var gameDir = manager.GetGameDir(instance.Id);
            Directory.CreateDirectory(gameDir);
            // Keep this run small and windowed without changing any real instance's preferences.
            await File.WriteAllTextAsync(Path.Combine(gameDir, "options.txt"), "fullscreen:false\nrenderDistance:4\nmaxFps:30\ninitialTutorialCompleted:true\n");
            var major = vanilla.JavaVersion?.MajorVersion ?? 8;
            var component = vanilla.JavaVersion?.Component ?? "jre-legacy";
            var localJava = Path.Combine(manager.SharedRuntimeDir, component, "windows-x64", component, "bin", "javaw.exe");
            var java = File.Exists(Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(localJava)!)!, ".complete"))
                ? localJava : JavaFinder.FindJava(component, major);
            java ??= await JavaFinder.DownloadJavaAsync(component, manager.SharedDir, s => Progress(s, -1));
            if (java == null) throw new InvalidOperationException($"Java {major} runtime is unavailable.");
            JavaFinder.ValidateJava(java, major);
            report.Java = java;
            report.JavaMajor = major;
            var downloader = new AssetDownloader(manager.SharedDir, gameDir);
            downloader.ProgressChanged += Progress;
            report.Phase = "vanilla installation";
            await downloader.DownloadVersionAsync(vanilla);
            report.Phase = "loader installation";
            var explicitVersion = args.ElementAtOrDefault(3);
            switch (loader)
            {
                case LoaderType.Fabric:
                    var fabric = new FabricInstaller(manager.SharedDir, gameDir);
                    fabric.ProgressChanged += Progress;
                    instance.LoaderVersion = explicitVersion ?? (await fabric.GetLoaderVersionsAsync(report.Minecraft)).First(v => v.Stable).Version;
                    await fabric.InstallAsync(report.Minecraft, instance.LoaderVersion);
                    break;
                case LoaderType.Quilt:
                    var quilt = new QuiltInstaller(manager.SharedDir, gameDir);
                    quilt.ProgressChanged += Progress;
                    instance.LoaderVersion = explicitVersion ?? (await quilt.GetLoaderVersionsAsync(report.Minecraft)).First();
                    await quilt.InstallAsync(report.Minecraft, instance.LoaderVersion);
                    break;
                case LoaderType.Forge:
                    var forge = new ForgeInstaller(manager.SharedDir, gameDir);
                    forge.ProgressChanged += Progress;
                    instance.LoaderVersion = explicitVersion ?? (await forge.GetVersionsAsync(report.Minecraft)).First();
                    await forge.InstallAsync(report.Minecraft, instance.LoaderVersion, java);
                    break;
                case LoaderType.NeoForge:
                    var neo = new NeoForgeInstaller(manager.SharedDir, gameDir);
                    neo.ProgressChanged += Progress;
                    instance.LoaderVersion = explicitVersion ?? (await neo.GetVersionsAsync(report.Minecraft)).First();
                    await neo.InstallAsync(report.Minecraft, instance.LoaderVersion, java);
                    break;
            }
            report.LoaderVersion = instance.LoaderVersion;
            manager.SaveInstance(instance);
            var meta = loader == LoaderType.None ? vanilla : await versions.GetMergedMetaAsync(instance.GetEffectiveVersionId(), gameDir);
            for (int pass = 1; pass <= 2; pass++)
            {
                report.Phase = $"launch {pass}";
                await downloader.DownloadVersionAsync(vanilla);
                if (loader != LoaderType.None)
                    await downloader.DownloadVersionAsync(new VersionMeta { Id = vanilla.Id, Libraries = meta.Libraries });
                await LaunchAsync(meta, java, gameDir, manager.SharedDir, vanilla.Id, pass);
                report.CompletedLaunches = pass;
            }
            report.Phase = "complete";
            report.Passed = true;
            Console.WriteLine($"PASS {report.Minecraft} {report.Loader} {report.LoaderVersion}: two launches and clean exits");
            return 0;
        }
        catch (Exception ex)
        {
            report.Error = ex.ToString();
            Console.Error.WriteLine($"FAIL {report.Minecraft} {report.Loader} ({report.Phase}): {ex}");
            return 1;
        }
        finally
        {
            report.FinishedAt = DateTimeOffset.UtcNow;
            await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    private static async Task LaunchAsync(VersionMeta meta, string java, string gameDir, string sharedDir, string vanillaId, int pass)
    {
        var logPath = Path.Combine(gameDir, "logs", $"smoke-{pass}.log");
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        using var log = TextWriter.Synchronized(new StreamWriter(logPath) { AutoFlush = true });
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var process = new GameLauncher(gameDir, sharedDir).Launch(meta, java, "MechanicaTest",
            minMem: 512, maxMem: 2048, windowWidth: 960, windowHeight: 540, vanillaVersionId: vanillaId);
        void Output(object sender, DataReceivedEventArgs e)
        {
            if (e.Data == null) return;
            log.WriteLine(e.Data);
            if (e.Data.Contains("Created:", StringComparison.Ordinal) &&
                (e.Data.Contains("atlas", StringComparison.OrdinalIgnoreCase) || e.Data.Contains("textures", StringComparison.OrdinalIgnoreCase)))
                ready.TrySetResult();
        }
        process.OutputDataReceived += Output;
        process.ErrorDataReceived += Output;
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        Console.WriteLine($"START pass={pass} pid={process.Id} log={logPath}");
        var exited = process.WaitForExitAsync();
        try
        {
            var outcome = await Task.WhenAny(ready.Task, exited, Task.Delay(TimeSpan.FromMinutes(3)));
            if (outcome == exited) throw new InvalidOperationException($"Minecraft exited before rendering, code {process.ExitCode}. Log: {logPath}");
            if (outcome != ready.Task) throw new TimeoutException($"Minecraft did not initialize textures in 3 minutes. Log: {logPath}");
            await Task.Delay(TimeSpan.FromSeconds(15));
            if (process.HasExited) throw new InvalidOperationException($"Minecraft exited after initialization, code {process.ExitCode}. Log: {logPath}");
            process.Refresh();
            if (process.MainWindowHandle == IntPtr.Zero) throw new InvalidOperationException($"Minecraft has no game window. Log: {logPath}");
            Console.WriteLine($"READY pass={pass} title={process.MainWindowTitle}");
            // LWJGL 2 handles SC_CLOSE, but ignores the WM_CLOSE sent by Process.CloseMainWindow.
            var closeRequested = meta.Libraries.Any(l => l.Name.StartsWith("org.lwjgl.lwjgl:lwjgl:"))
                ? PostMessage(process.MainWindowHandle, 0x0112, (IntPtr)0xF060, IntPtr.Zero)
                : process.CloseMainWindow();
            if (!closeRequested) throw new InvalidOperationException("Minecraft did not accept a window-close request.");
            await exited.WaitAsync(TimeSpan.FromSeconds(30));
            if (process.ExitCode != 0) throw new InvalidOperationException($"Minecraft close returned {process.ExitCode}. Log: {logPath}");
            Console.WriteLine($"CLOSED pass={pass} exit={process.ExitCode}");
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await exited.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    private static DateTime _lastProgress;
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    private static void Progress(string status, double percent)
    {
        if (DateTime.UtcNow - _lastProgress < TimeSpan.FromSeconds(5) && percent is > 0 and < 100) return;
        _lastProgress = DateTime.UtcNow;
        Console.WriteLine($"{DateTimeOffset.Now:HH:mm:ss} {status}");
    }

    private sealed class SmokeReport
    {
        public DateTimeOffset StartedAt { get; set; }
        public DateTimeOffset FinishedAt { get; set; }
        public string Minecraft { get; set; } = "";
        public string Loader { get; set; } = "";
        public string? LoaderVersion { get; set; }
        public string? Java { get; set; }
        public int JavaMajor { get; set; }
        public string Phase { get; set; } = "metadata";
        public int CompletedLaunches { get; set; }
        public bool Passed { get; set; }
        public string? Error { get; set; }
    }
}
