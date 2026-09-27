using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using MechanicaLauncher.Core.Servers;

namespace MechanicaLauncher.Desktop;

public sealed partial class GameSessions
{
    public Func<FavoriteServer, ServerSyncPlan, CancellationToken, Task<bool>>? ConfirmServerSync { get; set; }
    internal event Action<string, int>? ProcessStarted;
    internal event Action<string, int, int>? ProcessExited;
    private readonly CancellationTokenSource syncLifetime = new();
    private readonly ConcurrentDictionary<string, GameBridgeSession> bridges = new();
    private readonly ConcurrentDictionary<string, BridgeRun> syncBusy = new();
    private const string BridgeFileName = "mechanica-server-sync-1.0.0.jar";

    private sealed class BridgeRun
    {
        public required GameBridgeSession Session { get; init; }
        public FavoriteServer? Server { get; set; }
        public ServerModSync? Sync { get; set; }
        public ServerSyncPlan? Plan { get; set; }
        public ServerSyncStage? Stage { get; set; }
        public bool RestartRequested { get; set; }
        public CancellationTokenSource? Operation { get; set; }
        public DownloadJob? Job { get; set; }
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public object StateGate { get; } = new();
        public long Generation { get; set; }
    }

    public async Task LaunchServerAsync(FavoriteServer server, Func<string, Task<bool>> confirmCompatibility)
    {
        var instance = instances.GetInstance(server.InstanceId) ?? throw new InvalidOperationException("Сборка сервера удалена.");
        if (instance.UseServerModSync && !string.IsNullOrWhiteSpace(server.SyncManifestUrl))
        {
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(syncLifetime.Token);
            if (IsBusy(instance.Id)) throw new InvalidOperationException(Locale.Get("feature.busy"));
            bool proceed = false;
            var job = Enqueue("Моды · " + server.Name, instance.Id, async token =>
            {
                var sync = CreateServerSync(server);
                string gameDir = instances.GetGameDir(instance.Id);
                await sync.RecoverAsync(gameDir, token);
                var plan = await sync.PlanAsync(new Uri(server.SyncManifestUrl), instance, gameDir, server.AllowLocalSync, token);
                CheckSyncPlan(plan);
                if (!plan.HasChanges) { proceed = true; return; }
                if (!await ApproveServerSyncAsync(server, plan, token)) return;
                var stage = await sync.StageAsync(plan, SyncStageDirectory(instance.Id, plan.PlanId), token);
                ValidateSyncServer(server);
                ValidateSyncInstance(instance);
                await sync.ApplyAsync(stage, () => IsRunning(instance.Id), token);
                proceed = true;
            });
            using var registration = cancellation.Token.Register(job.Cancel);
            await job.Completion;
            RequireCompleted(job);
            if (!proceed) return;
            ValidateSyncInstance(instance);
        }
        await LaunchAsync(instance, server.Host, server.Port, confirmCompatibility);
    }

    private static ServerModSync CreateServerSync(FavoriteServer server)
    {
        if (server.SyncManifestUrl == null || !FavoriteServers.IsValidSyncUrl(server.SyncManifestUrl, server.AllowLocalSync))
            throw new InvalidDataException("Неверный адрес списка модов сервера.");
        return new(approvedExternalOrigins: [new Uri(new Uri(server.SyncManifestUrl).GetLeftPart(UriPartial.Authority))]);
    }

    private string SyncStageDirectory(string instanceId, Guid planId) =>
        Path.Combine(instances.SharedDir, "server-sync", instanceId, planId.ToString("N"));

    private static void ValidateSyncServer(FavoriteServer expected)
    {
        var current = new FavoriteServers().Load().FirstOrDefault(server => server.Id == expected.Id);
        if (current == null || current.InstanceId != expected.InstanceId || current.Host != expected.Host || current.Port != expected.Port ||
            current.SyncManifestUrl != expected.SyncManifestUrl || current.AutoSync != expected.AutoSync || current.AllowLocalSync != expected.AllowLocalSync)
            throw new InvalidOperationException("Настройки сервера изменились. Подключись ещё раз.");
    }

    private void ValidateSyncInstance(GameInstance expected)
    {
        var current = instances.GetInstance(expected.Id);
        if (current == null || current.McVersion != expected.McVersion || current.Loader != expected.Loader || current.LoaderVersion != expected.LoaderVersion ||
            current.UseServerModSync != expected.UseServerModSync)
            throw new InvalidOperationException(Locale.Get("catalog.target_changed"));
    }

    private static void CheckSyncPlan(ServerSyncPlan plan)
    {
        if (plan.Conflicts.Count > 0)
            throw new InvalidOperationException("Конфликт модов:\n" + string.Join("\n", plan.Conflicts.Select(c => c.FileName + ": " + c.Reason)));
    }

    private async Task<bool> ApproveServerSyncAsync(FavoriteServer server, ServerSyncPlan plan, CancellationToken token)
    {
        if (Events.Active?.Ui?.AllowModInstall == false) throw new InvalidOperationException(Locale.Get("catalog.install_locked"));
        bool allowed = server.AutoSync || ConfirmServerSync != null && await ConfirmServerSync(server, plan, token).WaitAsync(token);
        token.ThrowIfCancellationRequested();
        ValidateSyncServer(server);
        if (Events.Active?.Ui?.AllowModInstall == false) throw new InvalidOperationException(Locale.Get("catalog.install_locked"));
        return allowed;
    }

    private BridgeRun CreateBridgeRun(GameInstance instance, bool restarted)
    {
        BridgeRun? run = null;
        var session = new GameBridgeSession(instance.Id, async (request, token) =>
        {
            if (!instance.UseServerModSync) return Reply("Result", new { status = "ready" });
            if (request.Type == "Cancel")
            {
                CancellationTokenSource? operation;
                DownloadJob? job;
                lock (run!.StateGate)
                {
                    if (run.Plan is { } activePlan && request.Payload.TryGetProperty("planId", out var cancelId) &&
                        (!cancelId.TryGetGuid(out var requestedId) || requestedId != activePlan.PlanId))
                        return Reply("Result", new { status = "error", code = "invalid_request" });
                    run.Generation++;
                    operation = run.Operation; job = run.Job;
                    run.RestartRequested = false; run.Stage = null; run.Plan = null;
                }
                try { operation?.Cancel(); } catch (ObjectDisposedException) { }
                job?.Cancel();
                syncBusy.TryRemove(new KeyValuePair<string, BridgeRun>(instance.Id, run));
                return Reply("Result", new { status = "cancelled" });
            }
            if (!await run!.Gate.WaitAsync(0, token)) return Reply("Result", new { status = "error", code = "busy" });
            try
            {
                if (request.Type == "PrepareConnection")
                {
                    long generation;
                    lock (run.StateGate)
                    {
                        if (run.Stage != null) return Reply("Result", new { status = "error", code = "busy" });
                        run.Operation?.Dispose();
                        run.Operation = CancellationTokenSource.CreateLinkedTokenSource(token, syncLifetime.Token);
                        token = run.Operation.Token;
                        generation = ++run.Generation;
                        run.Plan = null; run.Job = null;
                    }
                    string host = request.Payload.GetProperty("host").GetString() ?? "";
                    int port = request.Payload.GetProperty("port").GetInt32();
                    var matches = new FavoriteServers().Load().Where(s => s.InstanceId == instance.Id &&
                        s.Host.Equals(host, StringComparison.OrdinalIgnoreCase) && s.Port == port && !string.IsNullOrWhiteSpace(s.SyncManifestUrl)).ToArray();
                    if (matches.Length == 0) return Reply("Result", new { status = "ready" });
                    if (matches.Length != 1) return Reply("Result", new { status = "error", code = "conflict", message = "Несколько настроек для этого сервера." });
                    if (IsSyncBusy(instance.Id)) return Reply("Result", new { status = "error", code = "busy" });
                    run.Server = matches[0]; run.Sync = CreateServerSync(run.Server);
                    Report("Проверяю моды · " + run.Server.Name);
                    var plan = await run.Sync.PlanAsync(new Uri(run.Server.SyncManifestUrl!), instance, instances.GetGameDir(instance.Id), run.Server.AllowLocalSync, token);
                    token.ThrowIfCancellationRequested();
                    CheckSyncPlan(plan);
                    bool verifyRestart = restarted;
                    restarted = false;
                    if (!plan.HasChanges) return Reply("Result", new { status = "ready" });
                    if (verifyRestart) return Reply("Result", new { status = "error", code = "conflict", message = "Список модов изменился после обновления. Подключись ещё раз." });
                    if (!await ApproveServerSyncAsync(run.Server, plan, token)) return Reply("Result", new { status = "cancelled" });
                    lock (run.StateGate)
                    {
                        token.ThrowIfCancellationRequested();
                        if (run.Generation != generation) return Reply("Result", new { status = "cancelled" });
                        run.Plan = plan;
                    }
                    return Reply("Plan", new { planId = plan.PlanId, revision = plan.Revision,
                        manifestSha512 = plan.ManifestSha512, downloadBytes = plan.DownloadBytes, restartRequired = true });
                }
                if (request.Type == "Apply")
                {
                    ServerSyncPlan approvedPlan;
                    long generation;
                    CancellationToken operationToken;
                    lock (run.StateGate)
                    {
                        if (run.Plan == null || run.Sync == null || run.Server == null ||
                            !request.Payload.TryGetProperty("planId", out var id) || !id.TryGetGuid(out var requestedPlan) || requestedPlan != run.Plan.PlanId)
                            return Reply("Result", new { status = "error", code = "invalid_request" });
                        if (run.RestartRequested) return Reply("Result", new { status = "restart_required", planId = requestedPlan });
                        approvedPlan = run.Plan; generation = run.Generation; operationToken = run.Operation!.Token;
                    }
                    var planId = approvedPlan.PlanId;
                    using var applyCancellation = CancellationTokenSource.CreateLinkedTokenSource(token, operationToken, syncLifetime.Token);
                    token = applyCancellation.Token;
                    ValidateSyncServer(run.Server!);
                    if (!syncBusy.TryAdd(instance.Id, run)) return Reply("Result", new { status = "error", code = "busy" });
                    try
                    {
                        ServerSyncStage? staged = null;
                        var job = Downloads.Enqueue("Моды · " + run.Server!.Name, instance.Id, async cancellation =>
                        {
                            if (Events.Active?.Ui?.AllowModInstall == false) throw new InvalidOperationException(Locale.Get("catalog.install_locked"));
                            staged = await run.Sync!.StageAsync(approvedPlan, SyncStageDirectory(instance.Id, planId), cancellation);
                        });
                        lock (run.StateGate) run.Job = job;
                        using var registration = token.Register(job.Cancel);
                        await job.Completion; token.ThrowIfCancellationRequested(); RequireCompleted(job);
                        lock (run.StateGate)
                        {
                            token.ThrowIfCancellationRequested();
                            if (run.Generation != generation) return Reply("Result", new { status = "cancelled" });
                            run.Stage = staged; run.RestartRequested = true;
                        }
                        Report("Моды скачаны. Ожидаю перезапуск Minecraft.");
                        _ = ExpireBridgeRestartAsync(run, instance.Id, generation);
                        return Reply("Result", new { status = "restart_required", planId });
                    }
                    catch { syncBusy.TryRemove(new KeyValuePair<string, BridgeRun>(instance.Id, run)); throw; }
                }
                return Reply("Result", new { status = "error", code = "invalid_request" });
            }
            catch (OperationCanceledException) { return Reply("Result", new { status = "cancelled" }); }
            catch (ServerModSyncException ex)
            {
                Report(ex.Message);
                return Reply("Result", new { status = "error", code = ex.Code, message = ex.Message });
            }
            catch (Exception ex)
            {
                Report(ex.Message);
                return Reply("Result", new { status = "error", code = "internal_error", message = ex.Message });
            }
            finally { run.Gate.Release(); }
        });
        run = new() { Session = session };
        return run;
    }

    private bool IsSyncBusy(string instanceId) => syncBusy.ContainsKey(instanceId);
    private async Task ExpireBridgeRestartAsync(BridgeRun run, string instanceId, long generation)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(1), syncLifetime.Token);
            lock (run.StateGate)
            {
                if (run.Generation != generation || !run.RestartRequested || !IsRunning(instanceId) ||
                    !bridges.TryGetValue(instanceId, out var session) || session != run.Session) return;
                run.Generation++; run.RestartRequested = false; run.Stage = null; run.Plan = null;
            }
            syncBusy.TryRemove(new KeyValuePair<string, BridgeRun>(instanceId, run));
            Report("Minecraft не завершился. Повтори подключение после выхода из игры.");
        }
        catch (OperationCanceledException) { }
    }
    private static BridgeReply Reply(string type, object payload) => new(type, JsonSerializer.SerializeToElement(payload));
    private static void RequireCompleted(DownloadJob job)
    {
        if (job.State == DownloadState.Cancelled) throw new OperationCanceledException();
        if (job.State != DownloadState.Completed) throw new InvalidOperationException(job.Snapshot().Error ?? "Не удалось подготовить моды сервера.");
    }

    private async Task<bool> CompleteBridgeAsync(BridgeRun run, GameInstance instance, int exitCode, bool wasStopped,
        Func<string, Task<bool>> confirmCompatibility)
    {
        bridges.TryRemove(new KeyValuePair<string, GameBridgeSession>(instance.Id, run.Session)); run.Session.Dispose();
        ServerSyncStage stage;
        ServerModSync sync;
        FavoriteServer server;
        long generation;
        lock (run.StateGate)
        {
            if (!run.RestartRequested || run.Stage == null || run.Sync == null || run.Server == null ||
                wasStopped || exitCode != 0 || syncLifetime.IsCancellationRequested)
            { syncBusy.TryRemove(new KeyValuePair<string, BridgeRun>(instance.Id, run)); return false; }
            stage = run.Stage; sync = run.Sync; server = run.Server; generation = run.Generation;
        }
        try
        {
            var job = EnqueueCore("Установка · " + server.Name, instance.Id, async token =>
            {
                if (Events.Active?.Ui?.AllowModInstall == false) throw new InvalidOperationException(Locale.Get("catalog.install_locked"));
                ValidateSyncServer(server);
                ValidateSyncInstance(instance);
                lock (run.StateGate)
                    if (run.Generation != generation || !run.RestartRequested) throw new OperationCanceledException();
                await sync.ApplyAsync(stage, () => IsRunning(instance.Id), token);
            }, true);
            using var registration = syncLifetime.Token.Register(job.Cancel);
            await job.Completion; RequireCompleted(job);
            syncLifetime.Token.ThrowIfCancellationRequested();
            syncBusy.TryRemove(new KeyValuePair<string, BridgeRun>(instance.Id, run));
            await LaunchCoreAsync(instances.GetInstance(instance.Id)!, server.Host, server.Port, confirmCompatibility, true);
            return IsRunning(instance.Id);
        }
        catch (OperationCanceledException) { Report(Locale.Get("home.cancelled")); return false; }
        catch (Exception ex) { Report(ex.Message); return false; }
        finally { syncBusy.TryRemove(new KeyValuePair<string, BridgeRun>(instance.Id, run)); }
    }

    private async Task InstallBundledBridgeAsync(GameInstance instance, string gameDir, CancellationToken token)
    {
        string mods = Path.Combine(gameDir, "mods");
        string target = Path.Combine(mods, BridgeFileName);
        string disabled = target + ".disabled";
        string receipt = Path.Combine(gameDir, ".mechanica", "bridge-sha512.txt");
        foreach (var path in new[] { gameDir, mods, target, disabled, Path.GetDirectoryName(receipt)!, receipt })
            if ((File.Exists(path) || Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Каталог моста содержит ссылку на другой путь.");
        string? ownedHash = File.Exists(receipt) ? (await File.ReadAllTextAsync(receipt, token)).Trim() : null;
        static async Task<string> Hash(string file, CancellationToken cancellation)
        {
            using var stream = File.OpenRead(file);
            return Convert.ToHexString(await SHA512.HashDataAsync(stream, cancellation));
        }
        if (!instance.UseServerModSync)
        {
            if (File.Exists(target) && ownedHash != null)
            {
                if (await Hash(target, token) != ownedHash) throw new InvalidOperationException("Файл Mechanica Server Sync изменён: " + BridgeFileName);
                if (File.Exists(disabled)) throw new InvalidOperationException("Отключённый файл Mechanica Server Sync уже существует.");
                if (Events.Active?.Ui?.AllowModToggle == false) throw new InvalidOperationException(Locale.Get("catalog.install_locked"));
                token.ThrowIfCancellationRequested();
                File.Move(target, disabled);
            }
            return;
        }
        string packageDirectory = Path.Combine(AppContext.BaseDirectory, "bridges");
        string manifest = Path.Combine(packageDirectory, "manifest.json");
        if (!File.Exists(manifest)) throw new FileNotFoundException("Mechanica Server Sync отсутствует в лаунчере.", manifest);
        var packages = JsonSerializer.Deserialize<BridgePackage[]>(await File.ReadAllTextAsync(manifest, token),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
        var package = packages.SingleOrDefault(p => p.Minecraft == instance.McVersion && p.Loader.Equals(instance.Loader.ToString(), StringComparison.OrdinalIgnoreCase));
        if (package == null) throw new InvalidOperationException($"Mechanica Server Sync не поддерживает {instance.Loader} {instance.McVersion}.");
        if (!Version.TryParse(instance.LoaderVersion?.Split('-')[0], out var loaderVersion) ||
            !Version.TryParse(package.MinimumLoaderVersion, out var minimum) || loaderVersion < minimum)
            throw new InvalidOperationException($"Mechanica Server Sync требует {instance.Loader} {package.MinimumLoaderVersion} или новее.");
        if (string.IsNullOrWhiteSpace(package.FileName) || package.FileName != Path.GetFileName(package.FileName) ||
            package.FileName.IndexOfAny(['/', '\\', ':']) >= 0 || !package.FileName.EndsWith(".jar", StringComparison.Ordinal))
            throw new InvalidDataException("Неверный файл Mechanica Server Sync.");
        string bundled = Path.Combine(packageDirectory, package.FileName);
        if (!File.Exists(bundled)) throw new FileNotFoundException("Mechanica Server Sync отсутствует в лаунчере.", bundled);
        string desired = Convert.ToHexString(SHA512.HashData(await File.ReadAllBytesAsync(bundled, token)));
        if (!desired.Equals(package.Sha512, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Файл Mechanica Server Sync повреждён.");
        if (File.Exists(disabled))
        {
            if (File.Exists(target) || ownedHash == null || await Hash(disabled, token) != ownedHash)
                throw new InvalidOperationException("Файл отключённого Mechanica Server Sync изменён.");
            if (Events.Active?.Ui?.AllowModInstall == false) throw new InvalidOperationException(Locale.Get("catalog.install_locked"));
            token.ThrowIfCancellationRequested();
            File.Move(disabled, target);
        }
        if (File.Exists(target))
        {
            string installed = Convert.ToHexString(SHA512.HashData(await File.ReadAllBytesAsync(target, token)));
            if (installed == desired)
            {
                if (ownedHash != desired && Events.Active?.Ui?.AllowModInstall != false)
                    await WriteBridgeFileAsync(receipt, System.Text.Encoding.UTF8.GetBytes(desired), token);
                return;
            }
            if (ownedHash != installed)
                throw new InvalidOperationException("Файл клиентского моста изменён: " + BridgeFileName);
        }
        if (Events.Active?.Ui?.AllowModInstall == false) throw new InvalidOperationException(Locale.Get("catalog.install_locked"));
        await WriteBridgeFileAsync(target, await File.ReadAllBytesAsync(bundled, token), token);
        await WriteBridgeFileAsync(receipt, System.Text.Encoding.UTF8.GetBytes(desired), token);
    }

    private sealed record BridgePackage(string Minecraft, string Loader, string MinimumLoaderVersion, string FileName, string Sha512);

    private static async Task WriteBridgeFileAsync(string path, byte[] bytes, CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temp, bytes, token);
            token.ThrowIfCancellationRequested();
            File.Move(temp, path, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
