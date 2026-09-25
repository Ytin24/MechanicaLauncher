using System.Collections.Concurrent;
using MechanicaLauncher.Core.Auth;
using MechanicaLauncher.Core.Discord;
using MechanicaLauncher.Core.Game;
using MechanicaLauncher.Core.Mods;
using MechanicaLauncher.Core.Models;
using MechanicaLauncher.Core.Config;

namespace MechanicaLauncher.Desktop;

public sealed class GameSessions(LauncherSettings settings, InstanceManager instances) : IDisposable
{
    private readonly SemaphoreSlim preparationGate = new(1, 1);
    private readonly ConcurrentDictionary<string, Process> running = new();
    private readonly ConcurrentDictionary<string, bool> stopped = new();
    private readonly ConcurrentQueue<string> lines = new();
    private CancellationTokenSource? preparation;
    private string? preparingId;
    public DownloadQueue Downloads { get; } = new();
    public DiscordPresence Discord { get; } = new();
    public EventConfigManager Events { get; } = new();
    public event Action? Changed;
    public event Action? GameExited;
    public event Action? GameStarted;
    public string Status { get; private set; } = "";
    public double Progress { get; private set; }
    public string Log => string.Join("\n", lines);
    public bool Preparing => preparation != null;
    public int RunningCount => running.Count;
    public bool IsRunning(string id)
    {
        if (!running.TryGetValue(id, out var process)) return false;
        try { return !process.HasExited; }
        catch (InvalidOperationException) { return false; }
    }
    public bool IsBusy(string id) => preparingId == id || IsRunning(id) || Downloads.Jobs.Any(j => j.InstanceId == id && j.State is DownloadState.Queued or DownloadState.Running);
    public void Cancel() => preparation?.Cancel();
    private void Report(string status, double progress = 0) { Status = status; Progress = Math.Clamp(progress, 0, 100); Changed?.Invoke(); }
    public void Stop(string id)
    {
        if (running.TryGetValue(id, out var process))
        {
            stopped[id] = true;
            try { process.Kill(entireProcessTree: true); }
            catch { stopped.TryRemove(id, out _); throw; }
        }
    }
    public DownloadJob Enqueue(string title, string? instanceId, Func<CancellationToken, Task> work) => Downloads.Enqueue(title, instanceId, async token =>
    {
        await preparationGate.WaitAsync(token);
        try
        {
            if (instanceId != null && IsRunning(instanceId)) throw new InvalidOperationException(Locale.Get("feature.busy"));
            preparingId = instanceId ?? "import"; preparation = DownloadQueue.CurrentCancellation; Changed?.Invoke();
            await work(token);
        }
        finally { preparingId = null; preparation = null; preparationGate.Release(); Changed?.Invoke(); }
    });
    public async Task LaunchAsync(GameInstance instance, string? server, int? port, Func<string, Task<bool>> confirmCompatibility)
    {
        if (IsRunning(instance.Id) || !await preparationGate.WaitAsync(0)) throw new InvalidOperationException(Locale.Get("feature.busy"));
        using var cancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        using var tracking = Downloads.Track(instance.Name, instance.Id, cancellation, () => _ = RetryLaunch());
        async Task RetryLaunch()
        {
            try { await LaunchAsync(instance, server, port, confirmCompatibility); }
            catch (Exception ex) { Report(ex.Message); }
        }
        preparation = cancellation; preparingId = instance.Id;
        var session = Discord.BeginPreparation(instance);
        TextWriter? log = null;
        try
        {
            Report(Locale.Get("home.loading"));
            var gameDir = instances.GetGameDir(instance.Id);
            Directory.CreateDirectory(Path.Combine(gameDir, "logs"));
            log = TextWriter.Synchronized(new StreamWriter(Path.Combine(gameDir, "logs", "launcher-latest.log")) { AutoFlush = true });
            log.WriteLine($"{DateTimeOffset.Now:O} {instance.Name} · {instance.McVersion} · {instance.Loader}");
            var compatibility = await new ModCompatibilityChecker().CheckAsync(instance, gameDir, false, token);
            if (compatibility.Issues.Any(i => i.IsError) && !await confirmCompatibility(string.Join("\n\n", compatibility.Issues.Where(i => i.IsError).Take(5).Select(i => Locale.Get("compat." + i.Code) + "\n" + i.Detail))))
            { cancellation.Cancel(); token.ThrowIfCancellationRequested(); }
            if (settings.AuthMode == "microsoft")
            {
                if (string.IsNullOrWhiteSpace(settings.AccessToken) || settings.AccessToken == "0") throw new InvalidOperationException(Locale.Get("acc.session_expired"));
                if (!await MicrosoftAuth.ValidateTokenAsync(settings.AccessToken, token))
                {
                    if (string.IsNullOrEmpty(settings.MsRefreshToken)) throw new InvalidOperationException(Locale.Get("acc.session_expired"));
                    var auth = await new MicrosoftAuth(settings.MsClientId).RefreshAsync(settings.MsRefreshToken, token);
                    settings.Username = auth.Username; settings.Uuid = auth.Uuid; settings.AccessToken = auth.AccessToken;
                    settings.MsRefreshToken = auth.RefreshToken ?? settings.MsRefreshToken; settings.Save();
                }
            }
            var versions = new VersionManager(instances.SharedDir);
            var vanilla = await versions.GetVersionMetaAsync(instance.McVersion, token);
            var component = vanilla.JavaVersion?.Component ?? "jre-legacy";
            int requiredJava = vanilla.JavaVersion?.MajorVersion ?? 8;
            var java = !string.IsNullOrEmpty(instance.JavaPath) ? instance.JavaPath : JavaFinder.FindJava(component, requiredJava);
            java ??= await JavaFinder.DownloadJavaAsync(component, instances.SharedDir, status => Report(status), token);
            if (java == null) throw new InvalidOperationException("Java download failed.");
            JavaFinder.ValidateJava(java, requiredJava);
            var downloader = new AssetDownloader(instances.SharedDir, gameDir);
            downloader.ProgressChanged += Report;
            await Task.Run(() => downloader.DownloadVersionAsync(vanilla, token), token);
            bool modded = instance.Loader != LoaderType.None;
            string versionId = instance.GetEffectiveVersionId();
            if (modded)
            {
                if (string.IsNullOrWhiteSpace(instance.LoaderVersion)) throw new InvalidOperationException("No mod loader version selected.");
                var versionDir = Path.Combine(gameDir, "versions", versionId);
                if (!File.Exists(Path.Combine(versionDir, versionId + ".json")) || !File.Exists(Path.Combine(versionDir, ".complete")))
                {
                    switch (instance.Loader)
                    {
                        case LoaderType.Fabric:
                            var fabric = new FabricInstaller(instances.SharedDir, gameDir); fabric.ProgressChanged += Report;
                            await fabric.InstallAsync(instance.McVersion, instance.LoaderVersion, token); break;
                        case LoaderType.Quilt:
                            var quilt = new QuiltInstaller(instances.SharedDir, gameDir); quilt.ProgressChanged += Report;
                            await quilt.InstallAsync(instance.McVersion, instance.LoaderVersion, token); break;
                        case LoaderType.Forge:
                            var forge = new ForgeInstaller(instances.SharedDir, gameDir); forge.ProgressChanged += Report;
                            await forge.InstallAsync(instance.McVersion, instance.LoaderVersion, java, token); break;
                        case LoaderType.NeoForge:
                            var neo = new NeoForgeInstaller(instances.SharedDir, gameDir); neo.ProgressChanged += Report;
                            await neo.InstallAsync(instance.McVersion, instance.LoaderVersion, java, token); break;
                    }
                }
            }
            var meta = modded ? await versions.GetMergedMetaAsync(versionId, gameDir, token) : vanilla;
            if (modded) await Task.Run(() => downloader.DownloadVersionAsync(new VersionMeta { Id = instance.McVersion, Libraries = meta.Libraries }, token), token);
            if (Events.Active?.Integrity is { CheckBeforeLaunch: true })
            {
                var integrity = await IntegrityChecker.VerifyAsync(Events.Active, gameDir);
                if (!integrity.IsValid && Events.Active.Integrity.BlockOnFailure) throw new InvalidOperationException(string.Join("\n", integrity.Violations));
            }
            token.ThrowIfCancellationRequested();
            var eventServer = Events.Active?.Server;
            var process = new GameLauncher(gameDir, instances.SharedDir).Launch(meta, java, settings.Username, settings.Uuid,
                settings.AuthMode == "microsoft" ? settings.AccessToken : "0", instance.MinMemoryMb, instance.MaxMemoryMb, instance.JvmArgs,
                instance.WindowWidth, instance.WindowHeight, modded ? instance.McVersion : null,
                server ?? (eventServer?.AutoConnect == true ? eventServer.Host : null), port ?? (eventServer?.AutoConnect == true ? eventServer.Port : null));
            int modCount = Directory.Exists(Path.Combine(gameDir, "mods")) ? Directory.GetFiles(Path.Combine(gameDir, "mods"), "*.jar").Length : 0;
            Discord.GameStarted(session, modCount);
            lines.Clear();
            var writer = log; log = null;
            var crashLines = new ConcurrentQueue<string>();
            void Append(string? value)
            {
                if (value == null) return;
                string safe = CrashAnalyzer.Redact(value, [settings.AccessToken, settings.MsRefreshToken]);
                lines.Enqueue(safe[..Math.Min(safe.Length, 4000)]); while (lines.Count > 200) lines.TryDequeue(out _);
                crashLines.Enqueue(safe[..Math.Min(safe.Length, 4000)]); while (crashLines.Count > 500) crashLines.TryDequeue(out _);
                Discord.ProcessLogLine(session, value);
                try { writer.WriteLine(safe); } catch (IOException) { } catch (ObjectDisposedException) { }
            }
            process.OutputDataReceived += (_, e) => Append(e.Data);
            process.ErrorDataReceived += (_, e) => Append(e.Data);
            running[instance.Id] = process;
            process.BeginOutputReadLine(); process.BeginErrorReadLine();
            var latest = instances.GetInstance(instance.Id);
            if (latest != null) { latest.LastPlayed = DateTime.UtcNow; instances.SaveInstance(latest); }
            tracking.Job.Complete(); Report(Locale.CurrentLanguage == "ru" ? "Minecraft запущен" : "Minecraft is running", 100); GameStarted?.Invoke();
            _ = Task.Run(async () =>
            {
                try
                {
                    await process.WaitForExitAsync();
                    process.WaitForExit();
                    int code = process.ExitCode;
                    if (!stopped.TryRemove(instance.Id, out _) && code != 0)
                    {
                        var report = CrashAnalyzer.Analyze(string.Join("\n", crashLines), code, [settings.AccessToken, settings.MsRefreshToken]);
                        CrashAnalyzer.SaveReport(gameDir, report);
                        Report(Locale.Get("crash." + report.Reason));
                    }
                    else Report(Locale.Get("home.game_closed"));
                }
                catch (Exception ex) { Report(ex.Message); }
                finally
                {
                    running.TryRemove(instance.Id, out _); writer.Dispose(); process.Dispose();
                    Discord.EndSession(session); Changed?.Invoke(); GameExited?.Invoke();
                }
            });
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { Report(Locale.Get("home.cancelled")); }
        catch (Exception ex) { tracking.Job.Fail(ex); Report(ex.Message); throw; }
        finally
        {
            log?.Dispose(); Discord.EndPreparation(session); preparingId = null; preparation = null;
            preparationGate.Release(); Changed?.Invoke();
        }
    }
    public void Dispose() { Cancel(); Discord.Dispose(); }
}
