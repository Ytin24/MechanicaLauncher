using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MechanicaLauncher.Core.Instances;
using MechanicaLauncher.Core.Models;
using MechanicaLauncher.Core.Profiles;
using MechanicaLauncher.Core.Servers;
using MechanicaLauncher.Desktop;

internal static partial class Program
{
    private static void GameBridgeLifecycleChecks(Pump context)
    {
        string? previousData = Environment.GetEnvironmentVariable("MECHANICA_DATA_DIR");
        string data = Path.Combine(output, "bridge-lifecycle-data");
        Environment.SetEnvironmentVariable("MECHANICA_DATA_DIR", data);
        void Until(Func<bool> completed, string operation)
        {
            var timer = Stopwatch.StartNew();
            while (!completed())
            {
                if (timer.Elapsed.TotalSeconds > 15) throw new TimeoutException("Bridge lifecycle: " + operation);
                context.Drain(); Thread.Sleep(1);
            }
            context.Drain();
        }
        void Await(Task task, string operation)
        {
            Until(() => task.IsCompleted, operation);
            task.GetAwaiter().GetResult();
        }
        try
        {
            using var bundle = new BridgeBundleFixture();
            using var unlisted = new ServerDiscoveryPublisher { Advertisement = null };
            var fabric = bundle.Add("1.21.1", LoaderType.Fabric, "0.19.3", "1.0.0");
            var stubPackage = bundle.Add("window-fixture", LoaderType.Fabric, "0.19.3", "1.0.0");
            bundle.Manifest(fabric, stubPackage);
            var instances = new InstanceManager(data);
            foreach (bool ignoreCancellation in new[] { false, true })
            {
                var instance = instances.CreateInstance("Отмена загрузки " + ignoreCancellation, "1.21.1", LoaderType.Fabric, "0.19.3");
                instance.UseServerModSync = true; instances.SaveInstance(instance);
                string gameDir = instances.GetGameDir(instance.Id);
                string installed = Path.Combine(gameDir, "mods", "bridge-lifecycle.jar");
                var server = new FavoriteServer(Guid.NewGuid().ToString("N"), "Lifecycle", "play.example", 25565, instance.Id)
                {
                    SyncManifestUrl = "https://bridge-lifecycle.example/descriptor.json", AutoSync = true
                };
                new FavoriteServers(data).Save([server]);
                using var sessions = new GameSessions(new LauncherSettings { Username = "BridgeLifecycle", AuthMode = "offline", DiscordRpc = false }, instances);
                using var handler = new BridgeLifecycleHttp(ignoreCancellation);
                using var http = new HttpClient(handler);
                var sync = new ServerModSync(http, approvedExternalOrigins: [new Uri("https://bridge-lifecycle.example")]);
                var planTask = sync.PlanAsync(new Uri(server.SyncManifestUrl), instance, gameDir);
                Await(planTask, "building a validated plan");
                var plan = planTask.Result;
                Check(plan.Conflicts.Count == 0 && plan.HasChanges, "bridge cancellation uses a real validated download plan");

                object run = NewBridgeLifecycleRun(sessions, instance);
                using var pipe = BridgeLifecycleProperty<GameBridgeSession>(run, "Session");
                using var operation = new CancellationTokenSource();
                SetBridgeLifecycleProperty(run, "Server", server);
                SetBridgeLifecycleProperty(run, "Sync", sync);
                SetBridgeLifecycleProperty(run, "Plan", plan);
                SetBridgeLifecycleProperty(run, "Operation", operation);
                var route = BridgeLifecycleRoute(pipe);
                var apply = route(BridgeLifecycleRequest("Apply", new { planId = plan.PlanId }), CancellationToken.None);
                try
                {
                    Until(() => handler.FileRequested.Task.IsCompleted, "waiting for the gated mod response");
                    Check(!apply.IsCompleted && sessions.IsBusy(instance.Id) && !File.Exists(installed),
                        "an active bridge download reserves the instance without installing into the game");
                    var cancel = route(BridgeLifecycleRequest("Cancel", new { }), CancellationToken.None);
                    Await(cancel, "cancelling while the response is pending");
                    Check(BridgeLifecycleStatus(cancel.Result) == "cancelled" && operation.IsCancellationRequested,
                        "parallel Cancel acknowledges and cancels the active operation");
                    handler.Release.TrySetResult();
                    Await(apply, "finishing a cancelled download");
                    Check(BridgeLifecycleStatus(apply.Result) == "cancelled", "a cancelled Apply never asks Minecraft to restart");
                    Check(BridgeLifecycleProperty<object?>(run, "Stage") == null && !BridgeLifecycleProperty<bool>(run, "RestartRequested"),
                        "late download completion cannot publish a cancelled stage or restart");
                    Check(!sessions.IsBusy(instance.Id) && !File.Exists(installed), "cancel releases the instance and leaves its mod directory unchanged");
                    for (int retry = 0; retry < 2; retry++)
                    {
                        var prepare = route(BridgeLifecycleRequest("PrepareConnection", new { host = "127.0.0.1", port = unlisted.GamePort, knownRevision = 0 }), CancellationToken.None);
                        Await(prepare, "preparing again after cancellation");
                        Check(BridgeLifecycleStatus(prepare.Result) == "ready", "cancelled bridge sessions accept subsequent connection attempts");
                    }
                    Check(handler.FileRequests == 1, "cancel and retry do not start a second download");

                    var nextPlan = sync.PlanAsync(new Uri(server.SyncManifestUrl), instance, gameDir);
                    Await(nextPlan, "building a new plan after cancellation");
                    SetBridgeLifecycleProperty(run, "Server", server);
                    SetBridgeLifecycleProperty(run, "Sync", sync);
                    SetBridgeLifecycleProperty(run, "Plan", nextPlan.Result);
                    var successful = route(BridgeLifecycleRequest("Apply", new { planId = nextPlan.Result.PlanId }), CancellationToken.None);
                    Await(successful, "staging a subsequent successful attempt");
                    Check(BridgeLifecycleStatus(successful.Result) == "restart_required" && BridgeLifecycleProperty<object?>(run, "Stage") != null,
                        "a new approved attempt can stage successfully after cancellation");
                    Check(!File.Exists(installed) && sessions.IsBusy(instance.Id), "completed staging waits for game exit before installing files");
                    var cancelledRestart = route(BridgeLifecycleRequest("Cancel", new { planId = nextPlan.Result.PlanId }), CancellationToken.None);
                    Await(cancelledRestart, "cancelling the pending restart");
                    Check(BridgeLifecycleStatus(cancelledRestart.Result) == "cancelled" && !BridgeLifecycleProperty<bool>(run, "RestartRequested") &&
                        BridgeLifecycleProperty<object?>(run, "Stage") == null && !sessions.IsBusy(instance.Id) && !File.Exists(installed),
                        "Cancel after staging removes pending restart without applying the staged mod");
                }
                finally
                {
                    handler.Release.TrySetResult();
                    sessions.Downloads.CancelAll();
                    Await(apply, "draining the owned cancelled operation");
                }
            }

            using (var sessions = new GameSessions(new LauncherSettings { DiscordRpc = false }, instances))
            {
                foreach (string loader in new[] { "0.16.9", "0.19.2" })
                {
                    var instance = instances.CreateInstance("Старый Fabric " + loader, "1.21.1", LoaderType.Fabric, loader);
                    instance.UseServerModSync = true; instances.SaveInstance(instance);
                    new FavoriteServers(data).Save([new FavoriteServer(Guid.NewGuid().ToString("N"), "Old loader", "play.example", 25565, instance.Id)
                    {
                        SyncManifestUrl = "https://bridge-lifecycle.example/descriptor.json"
                    }]);
                    string gameDir = instances.GetGameDir(instance.Id);
                    var install = (Task)BridgeLifecycleMethod("InstallBundledBridgeAsync").Invoke(sessions, [instance, gameDir, CancellationToken.None])!;
                    Until(() => install.IsCompleted, "rejecting an unsupported Fabric loader");
                    Exception? error = null;
                    try { install.GetAwaiter().GetResult(); } catch (Exception ex) { error = ex; }
                    Check(error is InvalidOperationException && error.Message.Contains("0.19.3") &&
                        !File.Exists(Path.Combine(gameDir, "mods", "mechanica-server-sync-1.0.0.jar")) &&
                        !File.Exists(Path.Combine(gameDir, ".mechanica", "bridge-sha512.txt")),
                        "opted-in Fabric " + loader + " fails explicitly without installing an incompatible bridge");
                }
            }

            new FavoriteServers(data).Save([]);
            using var model = new LauncherModel(new LauncherSettings { Username = "BridgeLifecycle", AuthMode = "offline", DiscordRpc = false }, instances);
            var game = WindowGame(model);
            var bridges = (ConcurrentDictionary<string, GameBridgeSession>)typeof(GameSessions)
                .GetField("bridges", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(model.Sessions)!;
            var ordinaryServer = new FavoriteServer(Guid.NewGuid().ToString("N"), "Без синхронизации", "127.0.0.1", 25565, game.Id)
            {
                SyncManifestUrl = "http://127.0.0.1:1/descriptor.json", AutoSync = true, AllowLocalSync = true
            };
            int ordinaryCompleted = 0;
            bool ordinaryStarted = false;
            Action ordinaryExited = () => Interlocked.Increment(ref ordinaryCompleted);
            model.Sessions.GameExited += ordinaryExited;
            try
            {
                Await(model.Sessions.LaunchServerAsync(ordinaryServer, _ => Task.FromResult(true)), "connecting with synchronization disabled");
                ordinaryStarted = true;
                Check(!game.UseServerModSync && model.Sessions.IsRunning(game.Id) && bridges.IsEmpty &&
                    !File.Exists(Path.Combine(instances.GetGameDir(game.Id), "mods", "mechanica-server-sync-1.0.0.jar")),
                    "default opt-out starts the game without querying the unavailable mod source or creating a bridge session");
            }
            finally
            {
                try
                {
                    if (model.Sessions.IsRunning(game.Id)) model.Sessions.Stop(game.Id);
                    Until(() => !model.Sessions.IsBusy(game.Id) && (!ordinaryStarted || Volatile.Read(ref ordinaryCompleted) > 0), "draining the ordinary owned stub process");
                }
                finally { model.Sessions.GameExited -= ordinaryExited; }
            }
            game.Loader = LoaderType.Fabric; game.LoaderVersion = "0.19.3"; game.UseServerModSync = true;
            game.JvmArgs = "";
            instances.SaveInstance(game);
            string moddedId = game.GetEffectiveVersionId();
            string moddedDirectory = Path.Combine(instances.GetGameDir(game.Id), "versions", moddedId);
            Directory.CreateDirectory(moddedDirectory);
            File.WriteAllText(Path.Combine(moddedDirectory, moddedId + ".json"), JsonSerializer.Serialize(new VersionMeta
            {
                Id = moddedId, InheritsFrom = game.McVersion, MainClass = "MechanicaFixture", MinecraftArguments = ""
            }));
            File.WriteAllText(Path.Combine(moddedDirectory, ".complete"), "");
            using var firstExited = new ManualResetEventSlim();
            using var releaseFirstExit = new ManualResetEventSlim();
            var callbackErrors = new ConcurrentQueue<string>();
            int launches = 0, completed = 0;
            model.Sessions.ProcessStarted += (_, _) => Interlocked.Increment(ref launches);
            model.Sessions.ProcessExited += (_, _, _) =>
            {
                if (Volatile.Read(ref launches) != 1) return;
                firstExited.Set();
                if (!releaseFirstExit.Wait(TimeSpan.FromSeconds(15))) callbackErrors.Enqueue("First exit cleanup was not released.");
            };
            model.Sessions.GameExited += () => Interlocked.Increment(ref completed);
            try
            {
                Await(model.Sessions.LaunchAsync(game, null, null, _ => Task.FromResult(true)), "starting the first stub JVM");
                Until(() => firstExited.IsSet, "holding the old exit callback before final bridge cleanup");
                game.JvmArgs = "--fixture-wait";
                instances.SaveInstance(game);
                Await(model.Sessions.LaunchAsync(game, null, null, _ => Task.FromResult(true)), "relaunching while old exit cleanup is pending");
                Check(launches == 2 && model.Sessions.IsRunning(game.Id) && bridges.TryGetValue(game.Id, out _),
                    "the replacement JVM receives its own tracked bridge session");
                var replacement = bridges[game.Id];
                releaseFirstExit.Set();
                Until(() => Volatile.Read(ref completed) >= 1, "finishing the old bridge cleanup");
                Check(model.Sessions.IsRunning(game.Id) && model.Sessions.RunningCount == 1 &&
                    bridges.TryGetValue(game.Id, out var remaining) && ReferenceEquals(remaining, replacement),
                    "old exit cleanup preserves the replacement JVM and its authenticated session");
                Check(callbackErrors.IsEmpty, "stub lifecycle callbacks complete without timeout");
            }
            finally
            {
                releaseFirstExit.Set();
                if (model.Sessions.IsRunning(game.Id)) model.Sessions.Stop(game.Id);
                Until(() => model.Sessions.RunningCount == 0, "draining owned stub processes");
            }
        }
        finally { Environment.SetEnvironmentVariable("MECHANICA_DATA_DIR", previousData); }
    }

    private static MethodInfo BridgeLifecycleMethod(string name) => typeof(GameSessions).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(typeof(GameSessions).Name, name);
    private static object NewBridgeLifecycleRun(GameSessions sessions, GameInstance instance) => BridgeLifecycleMethod("CreateBridgeRun").Invoke(sessions, [instance, false])!;
    private static T BridgeLifecycleProperty<T>(object run, string name) => (T)run.GetType().GetProperty(name)!.GetValue(run)!;
    private static void SetBridgeLifecycleProperty(object run, string name, object value) => run.GetType().GetProperty(name)!.SetValue(run, value);
    private static Func<BridgeRequest, CancellationToken, Task<BridgeReply>> BridgeLifecycleRoute(GameBridgeSession session) =>
        (Func<BridgeRequest, CancellationToken, Task<BridgeReply>>)typeof(GameBridgeSession).GetField("handle", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session)!;
    private static BridgeRequest BridgeLifecycleRequest(string type, object payload) => new(type, JsonSerializer.SerializeToElement(payload), Guid.NewGuid());
    private static string? BridgeLifecycleStatus(BridgeReply reply) => reply.Payload.GetProperty("status").GetString();

    private sealed class BridgeLifecycleHttp : HttpMessageHandler
    {
        private readonly byte[] descriptor, manifest, jar;
        private readonly bool ignoreCancellation;
        public TaskCompletionSource FileRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int FileRequests;

        public BridgeLifecycleHttp(bool ignoreCancellation)
        {
            this.ignoreCancellation = ignoreCancellation;
            using (var bytes = new MemoryStream())
            {
                using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, leaveOpen: true))
                using (var writer = new StreamWriter(zip.CreateEntry("fabric.mod.json").Open(), new UTF8Encoding(false)))
                    writer.Write("{\"schemaVersion\":1,\"id\":\"bridge_lifecycle\",\"version\":\"1.0.0\",\"environment\":\"*\",\"depends\":{\"minecraft\":\"1.21.1\",\"fabricloader\":\">=0.19.3\"}}");
                jar = bytes.ToArray();
            }
            var serverId = Guid.NewGuid();
            manifest = JsonSerializer.SerializeToUtf8Bytes(new
            {
                type = "Manifest", protocolMajor = 1, protocolMinor = 0, serverId, revision = 1, expiresUtc = DateTime.UtcNow.AddHours(1),
                targetId = "fabric-1.21.1", minecraft = "1.21.1", loader = "fabric", loaderVersion = "0.19.3",
                files = new[] { new { artifactId = "bridge-lifecycle", modIds = new[] { "bridge_lifecycle" }, filename = "bridge-lifecycle.jar", client = "required",
                    size = jar.Length, sha512 = Convert.ToHexString(SHA512.HashData(jar)).ToLowerInvariant(), dependencies = Array.Empty<string>(),
                    source = new { type = "external", url = "https://bridge-lifecycle.example/bridge-lifecycle.jar" } } }
            });
            descriptor = JsonSerializer.SerializeToUtf8Bytes(new
            {
                descriptorVersion = 1, serverId, protocols = new[] { new { major = 1, minor = 0, requiredCapabilities = new[] { "mods-v1" },
                    targets = new[] { new { targetId = "fabric-1.21.1", minecraft = "1.21.1", loader = "fabric", loaderVersion = "0.19.3",
                        manifestUrl = "https://bridge-lifecycle.example/manifest.json", sha512 = Convert.ToHexString(SHA512.HashData(manifest)).ToLowerInvariant() } } } }
            });
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.Host != "bridge-lifecycle.example") throw new InvalidOperationException("Unexpected lifecycle source.");
            byte[] bytes;
            switch (request.RequestUri.AbsolutePath)
            {
                case "/descriptor.json": bytes = descriptor; break;
                case "/manifest.json": bytes = manifest; break;
                case "/bridge-lifecycle.jar":
                    Interlocked.Increment(ref FileRequests);
                    FileRequested.TrySetResult();
                    await Release.Task.WaitAsync(ignoreCancellation ? CancellationToken.None : cancellationToken);
                    bytes = jar;
                    break;
                default: throw new InvalidOperationException("Unexpected lifecycle endpoint: " + request.RequestUri);
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        }
    }
}
