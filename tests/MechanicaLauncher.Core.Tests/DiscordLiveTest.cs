using System.Collections.Concurrent;
using DiscordRPC;
using MechanicaLauncher.Core.Discord;
using MechanicaLauncher.Core.Instances;
using MechanicaLauncher.Core.Profiles;

internal static class DiscordLiveTest
{
    public static async Task<int> RunAsync()
    {
        var received = new ConcurrentQueue<(string Details, string State, string? Image, ulong? Start)>();
        var clears = 0;
        using var presence = new DiscordPresence(TimeProvider.System, () =>
        {
            var client = new DiscordRpcClient(DiscordPresence.ApplicationId, autoEvents: false);
            client.OnRpcMessage += (_, message) =>
            {
                if (message is DiscordRPC.Message.PresenceMessage { Presence: null }) Interlocked.Increment(ref clears);
                if (message is not DiscordRPC.Message.PresenceMessage { Presence: { } activity }) return;
                received.Enqueue((activity.Details, activity.State, activity.Assets?.LargeImageKey, activity.Timestamps?.StartUnixMilliseconds));
            };
            return client;
        });
        var settings = new LauncherSettings { DiscordShowServer = false };
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            var png = await http.GetByteArrayAsync(DiscordPresence.MinecraftImage);
            if (png.Length < 8 || !png.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
                throw new InvalidOperationException("The public activity image is not a PNG.");
            Console.WriteLine("PASS Public Mojang image is reachable and contains valid PNG data");
            presence.Configure(settings, "ru");
            await WaitFor(() => received.Any(p => p.Details == "В лаунчере"));
            if (!received.Any(p => p.Image == DiscordPresence.MinecraftImage ||
                p.Image?.StartsWith("mp:external/", StringComparison.Ordinal) == true || ulong.TryParse(p.Image, out _)))
                throw new InvalidOperationException("Discord acknowledgement is missing the activity image.");
            Console.WriteLine("PASS Discord desktop acknowledged launcher activity with the Minecraft image URL");
            var session = presence.BeginPreparation(new GameInstance { Name = "Проверка Mechanica", McVersion = "1.21.1", Loader = LoaderType.Fabric });
            presence.GameStarted(session, 8);
            presence.ProcessLogLine(session, "[12:00:00] [Render thread/INFO]: Sound engine started");
            await WaitFor(() => received.Any(p => p.Details == "Главное меню" && p.Start.HasValue));
            var started = presence.Snapshot.StartedAt;
            Console.WriteLine("PASS Discord desktop accepted Cyrillic game status, metadata and session timer");
            presence.ProcessLogLine(session, "[12:00:00] [Render thread/INFO]: Connecting to private.example.test, 25565");
            presence.ProcessLogLine(session, "[12:00:00] [Render thread/INFO]: Loaded 10 advancements");
            await WaitFor(() => received.Any(p => p.Details == "Сетевая игра"));
            if (received.Any(p => (p.Details + p.State).Contains("private.example.test")))
                throw new InvalidOperationException("Hidden server was transmitted.");
            Console.WriteLine("PASS Discord desktop accepted multiplayer with the server address hidden");
            received.Clear();
            presence.Reconnect();
            await WaitFor(() => received.Any(p => p.Details == "Сетевая игра"));
            if (presence.Snapshot.StartedAt != started) throw new InvalidOperationException("Reconnect reset the timer.");
            Console.WriteLine("PASS Real Discord IPC reconnect restored the same game and timer");
            presence.EndSession(session);
            await WaitFor(() => received.Any(p => p.Details == "В лаунчере" && p.Start == null));
            Console.WriteLine("PASS Discord desktop accepted idle status without stale game metadata or timer");
            settings.DiscordRpc = false;
            var previousClears = Volatile.Read(ref clears);
            presence.Configure(settings, "ru");
            await WaitFor(() => Volatile.Read(ref clears) > previousClears);
            Console.WriteLine("PASS Discord desktop acknowledged clearing the activity on disable");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAIL {ex}\n{presence.Snapshot}");
            foreach (var activity in received) Console.WriteLine(activity);
            return 1;
        }

        async Task WaitFor(Func<bool> condition)
        {
            var deadline = DateTime.UtcNow.AddSeconds(25);
            while (!condition())
            {
                presence.Tick();
                if (presence.Snapshot.Status == DiscordConnectionStatus.Error)
                    throw new InvalidOperationException(presence.Snapshot.Error);
                if (DateTime.UtcNow > deadline) throw new TimeoutException("Discord did not acknowledge the expected activity.");
                await Task.Delay(100);
            }
            presence.Tick();
        }
    }
}
