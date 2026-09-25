using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using MechanicaLauncher.Core.IO;
using MechanicaLauncher.Core.Models;

namespace MechanicaLauncher.Core.Game;

internal static class LoaderInstaller
{
    public static async Task InstallAsync(HttpClient http, string name, string installerUrl,
        string mcVersion, string versionId, string sharedDir, string gameDir, string? javaPath,
        Action<string, double>? onProgress, CancellationToken cancellationToken, string? requiredLibrary = null)
    {
        var vanilla = await new VersionManager(sharedDir, http).GetVersionMetaAsync(mcVersion, cancellationToken);
        var requiredJava = vanilla.JavaVersion?.MajorVersion ?? 8;
        javaPath ??= JavaFinder.FindJava(requiredMajor: requiredJava);
        if (javaPath == null) throw new InvalidOperationException($"{name} requires Java {requiredJava}. Install the game runtime first.");
        JavaFinder.ValidateJava(javaPath, requiredJava);

        var installerPath = Path.Combine(Path.GetTempPath(), $"mechanica-{Guid.NewGuid():N}-installer.jar");
        try
        {
            onProgress?.Invoke($"Downloading {name} installer...", 10);
            await FileDownloader.EnsureAsync(http, installerUrl, installerPath, cancellationToken: cancellationToken);

            string json;
            using (var zip = ZipFile.OpenRead(installerPath))
            {
                var entry = zip.GetEntry("version.json")
                    ?? throw new NotSupportedException($"This {name} installer format is not supported: version.json is missing.");
                using var reader = new StreamReader(entry.Open());
                json = await reader.ReadToEndAsync(cancellationToken);
            }
            var profile = JsonSerializer.Deserialize<VersionMeta>(json);
            if (profile == null || string.IsNullOrEmpty(profile.MainClass) || profile.InheritsFrom != mcVersion)
                throw new InvalidDataException($"{name} profile does not match Minecraft {mcVersion}.");

            var profilesPath = Path.Combine(sharedDir, "launcher_profiles.json");
            if (!File.Exists(profilesPath))
                await AtomicFile.WriteTextAsync(profilesPath, "{\"profiles\":{}}", cancellationToken);

            var logPath = Path.Combine(gameDir, "logs", $"{name.ToLowerInvariant()}-installer.log");
            onProgress?.Invoke($"Running {name} installer...", 40);
            await RunProcessAsync(installerPath, sharedDir, javaPath, logPath, onProgress, cancellationToken);

            if (requiredLibrary != null)
            {
                var requiredPath = FileDownloader.GetPath(Path.Combine(sharedDir, "libraries"), requiredLibrary);
                if (!await FileDownloader.IsValidAsync(requiredPath, cancellationToken: cancellationToken))
                    throw new InvalidDataException($"{name} installer did not produce {requiredLibrary}. Log: {logPath}");
            }

            var downloader = new AssetDownloader(sharedDir, gameDir, http);
            downloader.ProgressChanged += (status, _) => onProgress?.Invoke(status, 90);
            await downloader.DownloadVersionAsync(new VersionMeta { Id = mcVersion, Libraries = profile.Libraries }, cancellationToken);

            var versionDir = FileDownloader.GetPath(gameDir, $"versions/{versionId}");
            await AtomicFile.WriteTextAsync(Path.Combine(versionDir, $"{versionId}.json"), json, cancellationToken);
            await AtomicFile.WriteTextAsync(Path.Combine(versionDir, ".complete"), "1", cancellationToken);
            onProgress?.Invoke($"{name} installed!", 100);
        }
        finally
        {
            AtomicFile.TryDelete(installerPath);
        }
    }

    internal static async Task RunProcessAsync(string installerPath, string sharedDir, string javaPath,
        string logPath, Action<string, double>? onProgress, CancellationToken cancellationToken)
    {
        var javaExe = Path.Combine(Path.GetDirectoryName(javaPath)!, "java.exe");
        if (!File.Exists(javaExe)) javaExe = javaPath;
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        using var log = TextWriter.Synchronized(new StreamWriter(logPath) { AutoFlush = true });
        var tail = new ConcurrentQueue<string>();
        var psi = new ProcessStartInfo
        {
            FileName = javaExe,
            WorkingDirectory = Path.GetDirectoryName(logPath)!,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var arg in new[] { "-jar", installerPath, "--installClient", sharedDir })
            psi.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = psi };
        void OnOutput(object sender, DataReceivedEventArgs args)
        {
            if (args.Data == null) return;
            tail.Enqueue(args.Data);
            while (tail.Count > 40) tail.TryDequeue(out _);
            try { log.WriteLine(args.Data); }
            catch (IOException ex) { Debug.WriteLine(ex.Message); }
            if (args.Data.StartsWith("Processor:", StringComparison.OrdinalIgnoreCase))
                onProgress?.Invoke(args.Data[..Math.Min(120, args.Data.Length)], 60);
        }
        process.OutputDataReceived += OnOutput;
        process.ErrorDataReceived += OnOutput;
        cancellationToken.ThrowIfCancellationRequested();
        if (!process.Start()) throw new InvalidOperationException("Could not start the loader installer.");
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(10));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException($"Loader installation exceeded 10 minutes. Log: {logPath}");
        }

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"Loader installer exited with code {process.ExitCode}. Log: {logPath}\n{string.Join("\n", tail)}");
    }
}
