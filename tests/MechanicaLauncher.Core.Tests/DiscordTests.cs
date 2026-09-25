using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;
using DiscordRPC;
using DiscordRPC.IO;
using DiscordRPC.Logging;
using MechanicaLauncher.Core.Discord;
using MechanicaLauncher.Core.Instances;
using MechanicaLauncher.Core.Profiles;

internal static class DiscordTests
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> check)
    {
        await check("Discord launch lifecycle clears all instance metadata on exit and cancellation", () =>
        {
            using var fixture = new Fixture();
            var rpc = fixture.Presence;
            Require(rpc.Snapshot.Details == "In the launcher" && rpc.Snapshot.StartedAt == null);
            var pending = rpc.BeginPreparation(Instance("cancelled"));
            Require(rpc.Snapshot.Details == "Preparing an instance" && rpc.Snapshot.StartedAt == null);
            rpc.EndPreparation(pending);
            Require(!rpc.Snapshot.State.Contains("cancelled"));
            var running = rpc.BeginPreparation(Instance("Adventure"));
            rpc.GameStarted(running, 12);
            Require(rpc.Snapshot.State.Contains("Adventure") && rpc.Snapshot.State.Contains("12 mods"));
            var started = rpc.Snapshot.StartedAt;
            fixture.Time.Advance(40);
            rpc.GameStarted(running, 99);
            Require(rpc.Snapshot.StartedAt == started && !rpc.Snapshot.State.Contains("99 mods"));
            rpc.EndPreparation(running);
            Require(rpc.Snapshot.StartedAt == started);
            rpc.EndSession(running);
            rpc.ProcessLogLine(running, Client("Connecting to stale.test, 25565"));
            Require(rpc.Snapshot.Details == "In the launcher" && rpc.BuildPresence().Timestamps == null);
            Require(!rpc.Snapshot.State.Contains("Adventure") && rpc.BuildPresence().Assets.SmallImageKey == null);
            return Task.CompletedTask;
        });
        await check("Discord tracks each running game and ignores stale exits and logs", () =>
        {
            using var fixture = new Fixture();
            var rpc = fixture.Presence;
            var first = fixture.Start("First");
            rpc.ProcessLogLine(first, Client("Connecting to first.test, 25565"));
            rpc.ProcessLogLine(first, Client("Loaded 15 advancements"));
            fixture.Time.Advance(20);
            var second = rpc.BeginPreparation(Instance("Second"));
            Require(rpc.Snapshot.State.Contains("First"));
            rpc.GameStarted(second, 5);
            rpc.ProcessLogLine(second, Server("Starting integrated minecraft server version 1.21.1"));
            var secondStart = rpc.Snapshot.StartedAt;
            rpc.EndSession(first);
            Require(rpc.Snapshot.State.Contains("Second") && rpc.Snapshot.StartedAt == secondStart);
            rpc.ProcessLogLine(first, Client("Connecting to stale.test, 25565"));
            Require(!rpc.Snapshot.Details.Contains("stale.test"));
            var third = fixture.Start("Third");
            rpc.EndSession(third);
            Require(rpc.Snapshot.State.Contains("Second") && rpc.Snapshot.StartedAt == secondStart);
            var pending = rpc.BeginPreparation(Instance("Pending"));
            rpc.EndSession(second);
            Require(rpc.Snapshot.State.Contains("Pending") && rpc.Snapshot.StartedAt == null);
            rpc.EndPreparation(pending);
            return Task.CompletedTask;
        });
        await check("Discord games without recognized log events do not stay in launching forever", () =>
        {
            using var fixture = new Fixture();
            var id = fixture.Start("Legacy");
            fixture.Time.Advance(30);
            Require(fixture.Presence.Snapshot.Details == "Minecraft is running");
            fixture.Presence.ProcessLogLine(id, Client("Sound engine started"));
            Require(fixture.Presence.Snapshot.Details == "Main menu");
            return Task.CompletedTask;
        });
        await check("Discord parser handles vanilla and Forge prefixes without treating startup as ready", () =>
        {
            var parser = new McStateMachine();
            parser.ProcessLine(Client("Setting user: Tester"));
            Require(parser.State.Type == McStateType.Starting && parser.State.Player == "Tester");
            parser.ProcessLine("[12:00:00] [Render thread/INFO] [net.minecraft.client.sounds.SoundEngine/]: Sound engine started");
            Require(parser.State.Type == McStateType.Menu);
            parser.ProcessLine(Server("Starting integrated minecraft server version 1.21.1"));
            parser.ProcessLine(Server("Default game type: SURVIVAL"));
            parser.ProcessLine(Server("Preparing level \"My world\""));
            parser.ProcessLine(Server("Preparing start region for dimension minecraft:overworld"));
            Require(parser.State.Type == McStateType.SinglePlayer && parser.State.World == "My world" &&
                    parser.State.Gamemode == null && parser.State.Dimension == null);
            parser.ProcessLine(Client("[CHAT] Set own game mode to Creative Mode"));
            Require(parser.State.Gamemode == "creative");
            parser.ProcessLine(Server("Saving and pausing game..."));
            parser.ProcessLine(Server("ThreadedAnvilChunkStorage: All dimensions are saved"));
            Require(parser.State.Type == McStateType.SinglePlayer);
            parser.ProcessLine(Server("Stopping server"));
            Require(parser.State.Type == McStateType.Menu && parser.State.World == null && parser.State.Dimension == null);
            return Task.CompletedTask;
        });
        await check("Discord multiplayer is not overwritten by other players joining or leaving", () =>
        {
            var parser = new McStateMachine();
            parser.ProcessLine(Client("Connecting to play.example.test, 25566"));
            Require(parser.State.Type == McStateType.Connecting && parser.State.Port == 25566);
            parser.ProcessLine(Client("Loaded 20 advancements"));
            parser.ProcessLine(Client("[CHAT] Someone joined the game"));
            parser.ProcessLine(Server("Someone lost connection: Disconnected"));
            parser.ProcessLine(Client("[CHAT] <Someone> Stopping server"));
            Require(parser.State.Type == McStateType.MultiPlayer && parser.State.Server == "play.example.test");
            parser.ProcessLine(Client("Disconnecting from server"));
            Require(parser.State.Type == McStateType.Menu && parser.State.Server == null);
            parser.ProcessLine(Client("Connecting to failure.test, 25565"));
            parser.ProcessLine("[12:00:00] [Server Connector #1/ERROR]: Couldn't connect to server");
            Require(parser.State.Type == McStateType.Menu);
            return Task.CompletedTask;
        });
        await check("Discord chat cannot spoof connection, dimensions or another player's advancement", () =>
        {
            var parser = new McStateMachine();
            parser.ProcessLine(Client("Setting user: Tester"));
            parser.ProcessLine(Server("Starting integrated minecraft server version 1.21.1"));
            foreach (var message in new[]
            {
                "<Other> Connecting to spoof.test, 25565", "<Other> Preparing level \"Fake\"",
                "<Other> Changing dimension to minecraft:the_end", "<Other> Set own game mode to Creative Mode",
                "Other has made the advancement [Not mine]", "<Other> Tester has made the advancement [Spoof]"
            }) parser.ProcessLine(Client("[CHAT] " + message));
            Require(parser.State.Type == McStateType.SinglePlayer && parser.State.Server == null &&
                    parser.State.Dimension == null && parser.State.Gamemode == null && parser.State.Achievement == null);
            parser.ProcessLine("Connecting to unframed.test, 25565");
            parser.ProcessLine(Client(new string('x', 10000)));
            Require(parser.State.Type == McStateType.SinglePlayer);
            return Task.CompletedTask;
        });
        await check("Discord own advancements expire after 30 seconds and clear between worlds", () =>
        {
            var time = new ManualTime();
            var parser = new McStateMachine(time);
            parser.ProcessLine(Client("Setting user: Tester"));
            parser.ProcessLine(Server("Starting integrated minecraft server version 1.21.1"));
            parser.ProcessLine(Client("[CHAT] Tester has completed the challenge [A balanced diet]"));
            Require(parser.State.Achievement == "A balanced diet");
            time.Advance(29);
            Require(parser.State.Achievement != null);
            time.Advance(1);
            Require(parser.State.Achievement == null);
            parser.ProcessLine(Client("[CHAT] Tester получил достижение [Каменный век]"));
            Require(parser.State.Achievement == "Каменный век");
            parser.ProcessLine(Server("Stopping server"));
            parser.ProcessLine(Server("Starting integrated minecraft server version 1.21.1"));
            Require(parser.State.Achievement == null);
            return Task.CompletedTask;
        });
        await check("Discord ignores saved dimensions and only reports explicit dimension changes", () =>
        {
            var parser = new McStateMachine();
            parser.ProcessLine(Server("Preparing level \"World\""));
            parser.ProcessLine(Server("Preparing start region for dimension minecraft:overworld"));
            parser.ProcessLine(Server("Preparing start region for dimension minecraft:the_nether"));
            parser.ProcessLine(Server("Saving chunks for level 'ServerLevel[World]'/minecraft:the_end"));
            parser.ProcessLine(Client("Loaded dimension minecraft:the_end"));
            Require(parser.State.Dimension == null);
            parser.ProcessLine(Client("Changing dimension to minecraft:the_nether"));
            Require(parser.State.Dimension == "the_nether");
            parser.ProcessLine(Client("Connecting to new.test, 25565"));
            Require(parser.State.Dimension == null && parser.State.World == null);
            return Task.CompletedTask;
        });
        await check("Discord UTF-8 payloads fit limits without splitting Unicode characters", () =>
        {
            using var fixture = new Fixture();
            var settings = new LauncherSettings { DiscordRpc = false };
            fixture.Presence.Configure(settings, "ru");
            var id = fixture.Start(string.Concat(Enumerable.Repeat("Длинная сборка 🛠 ", 30)));
            fixture.Presence.ProcessLogLine(id, Client("Connecting to " + new string('a', 220) + ".test, 25566"));
            var payload = fixture.Presence.BuildPresence();
            foreach (var value in new[] { payload.Details, payload.State, payload.Assets.LargeImageText })
            {
                Require(Encoding.UTF8.GetByteCount(value) <= 128 && !value.Contains('\uFFFD'));
                Require(!value.Any(char.IsControl));
            }
            Require(payload.State.Contains("1.21.1") && payload.State.Contains("Fabric"));
            Require(PresenceActivity.Text("One\r\n\tTwo\u202e", 30) == "One Two");
            Require(payload.Buttons.Length == 1 && payload.Buttons.All(b => b.Url.StartsWith("https://")));
            return Task.CompletedTask;
        });
        await check("Discord privacy hides server address in every field and button", () =>
        {
            using var fixture = new Fixture();
            var id = fixture.Start("Private");
            fixture.Presence.ProcessLogLine(id, Client("Connecting to 2001:db8::1, 25566"));
            fixture.Presence.ProcessLogLine(id, Client("Loaded 1 advancements"));
            Require(fixture.Presence.Snapshot.Details.Contains("[2001:db8::1]:25566"));
            fixture.Presence.Configure(new() { DiscordRpc = false, DiscordShowServer = false }, "ru");
            var json = Newtonsoft.Json.JsonConvert.SerializeObject(fixture.Presence.BuildPresence());
            Require(!json.Contains("2001:db8") && !json.Contains("25566") && !json.Contains("mechanica://"));
            Require(fixture.Presence.Snapshot.Details == "Сетевая игра");
            return Task.CompletedTask;
        });
        await check("Discord settings refresh dimensions, advancements and mod count without resetting time", () =>
        {
            using var fixture = new Fixture();
            var id = fixture.Start("Pack");
            fixture.Presence.ProcessLogLine(id, Client("Setting user: Tester"));
            fixture.Presence.ProcessLogLine(id, Server("Preparing level \"World\""));
            fixture.Presence.ProcessLogLine(id, Client("Entering dimension minecraft:the_nether"));
            fixture.Presence.ProcessLogLine(id, Client("[CHAT] Tester has made the advancement [Diamonds!]"));
            Require(fixture.Presence.Snapshot.Details.Contains("Diamonds!"));
            var started = fixture.Presence.Snapshot.StartedAt;
            var settings = new LauncherSettings { DiscordRpc = false, DiscordShowAchievements = false };
            fixture.Presence.Configure(settings, "ru");
            Require(fixture.Presence.Snapshot.Details.Contains("Незер"));
            settings.DiscordShowDimension = false;
            settings.DiscordShowMods = false;
            fixture.Presence.Configure(settings, "ru");
            Require(!fixture.Presence.Snapshot.Details.Contains("Незер") && !fixture.Presence.Snapshot.State.Contains("модов:"));
            Require(fixture.Presence.Snapshot.StartedAt == started);
            return Task.CompletedTask;
        });
        await check("Discord IPC enable, disable and reconnect preserve the game and clear the previous activity", async () =>
        {
            using var fixture = new Fixture();
            var id = fixture.Start("Running");
            var started = fixture.Presence.Snapshot.StartedAt;
            var settings = new LauncherSettings { DiscordRpc = true };
            fixture.Presence.Configure(settings, "en");
            await fixture.PumpUntil(() => fixture.Presence.Snapshot.Status == DiscordConnectionStatus.Connected);
            Require(fixture.Pipes.Count == 1 && fixture.Pipes[0].Activities.Any(a => a?["state"]?.ToString().Contains("Running") == true));
            fixture.Presence.Configure(settings, "en");
            Require(fixture.Pipes.Count == 1);
            settings.DiscordRpc = false;
            fixture.Presence.Configure(settings, "en");
            await fixture.PumpUntil(() => fixture.Pipes[0].Activities.Any(a => a == null));
            Require(fixture.Pipes[0].Activities.Any(a => a == null) && fixture.Presence.Snapshot.Status == DiscordConnectionStatus.Disabled);
            fixture.Presence.ProcessLogLine(id, Client("Sound engine started"));
            fixture.Time.Advance(10);
            settings.DiscordRpc = true;
            fixture.Presence.Configure(settings, "en");
            await fixture.PumpUntil(() => fixture.Presence.Snapshot.Status == DiscordConnectionStatus.Connected);
            Require(fixture.Presence.Snapshot.Details == "Main menu" && fixture.Presence.Snapshot.StartedAt == started);
            fixture.Presence.Reconnect();
            await fixture.PumpUntil(() => fixture.Presence.Snapshot.Status == DiscordConnectionStatus.Connected);
            Require(fixture.Pipes.Count == 3 && fixture.Presence.Snapshot.StartedAt == started);
        });
        await check("Discord IPC coalesces log bursts and applies privacy immediately", async () =>
        {
            using var fixture = new Fixture();
            var id = fixture.Start("Pack");
            var settings = new LauncherSettings();
            fixture.Presence.Configure(settings, "en");
            await fixture.PumpUntil(() => fixture.Presence.Snapshot.Status == DiscordConnectionStatus.Connected);
            var pipe = fixture.Pipes[0];
            var sent = pipe.Activities.Count;
            for (var i = 0; i < 20; i++)
            {
                fixture.Presence.ProcessLogLine(id, Client($"Connecting to server{i}.test, 25565"));
                fixture.Presence.Tick();
            }
            Require(pipe.Activities.Count == sent);
            fixture.Time.Advance(5);
            await fixture.PumpUntil(() => pipe.Activities.Count > sent);
            Require(pipe.Activities.Last()?["details"]?.ToString().Contains("server19.test") == true);
            settings.DiscordShowServer = false;
            fixture.Presence.Configure(settings, "en");
            await fixture.PumpUntil(() => fixture.Pipes.Count == 2 &&
                fixture.Pipes[1].Activities.LastOrDefault()?["details"]?.ToString() == "Connecting to a server");
            Require(pipe.Activities.Last() == null);
            Require(fixture.Pipes[1].Activities.All(a => a?.ToJsonString().Contains("server19.test") != true));
        });
        await check("Discord IPC reconnects automatically and keeps the latest state while offline", async () =>
        {
            using var fixture = new Fixture();
            fixture.AcceptConnections = false;
            var id = fixture.Start("Pack");
            fixture.Presence.Configure(new(), "en");
            await fixture.PumpUntil(() => fixture.Presence.Snapshot.Status == DiscordConnectionStatus.WaitingForDiscord);
            fixture.Presence.ProcessLogLine(id, Client("Connecting to latest.test, 25565"));
            var pipe = fixture.Pipes[0];
            pipe.AcceptConnections = true;
            await fixture.PumpUntil(() => fixture.Presence.Snapshot.Status == DiscordConnectionStatus.Connected, 15000);
            Require(pipe.Activities.Last()?["details"]?.ToString().Contains("latest.test") == true);
            pipe.Send(new { cmd = "DISPATCH", evt = "ERROR", data = new { code = 4000, message = "Test rejection" } });
            await fixture.PumpUntil(() => fixture.Presence.Snapshot.Status == DiscordConnectionStatus.Error);
            Require(fixture.Presence.Snapshot.Error?.Contains("Test rejection") == true);
        });
        await check("Discord concurrent game logs and exits cannot corrupt the selected session", async () =>
        {
            using var fixture = new Fixture();
            var ids = Enumerable.Range(0, 12).Select(i => fixture.Start("Pack" + i)).ToArray();
            await Task.WhenAll(ids.Take(11).Select(id => Task.Run(() =>
            {
                for (var i = 0; i < 100; i++) fixture.Presence.ProcessLogLine(id, Client("Connecting to other.test, 25565"));
                fixture.Presence.EndSession(id);
            })));
            Require(fixture.Presence.Snapshot.State.Contains("Pack11") && fixture.Presence.Snapshot.Details == "Starting Minecraft");
            fixture.Presence.Dispose();
            fixture.Presence.Configure(new(), "en");
            Require(fixture.Presence.Snapshot.Status == DiscordConnectionStatus.Disabled && fixture.Pipes.Count == 0);
        });
        await check("Discord does not replay a visible server after privacy changes while disconnected", async () =>
        {
            using var fixture = new Fixture();
            var id = fixture.Start("Pack");
            fixture.Presence.ProcessLogLine(id, Client("Connecting to private.test, 25565"));
            var settings = new LauncherSettings();
            fixture.Presence.Configure(settings, "en");
            await fixture.PumpUntil(() => fixture.Presence.Snapshot.Status == DiscordConnectionStatus.Connected);
            fixture.AcceptConnections = false;
            fixture.Pipes[0].AcceptConnections = false;
            fixture.Pipes[0].Close();
            await fixture.PumpUntil(() => fixture.Presence.Snapshot.Status == DiscordConnectionStatus.WaitingForDiscord, 15000);
            settings.DiscordShowServer = false;
            fixture.Presence.Configure(settings, "en");
            Require(fixture.Pipes.Count == 2);
            fixture.Pipes[1].AcceptConnections = true;
            await fixture.PumpUntil(() => fixture.Presence.Snapshot.Status == DiscordConnectionStatus.Connected, 15000);
            Require(fixture.Pipes[1].Activities.Count > 0 &&
                fixture.Pipes[1].Activities.All(a => a?.ToJsonString().Contains("private.test") != true));
        });
    }

    private static string Client(string text) => "[12:00:00] [Render thread/INFO]: " + text;
    private static string Server(string text) => "[12:00:00] [Server thread/INFO]: " + text;
    private static GameInstance Instance(string name) => new() { Id = "instance", Name = name, McVersion = "1.21.1", Loader = LoaderType.Fabric };
    private static void Require(bool condition) { if (!condition) throw new InvalidOperationException("Discord assertion failed."); }

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 9, 22, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(int seconds) => _now = _now.AddSeconds(seconds);
    }

    private sealed class Fixture : IDisposable
    {
        public ManualTime Time { get; } = new();
        public List<FakePipe> Pipes { get; } = [];
        public bool AcceptConnections { get; set; } = true;
        public DiscordPresence Presence { get; }
        public Fixture() => Presence = new(Time, () =>
        {
            var pipe = new FakePipe { AcceptConnections = AcceptConnections };
            Pipes.Add(pipe);
            return new DiscordRpcClient(DiscordPresence.ApplicationId, autoEvents: false, client: pipe);
        });
        public Guid Start(string name)
        {
            var id = Presence.BeginPreparation(Instance(name));
            Presence.GameStarted(id, 8);
            return id;
        }
        public async Task PumpUntil(Func<bool> predicate, int timeout = 6000)
        {
            using var cancellation = new CancellationTokenSource(timeout);
            while (!predicate())
            {
                if (cancellation.IsCancellationRequested)
                    throw new InvalidOperationException($"Discord IPC timeout: {Presence.Snapshot}; payloads: {string.Join(", ", Pipes.Select(p => p.Activities.Count))}");
                Presence.Tick();
                await Task.Delay(10);
            }
        }
        public void Dispose() => Presence.Dispose();
    }

    private sealed class FakePipe : INamedPipeClient
    {
        private readonly ConcurrentQueue<PipeFrame> _incoming = new();
        public ConcurrentQueue<JsonNode?> Activities { get; } = new();
        public ILogger Logger { get; set; } = new NullLogger();
        public bool IsConnected { get; private set; }
        public int ConnectedPipe => 0;
        public volatile bool AcceptConnections = true;
        public bool Connect(int pipe) => IsConnected = AcceptConnections;
        public void Close() => IsConnected = false;
        public void Dispose() => Close();
        public bool ReadFrame(out PipeFrame frame) => _incoming.TryDequeue(out frame);
        public void Send(object payload)
        {
            var frame = new PipeFrame();
            frame.SetObject(Opcode.Frame, Newtonsoft.Json.Linq.JToken.Parse(System.Text.Json.JsonSerializer.Serialize(payload)));
            _incoming.Enqueue(frame);
        }
        public bool WriteFrame(PipeFrame frame)
        {
            if (frame.Opcode == Opcode.Handshake)
                Send(new { cmd = "DISPATCH", evt = "READY", data = new { v = 1, config = new { cdn_host = "cdn.discordapp.com", api_endpoint = "//discord.com/api" }, user = new { id = "1", username = "Test", discriminator = "0001", avatar = (string?)null } } });
            else if (frame.Opcode == Opcode.Frame)
            {
                var message = JsonNode.Parse(Encoding.UTF8.GetString(frame.Data))!;
                if (message["cmd"]?.ToString() == "SET_ACTIVITY")
                {
                    var activity = message["args"]?["activity"]?.DeepClone();
                    Activities.Enqueue(activity?.DeepClone());
                    if (activity != null)
                    {
                        activity["application_id"] = DiscordPresence.ApplicationId;
                        activity["name"] = "Mechanica Launcher";
                    }
                    Send(new { cmd = "SET_ACTIVITY", nonce = message["nonce"]?.ToString(), data = activity });
                }
            }
            return true;
        }
    }
}
