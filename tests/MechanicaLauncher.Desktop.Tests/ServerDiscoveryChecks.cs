using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MechanicaLauncher.Core.Instances;
using MechanicaLauncher.Core.Profiles;
using MechanicaLauncher.Core.Servers;
using MechanicaLauncher.Desktop;

internal static partial class Program
{
    private static void ServerDiscoveryChecks(Pump context)
    {
        string? previousData = Environment.GetEnvironmentVariable("MECHANICA_DATA_DIR");
        string data = Path.Combine(output, "server-discovery-data");
        Environment.SetEnvironmentVariable("MECHANICA_DATA_DIR", data);
        try
        {
            using var publisher = new ServerDiscoveryPublisher();
            var instances = new InstanceManager(data);
            var instance = instances.CreateInstance("Обнаружение сервера", "1.21.1", LoaderType.Fabric, "0.19.3");
            instance.UseServerModSync = true;
            instances.SaveInstance(instance);
            string gameDir = instances.GetGameDir(instance.Id);
            string installed = Path.Combine(gameDir, "mods", ServerDiscoveryPublisher.FileName);
            string sentinel = Path.Combine(gameDir, "config", "personal-discovery.txt");
            File.WriteAllText(sentinel, "personal configuration");
            var favorites = new FavoriteServers(data);
            var approvals = new ConcurrentQueue<ServerDiscoveryApproval>();
            TaskCompletionSource<bool>? decision = null;
            using var sessions = new GameSessions(new LauncherSettings { Username = "Discovery", AuthMode = "offline", DiscordRpc = false }, instances);
            sessions.ConfirmServerSync = (server, plan, token) =>
            {
                approvals.Enqueue(new(server, plan, token));
                return decision?.Task.WaitAsync(token) ?? Task.FromException<bool>(new InvalidOperationException("Unexpected discovery confirmation."));
            };
            void Until(Func<bool> completed, string operation)
            {
                var timer = Stopwatch.StartNew();
                while (!completed())
                {
                    if (!publisher.Errors.IsEmpty) throw new InvalidOperationException(string.Join("\n", publisher.Errors));
                    if (timer.Elapsed.TotalSeconds > 15) throw new TimeoutException("Server discovery: " + operation);
                    context.Drain(); Thread.Sleep(1);
                }
                context.Drain();
            }
            BridgeReply Await(Task<BridgeReply> task, string operation)
            {
                Until(() => task.IsCompleted, operation);
                return task.GetAwaiter().GetResult();
            }
            void AwaitApproval(Task<BridgeReply> prepare, int count)
            {
                Until(() => approvals.Count == count || prepare.IsCompleted, "waiting for user consent");
                if (prepare.IsCompleted)
                    throw new InvalidOperationException("Discovery completed before consent: " + prepare.GetAwaiter().GetResult().Payload);
            }
            void NoFavorites(string operation) => Check(favorites.Load().Count == 0 && !File.Exists(Path.Combine(data, "servers.json")),
                operation + " does not create a favorite or silently persist trust");
            object run = NewBridgeLifecycleRun(sessions, instance);
            using var pipe = BridgeLifecycleProperty<GameBridgeSession>(run, "Session");
            var route = BridgeLifecycleRoute(pipe);
            Task<BridgeReply> Prepare(string host = "127.0.0.1") => route(BridgeLifecycleRequest("PrepareConnection",
                new { host, port = publisher.GamePort, knownRevision = 0 }), CancellationToken.None);
            BridgeReply Cancel() => Await(route(BridgeLifecycleRequest("Cancel", new { }), CancellationToken.None), "cancelling the owned attempt");
            try
            {
                decision = new(TaskCreationOptions.RunContinuationsAsynchronously);
                var prepare = Prepare();
                AwaitApproval(prepare, 1);
                var approval = approvals.Last();
                Check(approval.Server.Host == "127.0.0.1" && approval.Server.Port == publisher.GamePort && approval.Server.InstanceId == instance.Id &&
                    approval.Server.SyncManifestUrl == publisher.DescriptorUrl, "an unknown game endpoint discovers its advertised mod descriptor");
                Check(!approval.Server.AutoSync && approval.Server.AllowLocalSync,
                    "a literal loopback advertisement requires explicit consent and grants only local transport");
                Check(approval.Plan.Conflicts.Count == 0 && approval.Plan.Changes.Count == 1 &&
                    approval.Plan.Changes[0].FileName == ServerDiscoveryPublisher.FileName && approval.Plan.DownloadBytes == publisher.Jar.Length,
                    "discovery presents the validated missing-mod plan before downloading");
                Check(publisher.StatusRequests == 1 && publisher.HandshakeHosts.Single() == "127.0.0.1" && publisher.FileRequests == 0 &&
                    !File.Exists(installed) && !sessions.IsBusy(instance.Id), "status targets the actual game endpoint and consent leaves files untouched");
                NoFavorites("Discovery before consent");
                decision.SetResult(true);
                var planned = Await(prepare, "approving the discovered server");
                Check(planned.Type == "Plan" && planned.Payload.GetProperty("planId").GetGuid() == approval.Plan.PlanId &&
                    planned.Payload.GetProperty("restartRequired").GetBoolean(), "approved discovery publishes the exact plan through the game bridge");
                NoFavorites("Approved discovery");
                var applied = Await(route(BridgeLifecycleRequest("Apply", new { planId = approval.Plan.PlanId }), CancellationToken.None), "staging the discovered mod");
                Check(BridgeLifecycleStatus(applied) == "restart_required" && publisher.FileRequests == 1 && sessions.IsBusy(instance.Id),
                    "approved discovered content is downloaded once and requests a controlled restart");
                var stage = BridgeLifecycleProperty<ServerSyncStage>(run, "Stage");
                Check(stage.Files.Count == 1 && ServerSmokeHash(stage.Files.Values.Single()) == publisher.JarHash && !File.Exists(installed),
                    "the verified discovered JAR stays staged until the running game exits");
                Check(BridgeLifecycleStatus(Cancel()) == "cancelled" && !sessions.IsBusy(instance.Id) &&
                    BridgeLifecycleProperty<object?>(run, "Stage") == null && !BridgeLifecycleProperty<bool>(run, "RestartRequested"),
                    "cancelling the discovered restart clears its pending installation");
                NoFavorites("Staged discovery");

                decision = new(TaskCreationOptions.RunContinuationsAsynchronously);
                prepare = Prepare();
                AwaitApproval(prepare, 2);
                Check(!approvals.Last().Server.AutoSync, "rediscovery never inherits the previous one-time consent");
                decision.SetResult(false);
                Check(BridgeLifecycleStatus(Await(prepare, "declining discovery")) == "cancelled" && publisher.FileRequests == 1 && !File.Exists(installed),
                    "declining discovery prevents download and installation");
                NoFavorites("Declined discovery");

                decision = new(TaskCreationOptions.RunContinuationsAsynchronously);
                prepare = Prepare();
                AwaitApproval(prepare, 3);
                approval = approvals.Last();
                Check(BridgeLifecycleStatus(Cancel()) == "cancelled", "bridge Cancel acknowledges a pending discovery confirmation");
                Check(BridgeLifecycleStatus(Await(prepare, "cancelling pending discovery consent")) == "cancelled" && approval.Token.IsCancellationRequested,
                    "pending discovery consent receives cancellation and cannot publish a plan");
                decision.TrySetResult(true);
                Check(BridgeLifecycleProperty<object?>(run, "Plan") == null && publisher.FileRequests == 1 && !File.Exists(installed),
                    "a late approval cannot revive a cancelled discovery attempt");
                NoFavorites("Cancelled discovery");

                decision = null;
                using (var stalled = new ServerDiscoveryPublisher(holdStatus: true))
                {
                    prepare = route(BridgeLifecycleRequest("PrepareConnection", new { host = "127.0.0.1", port = stalled.GamePort, knownRevision = 0 }),
                        CancellationToken.None);
                    Until(() => stalled.StatusRequests == 1 || prepare.IsCompleted, "holding the server status response");
                    Check(!prepare.IsCompleted && stalled.StatusRequests == 1, "discovery can be cancelled while the game status response is pending");
                    Check(BridgeLifecycleStatus(Cancel()) == "cancelled" &&
                        BridgeLifecycleStatus(Await(prepare, "cancelling status discovery")) == "cancelled",
                        "cancellation interrupts status discovery instead of falling back to an ordinary connection");
                    Check(stalled.HttpRequests == 0 && approvals.Count == 3 && !sessions.IsBusy(instance.Id),
                        "cancelled status discovery opens no confirmation and makes no HTTP request");
                }
                int httpBefore = publisher.HttpRequests;
                publisher.Advertisement = null;
                Check(BridgeLifecycleStatus(Await(Prepare(), "connecting without advertisement")) == "ready",
                    "a server without a sync advertisement connects normally");
                Check(publisher.HttpRequests == httpBefore && approvals.Count == 3, "absence of advertisement makes no HTTP request and opens no confirmation");
                publisher.DropStatus = true;
                Check(BridgeLifecycleStatus(Await(Prepare(), "connecting when status is unavailable")) == "ready",
                    "an unavailable status response preserves ordinary connection");
                Check(publisher.HttpRequests == httpBefore && approvals.Count == 3, "unavailable status never attempts a mod download");
                publisher.DropStatus = false;
                foreach (var advertisement in new object[]
                {
                    new { protocol = 2, descriptorUrl = publisher.DescriptorUrl },
                    new { protocol = 1, descriptorUrl = "http://example.invalid/descriptor.json" },
                    new { protocol = 1, descriptorUrl = "http://user@127.0.0.1/descriptor.json" }
                })
                {
                    publisher.Advertisement = advertisement;
                    var invalid = Await(Prepare(), "rejecting invalid discovery data");
                    Check(invalid.Type == "Result" && BridgeLifecycleStatus(invalid) == "error" &&
                        invalid.Payload.TryGetProperty("message", out var message) && !string.IsNullOrWhiteSpace(message.GetString()),
                        "invalid advertised protocol or transport reports an error instead of connecting silently");
                    Check(publisher.HttpRequests == httpBefore && approvals.Count == 3 && !sessions.IsBusy(instance.Id),
                        "invalid discovery never contacts the advertised source or changes game files");
                }
                NoFavorites("Ordinary and invalid discovery");

                publisher.Advertisement = new { protocol = 1, descriptorUrl = publisher.DescriptorUrl };
                var saved = new FavoriteServer("saved-discovery-test", "Явная настройка", "localhost", publisher.GamePort, instance.Id)
                {
                    SyncManifestUrl = publisher.DescriptorUrl, AutoSync = true, AllowLocalSync = true
                };
                favorites.Save([saved]);
                int statusBefore = publisher.StatusRequests;
                planned = Await(Prepare("LOCALHOST."), "matching a saved DNS name with a trailing dot");
                Check(planned.Type == "Plan" && publisher.StatusRequests == statusBefore && approvals.Count == 3,
                    "saved host matching ignores case and a trailing DNS dot without falling back to discovery");
                Check(BridgeLifecycleProperty<FavoriteServer>(run, "Server").Id == saved.Id && favorites.Load().Single() == saved,
                    "explicit saved mapping retains its own trust and is not replaced by an ephemeral server");
                Cancel();
                Check(!File.Exists(installed) && File.ReadAllText(sentinel) == "personal configuration" && publisher.Errors.IsEmpty,
                    "all discovery paths preserve personal files and complete the owned network fixture cleanly");
            }
            finally
            {
                decision?.TrySetResult(false);
                Cancel();
                sessions.Downloads.CancelAll();
                BridgeLifecycleProperty<CancellationTokenSource?>(run, "Operation")?.Dispose();
            }
        }
        finally { Environment.SetEnvironmentVariable("MECHANICA_DATA_DIR", previousData); }
    }

    private sealed record ServerDiscoveryApproval(FavoriteServer Server, ServerSyncPlan Plan, CancellationToken Token);

    private sealed class ServerDiscoveryPublisher : IDisposable
    {
        public const string FileName = "discovery-fixture.jar";
        private readonly TcpListener game = new(IPAddress.Loopback, 0), http = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource lifetime = new();
        private readonly Task gameLoop, httpLoop;
        private readonly byte[] descriptor, manifest;
        private readonly bool holdStatus;
        private object? advertisement;
        private int statusRequests, httpRequests, fileRequests;
        public bool DropStatus;
        public int GamePort { get; }
        public string DescriptorUrl { get; }
        public byte[] Jar { get; }
        public string JarHash { get; }
        public int StatusRequests => Volatile.Read(ref statusRequests);
        public int HttpRequests => Volatile.Read(ref httpRequests);
        public int FileRequests => Volatile.Read(ref fileRequests);
        public object? Advertisement { get => Volatile.Read(ref advertisement); set => Volatile.Write(ref advertisement, value); }
        public ConcurrentQueue<string> HandshakeHosts { get; } = new();
        public ConcurrentQueue<string> Errors { get; } = new();

        public ServerDiscoveryPublisher(bool holdStatus = false)
        {
            this.holdStatus = holdStatus;
            game.Start(); http.Start();
            GamePort = ((IPEndPoint)game.LocalEndpoint).Port;
            string origin = "http://127.0.0.1:" + ((IPEndPoint)http.LocalEndpoint).Port;
            DescriptorUrl = origin + "/descriptor.json";
            using (var bytes = new MemoryStream())
            {
                using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, true))
                using (var writer = new StreamWriter(zip.CreateEntry("fabric.mod.json").Open(), new UTF8Encoding(false)))
                    writer.Write("{\"schemaVersion\":1,\"id\":\"discovery_fixture\",\"version\":\"1.0.0\",\"environment\":\"*\",\"depends\":{\"minecraft\":\"1.21.1\",\"fabricloader\":\">=0.19.3\"}}");
                Jar = bytes.ToArray();
            }
            JarHash = Convert.ToHexString(SHA512.HashData(Jar)).ToLowerInvariant();
            var serverId = Guid.NewGuid();
            manifest = JsonSerializer.SerializeToUtf8Bytes(new
            {
                type = "Manifest", protocolMajor = 1, protocolMinor = 0, serverId, revision = 1, expiresUtc = DateTime.UtcNow.AddHours(1),
                targetId = "fabric-1.21.1", minecraft = "1.21.1", loader = "fabric", loaderVersion = "0.19.3",
                files = new[] { new { artifactId = "discovery-fixture", modIds = new[] { "discovery_fixture" }, filename = FileName, client = "required",
                    size = Jar.Length, sha512 = JarHash, dependencies = Array.Empty<string>(), source = new { type = "external", url = origin + "/" + FileName } } }
            });
            descriptor = JsonSerializer.SerializeToUtf8Bytes(new
            {
                descriptorVersion = 1, serverId, protocols = new[] { new { major = 1, minor = 0, requiredCapabilities = new[] { "mods-v1" },
                    targets = new[] { new { targetId = "fabric-1.21.1", minecraft = "1.21.1", loader = "fabric", loaderVersion = "0.19.3",
                        manifestUrl = origin + "/manifest.json", sha512 = Convert.ToHexString(SHA512.HashData(manifest)).ToLowerInvariant() } } } }
            });
            Advertisement = new { protocol = 1, descriptorUrl = DescriptorUrl };
            gameLoop = Task.Run(() => Listen(game, true));
            httpLoop = Task.Run(() => Listen(http, false));
        }

        private async Task Listen(TcpListener listener, bool status)
        {
            while (!lifetime.IsCancellationRequested)
            {
                try
                {
                    using var client = await listener.AcceptTcpClientAsync(lifetime.Token);
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(10));
                    if (status) await ServeStatus(client.GetStream(), timeout.Token);
                    else await ServeHttp(client.GetStream(), timeout.Token);
                }
                catch (Exception ex) when (lifetime.IsCancellationRequested && ex is OperationCanceledException or SocketException or ObjectDisposedException or IOException) { return; }
                catch (Exception ex) { Errors.Enqueue(ex.ToString()); }
            }
        }

        private async Task ServeStatus(Stream stream, CancellationToken token)
        {
            using var handshake = new MemoryStream(await ReadPacket(stream, token));
            if (await ReadInt(handshake, token) != 0) throw new InvalidDataException("Expected a status handshake.");
            _ = await ReadInt(handshake, token);
            int length = await ReadInt(handshake, token);
            if (length is < 1 or > 253) throw new InvalidDataException("Invalid handshake host length.");
            var host = new byte[length];
            await handshake.ReadExactlyAsync(host, token);
            HandshakeHosts.Enqueue(Encoding.UTF8.GetString(host));
            int port = (handshake.ReadByte() << 8) | handshake.ReadByte();
            if (port != GamePort || await ReadInt(handshake, token) != 1 || handshake.Position != handshake.Length)
                throw new InvalidDataException("Status did not target the requested game endpoint.");
            if (!(await ReadPacket(stream, token)).SequenceEqual(new byte[] { 0 })) throw new InvalidDataException("Expected a status request.");
            Interlocked.Increment(ref statusRequests);
            if (holdStatus) await Task.Delay(Timeout.InfiniteTimeSpan, token);
            if (Volatile.Read(ref DropStatus)) return;
            var payload = new Dictionary<string, object>
            {
                ["version"] = new { name = "1.21.1", protocol = 767 },
                ["players"] = new { online = 0, max = 4 }, ["description"] = new { text = "Discovery fixture" }
            };
            if (Advertisement is { } sync) payload["mechanica"] = sync;
            byte[] json = JsonSerializer.SerializeToUtf8Bytes(payload);
            using var response = new MemoryStream();
            WriteInt(response, 0); WriteInt(response, json.Length); response.Write(json);
            await WritePacket(stream, response.ToArray(), token);
            byte[] ping;
            try { ping = await ReadPacket(stream, token); }
            catch (EndOfStreamException) { return; }
            catch (IOException ex) when (ex.InnerException is SocketException) { return; }
            if (ping.Length != 9 || ping[0] != 1) throw new InvalidDataException("Expected the status ping.");
            await WritePacket(stream, ping, token);
        }

        private async Task ServeHttp(Stream stream, CancellationToken token)
        {
            using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true);
            string request = await reader.ReadLineAsync(token) ?? throw new EndOfStreamException();
            int headerBytes = request.Length;
            for (;;)
            {
                string line = await reader.ReadLineAsync(token) ?? throw new EndOfStreamException();
                if (line.Length == 0) break;
                headerBytes += line.Length;
                if (headerBytes > 8192) throw new InvalidDataException("Fixture HTTP headers exceed the limit.");
            }
            string[] parts = request.Split(' ');
            if (parts.Length != 3 || parts[0] != "GET") throw new InvalidDataException("Unexpected fixture HTTP method.");
            Interlocked.Increment(ref httpRequests);
            byte[] bytes;
            switch (parts[1])
            {
                case "/descriptor.json": bytes = descriptor; break;
                case "/manifest.json": bytes = manifest; break;
                case "/" + FileName: Interlocked.Increment(ref fileRequests); bytes = Jar; break;
                default: throw new InvalidDataException("Unexpected fixture HTTP path: " + parts[1]);
            }
            var headers = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: " + bytes.Length +
                "\r\nContent-Type: " + (parts[1].EndsWith(".json", StringComparison.Ordinal) ? "application/json" : "application/java-archive") + "\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(headers, token);
            await stream.WriteAsync(bytes, token);
        }

        private static async Task<byte[]> ReadPacket(Stream stream, CancellationToken token)
        {
            int length = await ReadInt(stream, token);
            if (length is < 1 or > 262144) throw new InvalidDataException("Invalid fixture packet size.");
            var packet = new byte[length];
            await stream.ReadExactlyAsync(packet, token);
            return packet;
        }
        private static async Task WritePacket(Stream stream, byte[] packet, CancellationToken token)
        {
            using var bytes = new MemoryStream();
            WriteInt(bytes, packet.Length); bytes.Write(packet);
            await stream.WriteAsync(bytes.ToArray(), token);
        }
        private static async Task<int> ReadInt(Stream stream, CancellationToken token)
        {
            int value = 0;
            var one = new byte[1];
            for (int index = 0; index < 5; index++)
            {
                await stream.ReadExactlyAsync(one, token);
                if (index == 4 && (one[0] & 0xf0) != 0) throw new InvalidDataException("Invalid fixture VarInt.");
                value |= (one[0] & 0x7f) << (index * 7);
                if ((one[0] & 0x80) == 0) return value;
            }
            throw new InvalidDataException("Invalid fixture VarInt.");
        }
        private static void WriteInt(Stream stream, int value)
        {
            uint remaining = (uint)value;
            do
            {
                byte next = (byte)(remaining & 0x7f);
                remaining >>= 7;
                stream.WriteByte(remaining > 0 ? (byte)(next | 0x80) : next);
            } while (remaining > 0);
        }
        public void Dispose()
        {
            lifetime.Cancel(); game.Stop(); http.Stop();
            if (!Task.WaitAll([gameLoop, httpLoop], TimeSpan.FromSeconds(5)))
                throw new TimeoutException("Owned discovery fixture did not stop.");
            lifetime.Dispose();
        }
    }
}
