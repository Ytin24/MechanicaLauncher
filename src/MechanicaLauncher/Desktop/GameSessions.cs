using System.Collections.Concurrent;
using MechanicaLauncher.Core.Auth;
using MechanicaLauncher.Core.Discord;
using MechanicaLauncher.Core.Game;
using MechanicaLauncher.Core.Mods;
using MechanicaLauncher.Core.Models;
using MechanicaLauncher.Core.Config;
using MechanicaLauncher.Core.Servers;

namespace MechanicaLauncher.Desktop;

public sealed partial class GameSessions(LauncherSettings settings, InstanceManager instances) : IDisposable
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
    public bool IsBusy(string id) => preparingId == id || running.ContainsKey(id) || IsSyncBusy(id) || Downloads.Jobs.Any(j => j.InstanceId == id && j.State is DownloadState.Queued or DownloadState.Running);
    public void Cancel() => preparation?.Cancel();
    // A negative progress means "status only": keep the bar where it is instead of snapping it back to zero.
    private void Report(string status, double progress = 0) { Status = status; if (progress >= 0) Progress = Math.Clamp(progress, 0, 100); Changed?.Invoke(); }
    public void Stop(string id)
    {
        if (running.TryGetValue(id, out var process))
        {
            stopped[id] = true;
            try { process.Kill(entireProcessTree: true); }
            catch { stopped.TryRemove(id, out _); throw; }
        }
    }
    public DownloadJob Enqueue(string title, string? instanceId, Func<CancellationToken, Task> work) => EnqueueCore(title, instanceId, work, false);
    private readonly ConcurrentDictionary<string, DownloadJob> backgroundPreparations = new();
    public DownloadJob? BackgroundPreparation(string id) =>
        backgroundPreparations.TryGetValue(id, out var job) && job.State is DownloadState.Queued or DownloadState.Running ? job : null;
    // Downloads Java, the game and the loader right after an instance is created, so the first Play starts at once.
    public DownloadJob PrepareInBackground(GameInstance instance, string title, string readyStatus)
    {
        var job = Enqueue(title, instance.Id, async token =>
        {
            await GamePreparer.PrepareAsync(instance, instances, Report, token);
            Report(readyStatus, 100);
        });
        backgroundPreparations[instance.Id] = job;
        return job;
    }
    private DownloadJob EnqueueCore(string title, string? instanceId, Func<CancellationToken, Task> work, bool serverSync) => Downloads.Enqueue(title, instanceId, async token =>
    {
        await preparationGate.WaitAsync(token);
        try
        {
            if (instanceId != null && (IsRunning(instanceId) || !serverSync && IsSyncBusy(instanceId))) throw new InvalidOperationException(Locale.Get("feature.busy"));
            preparingId = instanceId ?? "import"; preparation = DownloadQueue.CurrentCancellation; Changed?.Invoke();
            await work(token);
        }
        finally { preparingId = null; preparation = null; preparationGate.Release(); Changed?.Invoke(); }
    });
    public Task LaunchAsync(GameInstance instance, string? server, int? port, Func<string, Task<bool>> confirmCompatibility) =>
        LaunchCoreAsync(instance, server, port, confirmCompatibility, false);

    private async Task LaunchCoreAsync(GameInstance instance, string? server, int? port, Func<string, Task<bool>> confirmCompatibility, bool syncRestart)
    {
        ObjectDisposedException.ThrowIf(syncLifetime.IsCancellationRequested, this);
        if (running.ContainsKey(instance.Id) || IsSyncBusy(instance.Id) || !await preparationGate.WaitAsync(0)) throw new InvalidOperationException(Locale.Get("feature.busy"));
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
        BridgeRun? unboundBridge = null;
        try
        {
            Report(Locale.Get("home.loading"));
            var gameDir = instances.GetGameDir(instance.Id);
            await new ServerModSync().RecoverAsync(gameDir, token);
            await new ModUpdateService().RecoverAsync(gameDir, () => IsRunning(instance.Id), token);
            await InstallBundledBridgeAsync(instance, gameDir, token);
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
            var (meta, java, modded) = await GamePreparer.PrepareAsync(instance, instances, Report, token);
            if (Events.Active?.Integrity is { CheckBeforeLaunch: true })
            {
                var integrity = await IntegrityChecker.VerifyAsync(Events.Active, gameDir);
                if (!integrity.IsValid && Events.Active.Integrity.BlockOnFailure) throw new InvalidOperationException(string.Join("\n", integrity.Violations));
            }
            token.ThrowIfCancellationRequested();
            var eventServer = Events.Active?.Server;
            unboundBridge = instance.UseServerModSync ? CreateBridgeRun(instance, syncRestart) : null;
            var process = new GameLauncher(gameDir, instances.SharedDir).Launch(meta, java, settings.Username, settings.Uuid,
                settings.AuthMode == "microsoft" ? settings.AccessToken : "0", instance.MinMemoryMb, instance.MaxMemoryMb, instance.JvmArgs,
                instance.WindowWidth, instance.WindowHeight, modded ? instance.McVersion : null,
                server ?? (eventServer?.AutoConnect == true ? eventServer.Host : null), port ?? (eventServer?.AutoConnect == true ? eventServer.Port : null),
                unboundBridge?.Session.Environment);
            var bridge = unboundBridge;
            try { bridge?.Session.BindProcess(process); }
            catch { try { process.Kill(entireProcessTree: true); } finally { process.Dispose(); } throw; }
            unboundBridge = null;
            if (bridge != null) bridges[instance.Id] = bridge.Session;
            int processId = process.Id;
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
            if (bridge != null) bridge.Trace = Append;
            running[instance.Id] = process;
            process.BeginOutputReadLine(); process.BeginErrorReadLine();
            ProcessStarted?.Invoke(instance.Id, processId);
            var latest = instances.GetInstance(instance.Id);
            if (latest != null) { latest.LastPlayed = DateTime.UtcNow; instances.SaveInstance(latest); }
            tracking.Job.Complete(); Report(Locale.CurrentLanguage == "ru" ? "Minecraft запущен" : "Minecraft is running", 100); GameStarted?.Invoke();
            _ = Task.Run(async () =>
            {
                int code = -1;
                bool wasStopped = false;
                try
                {
                    await process.WaitForExitAsync();
                    process.WaitForExit();
                    code = process.ExitCode;
                    wasStopped = stopped.TryRemove(instance.Id, out _);
                    if (!wasStopped && code != 0)
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
                    writer.Dispose(); process.Dispose();
                    if (bridge != null)
                    {
                        bridges.TryRemove(new KeyValuePair<string, GameBridgeSession>(instance.Id, bridge.Session)); bridge.Session.Dispose();
                    }
                    running.TryRemove(new KeyValuePair<string, Process>(instance.Id, process));
                    ProcessExited?.Invoke(instance.Id, processId, code);
                    Discord.EndSession(session);
                    bool restarted = bridge != null && await CompleteBridgeAsync(bridge, instance, code, wasStopped, confirmCompatibility);
                    Changed?.Invoke();
                    if (!restarted) GameExited?.Invoke();
                }
            });
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { Report(Locale.Get("home.cancelled")); }
        catch (Exception ex) { tracking.Job.Fail(ex); Report(ex.Message); throw; }
        finally
        {
            unboundBridge?.Session.Dispose(); log?.Dispose(); Discord.EndPreparation(session); preparingId = null; preparation = null;
            preparationGate.Release(); Changed?.Invoke();
        }
    }
    public void Dispose() { syncLifetime.Cancel(); foreach (var bridge in bridges.Values) bridge.Dispose(); Cancel(); Discord.Dispose(); }
}
