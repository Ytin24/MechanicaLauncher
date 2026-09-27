using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MechanicaLauncher.Core.Game;
using MechanicaLauncher.Core.Instances;
using MechanicaLauncher.Core.IO;
using MechanicaLauncher.Core.Models;
using MechanicaLauncher.Core.Mods;
using MechanicaLauncher.Core.Servers;

internal static class FeatureTests
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> check, string root)
    {
        string Area(string name) { var path = Path.Combine(root, "features", name); Directory.CreateDirectory(path); return path; }
        await check("Download queue preserves order and skips cancelled pending operations", async () =>
        {
            var queue = new DownloadQueue();
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var order = new List<int>();
            var first = queue.Enqueue("one", "a", async ct => { await release.Task.WaitAsync(ct); order.Add(1); });
            var cancelled = queue.Enqueue("two", "b", _ => { order.Add(2); return Task.CompletedTask; });
            var last = queue.Enqueue("three", "c", _ => { order.Add(3); return Task.CompletedTask; });
            cancelled.Cancel();
            release.SetResult();
            await last.Completion.WaitAsync(TimeSpan.FromSeconds(3));
            Require(order.SequenceEqual([1, 3]) && first.State == DownloadState.Completed && cancelled.State == DownloadState.Cancelled && !queue.HasPending);
        });
        await check("Active download cancellation can retry the owning operation", async () =>
        {
            var queue = new DownloadQueue();
            int attempts = 0;
            var first = queue.Enqueue("retry", "a", async ct => { if (++attempts == 1) await Task.Delay(Timeout.Infinite, ct); });
            first.Cancel();
            await first.Completion.WaitAsync(TimeSpan.FromSeconds(3));
            Require(first.State == DownloadState.Cancelled && first.Snapshot().CanRetry);
            queue.Retry(first);
            await queue.Jobs.Last().Completion.WaitAsync(TimeSpan.FromSeconds(3));
            Require(attempts == 2 && queue.Jobs.Last().State == DownloadState.Completed);
        });
        await check("Download center counts only verified files and retries after failed hashes", async () =>
        {
            var queue = new DownloadQueue();
            byte[] valid = Encoding.UTF8.GetBytes("verified content");
            bool broken = true;
            using var handler = new FakeHttp(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(broken ? [1, 2, 3] : valid) });
            using var http = new HttpClient(handler);
            var path = Path.Combine(Area("downloads"), "file.jar");
            await File.WriteAllTextAsync(path, "old content");
            var first = queue.Enqueue("file", "a", ct => FileDownloader.EnsureAsync(http, "https://example.test/file", path,
                sha512: Convert.ToHexString(SHA512.HashData(valid)), cancellationToken: ct));
            await first.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            Require(first.State == DownloadState.Failed && first.Snapshot().Files.All(f => !f.Complete));
            Require(await File.ReadAllTextAsync(path) == "old content" && Directory.GetFiles(Path.GetDirectoryName(path)!, "*.part").Length == 0);
            broken = false;
            queue.Retry(first);
            var second = queue.Jobs.Last();
            await second.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            var snapshot = second.Snapshot();
            Require(second.State == DownloadState.Completed && snapshot.Received == valid.Length && snapshot.Files.Single().Complete);
            Require(File.ReadAllBytes(path).SequenceEqual(valid) && !snapshot.CanRetry);
        });
        await check("Cancelling a streaming download removes its temporary file without replacing the old file", async () =>
        {
            var queue = new DownloadQueue();
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var handler = new FakeHttp(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StallingStream(started)) });
            using var http = new HttpClient(handler);
            var path = Path.Combine(Area("cancel-stream"), "file.jar");
            await File.WriteAllTextAsync(path, "old");
            var job = queue.Enqueue("stream", "a", ct => FileDownloader.EnsureAsync(http, "https://example.test/file", path, size: 4096, cancellationToken: ct));
            await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            job.Cancel();
            await job.Completion.WaitAsync(TimeSpan.FromSeconds(3));
            Require(job.State == DownloadState.Cancelled && File.ReadAllText(path) == "old" && Directory.GetFiles(Path.GetDirectoryName(path)!, "*.part").Length == 0);
        });
        await check("Download tracking does not leak between sequential operations", async () =>
        {
            var queue = new DownloadQueue();
            using var handler = new FakeHttp(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) });
            using var http = new HttpClient(handler);
            var dir = Area("isolated-jobs");
            var a = queue.Enqueue("a", null, ct => FileDownloader.EnsureAsync(http, "https://example.test/a", Path.Combine(dir, "a"), cancellationToken: ct));
            var b = queue.Enqueue("b", null, ct => FileDownloader.EnsureAsync(http, "https://example.test/b", Path.Combine(dir, "b"), cancellationToken: ct));
            await b.Completion.WaitAsync(TimeSpan.FromSeconds(3));
            Require(a.Snapshot().Files.Single().Name == "a" && b.Snapshot().Files.Single().Name == "b" && DownloadQueue.Current == null);
        });
        await check("Quick compatibility finds wrong loader, duplicate and damaged enabled mods", async () =>
        {
            var dir = Area("compat-local");
            MakeJar(dir, "one", "{\"id\":\"same\",\"version\":\"1\",\"depends\":{\"needed\":\"*\"}}");
            MakeJar(dir, "two", "{\"id\":\"same\",\"version\":\"2\"}");
            MakeJar(dir, "forge", "modLoader=\"javafml\"", "META-INF/mods.toml");
            File.WriteAllText(Path.Combine(dir, "mods", "broken.jar"), "broken");
            File.WriteAllText(Path.Combine(dir, "mods", "ignored.jar.disabled"), "ignored");
            var report = await new ModCompatibilityChecker().CheckAsync(Instance(), dir, false);
            Require(report.EnabledFiles == 4 && report.DisabledFiles == 1 && report.IdentifiedFiles == 0);
            foreach (var code in new[] { "loader", "duplicate", "unreadable", "dependency" }) Require(report.Issues.Any(i => i.Code == code));
        });
        await check("A cancellation arriving after a committed operation does not turn success into cancellation", async () =>
        {
            var queue = new DownloadQueue();
            var job = queue.Enqueue("committed", null, _ => { DownloadQueue.CurrentCancellation!.Cancel(); return Task.CompletedTask; });
            await job.Completion;
            Require(job.State == DownloadState.Completed);
        });
        await check("Repeated Retry clicks do not duplicate the same failed operation", async () =>
        {
            var queue = new DownloadQueue();
            int attempts = 0;
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var failed = queue.Enqueue("retry-once", null, async ct =>
            {
                if (++attempts == 1) throw new IOException("failed");
                await gate.Task.WaitAsync(ct);
            });
            await failed.Completion;
            queue.Retry(failed);
            queue.Retry(failed);
            Require(queue.Jobs.Count == 2 && !failed.Snapshot().CanRetry);
            gate.SetResult();
            await queue.Jobs.Last().Completion.WaitAsync(TimeSpan.FromSeconds(3));
            Require(attempts == 2);
        });
        await check("Offline Fabric checks Minecraft, loader and mod version ranges and declared breaks", async () =>
        {
            var dir = Area("compat-ranges");
            MakeJar(dir, "consumer", "{\"id\":\"consumer\",\"version\":\"1\",\"depends\":{\"minecraft\":\"~1.20.1\",\"fabricloader\":\">=0.17\",\"library\":\">=2\"},\"breaks\":{\"other\":\"*\"}}");
            MakeJar(dir, "library", "{\"id\":\"library\",\"version\":\"1.9\"}");
            MakeJar(dir, "other", "{\"id\":\"other\",\"version\":\"3\"}");
            var report = await new ModCompatibilityChecker().CheckAsync(Instance(), dir, false);
            Require(report.Issues.Count(i => i.Code == "version_range") == 3 && report.Issues.Any(i => i.Code == "conflict"));
            Require(FabricVersionRequirement.Matches("1.21.1", [">=1.20 <1.22"]) == true);
            Require(FabricVersionRequirement.Matches("1.21.1", ["1.21.x"]) == true);
            Require(FabricVersionRequirement.Matches("1.21.1", ["1.20.x"]) == false);
            Require(FabricVersionRequirement.Matches("0.2", ["^0.1"]) == true);
            Require(FabricVersionRequirement.Matches("1.21.1+build", ["1.21.1"]) == true);
            Require(FabricVersionRequirement.Matches("1.21.1", ["1.20", "~1.21"]) == true);
            Require(FabricVersionRequirement.Matches("26w01a", [">=26.1"]) == null);
            Require(FabricVersionRequirement.Matches("1.21.1", [">=1.21-pre1"]) == null);
        });
        await check("Fabric nested jars and provides satisfy local dependency presence checks", async () =>
        {
            var dir = Area("compat-nested");
            MakeJar(dir, "consumer", "{\"id\":\"consumer\",\"depends\":{\"nested\":\"*\",\"alias\":\"*\",\"minecraft\":\"*\"}}");
            var file = MakeJar(dir, "container", "{\"id\":\"container\",\"jars\":[{\"file\":\"META-INF/jars/nested.jar\"}]}");
            using (var zip = ZipFile.Open(file, ZipArchiveMode.Update))
            using (var output = zip.CreateEntry("META-INF/jars/nested.jar").Open())
            using (var inner = new ZipArchive(output, ZipArchiveMode.Create))
            using (var writer = new StreamWriter(inner.CreateEntry("fabric.mod.json").Open()))
                writer.Write("{\"id\":\"nested\",\"provides\":[\"alias\"]}");
            var report = await new ModCompatibilityChecker().CheckAsync(Instance(), dir, false);
            Require(report.Issues.Count == 0);
        });
        await check("Fabric multiline descriptions preserve offline dependency and version checks", async () =>
        {
            var dir = Area("compat-multiline");
            MakeJar(dir, "multiline", "{\"schemaVersion\":1,\"id\":\"multiline\",\"version\":\"1\",\"description\":\"Первая строка\nВторая строка\r\nТретья\tстрока\",\"depends\":{\"minecraft\":\"~1.20.1\",\"missing\":\"*\"}}");
            MakeJar(dir, "consumer", "{\"schemaVersion\":1,\"id\":\"consumer\",\"version\":\"1\",\"depends\":{\"multiline\":\"*\"}}");
            var report = await new ModCompatibilityChecker().CheckAsync(Instance(), dir, false);
            Require(report.EnabledFiles == 2 && !report.Issues.Any(i => i.Code == "unreadable"));
            Require(report.Issues.Single(i => i.Code == "dependency").Detail == "multiline.jar → missing");
            Require(report.Issues.Count(i => i.Code == "version_range") == 1);
        });
        await check("Fabric multiline descriptions preserve escaped quotes and backslashes", async () =>
        {
            var dir = Area("compat-multiline-escapes");
            var metadata = """{"schemaVersion":1,"id":"quoted","version":"1","description":"An \"escaped quote\" and \\ backslash""" + "\n" +
                """A second line, a literal \\n and an escaped newline \n; end \\","depends":{"missing":"*"}}""";
            MakeJar(dir, "quoted", metadata);
            var report = await new ModCompatibilityChecker().CheckAsync(Instance(), dir, false);
            Require(!report.Issues.Any(i => i.Code == "unreadable"));
            Require(report.Issues.Single(i => i.Code == "dependency").Detail == "quoted.jar → missing");
        });
        await check("Malformed Fabric syntax and escape sequences remain unreadable beside multiline text", async () =>
        {
            var dir = Area("compat-multiline-invalid");
            var malformed = new[] {
                """{"id":"bad","description":"first<break>line","depends" {"missing":"*"}}""",
                """{"id":"bad","description":"first<break>line \q","depends":{"missing":"*"}}""",
                """{"id":"bad","description":"first<break>line}"""
            };
            for (int i = 0; i < malformed.Length; i++) MakeJar(dir, "bad-" + i, malformed[i].Replace("<break>", "\n"));
            var report = await new ModCompatibilityChecker().CheckAsync(Instance(), dir, false);
            Require(report.Issues.Count(i => i.Code == "unreadable") == malformed.Length);
            Require(!report.Issues.Any(i => i.Code == "dependency"));
        });
        await check("Compatibility uses bulk hashes, caches exact results, and exposes offline coverage", async () =>
        {
            var dir = Area("compat-catalog");
            var a = MakeJar(dir, "a", "{\"id\":\"a\"}");
            var b = MakeJar(dir, "b", "{\"id\":\"b\"}");
            var versions = new Dictionary<string, ModrinthVersion>
            {
                [Hash(a)] = new() { Id = "va", ProjectId = "pa", GameVersions = ["1.20.1"], Loaders = ["forge"], Dependencies = [new() { DependencyType = "incompatible", ProjectId = "pb" }] },
                [Hash(b)] = new() { Id = "vb", ProjectId = "pb", GameVersions = ["1.21.1"], Loaders = ["fabric"] }
            };
            using var handler = new FakeHttp(async (request, ct) =>
            {
                Require(request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/v2/version_files");
                using var data = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                Require(data.RootElement.GetProperty("hashes").GetArrayLength() == 2 && data.RootElement.GetProperty("algorithm").GetString() == "sha1");
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(versions) };
            });
            using var http = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
            var checker = new ModCompatibilityChecker(new ModrinthClient(http));
            var full = await checker.CheckAsync(Instance(), dir, true);
            Require(full.IdentifiedFiles == 2 && full.CatalogAvailable && full.Issues.Any(i => i.Code == "minecraft") && full.Issues.Any(i => i.Code == "conflict"));
            var cached = await checker.CheckAsync(Instance(), dir, false);
            Require(handler.Calls == 1 && cached.Issues.Any(i => i.Code == "minecraft"));
            using var offlineHandler = new FakeHttp(_ => throw new HttpRequestException("offline"));
            using var offlineHttp = new HttpClient(offlineHandler) { BaseAddress = new Uri("https://example.test") };
            var offline = await new ModCompatibilityChecker(new ModrinthClient(offlineHttp)).CheckAsync(Instance(), dir, true);
            Require(!offline.CatalogAvailable && offline.IdentifiedFiles == 2 && offline.Issues.Any(i => i.Code == "offline"));
            File.WriteAllText(a, "changed file");
            var changed = await checker.CheckAsync(Instance(), dir, false);
            Require(changed.IdentifiedFiles == 1);
        });
        await check("Malformed metadata is reported instead of crashing the compatibility check", async () =>
        {
            var dir = Area("compat-malformed");
            MakeJar(dir, "bad", "[]");
            var report = await new ModCompatibilityChecker().CheckAsync(Instance(), dir, false);
            Require(report.Issues.Any(i => i.Code == "unreadable"));
            using var cancel = new CancellationTokenSource();
            cancel.Cancel();
            await Throws<OperationCanceledException>(() => new ModCompatibilityChecker().CheckAsync(Instance(), dir, true, cancel.Token));
        });
        await check("Crash analysis gives evidence and leaves unknown failures unknown", () =>
        {
            var memory = CrashAnalyzer.Analyze("warning\njava.lang.OutOfMemoryError: Java heap space\nMixinApplyError", -1);
            Require(memory.Reason == "memory" && memory.Evidence.Contains("OutOfMemoryError"));
            Require(CrashAnalyzer.Analyze("UnsupportedClassVersionError", 1).Reason == "java");
            Require(CrashAnalyzer.Analyze("GLFW error 65542", 1).Reason == "graphics");
            Require(CrashAnalyzer.Analyze("DuplicateModsFoundException", 1).Reason == "duplicate");
            Require(CrashAnalyzer.Analyze("ModResolutionException", 1).Reason == "dependency");
            Require(CrashAnalyzer.Analyze("some crash", -1073741819).Reason == "unknown");
            return Task.CompletedTask;
        });
        await check("Saved crash history is bounded and redacts explicit secrets and common token fields", async () =>
        {
            var dir = Area("crashes");
            Directory.CreateDirectory(Path.Combine(dir, "logs"));
            File.WriteAllText(Path.Combine(dir, "logs", "launcher-latest.log"), "OutOfMemoryError\naccess_token=abc123\nAuthorization: Bearer abc456\n--accessToken abc789\nprivate-secret");
            var report = await CrashAnalyzer.CaptureAsync(dir, -1, ["private-secret"]);
            Require(!report.LogTail.Contains("abc123") && !report.LogTail.Contains("abc456") && !report.LogTail.Contains("abc789") && !report.LogTail.Contains("private-secret"));
            for (int i = 0; i < 22; i++) CrashAnalyzer.SaveReport(dir, report with { ExitCode = i });
            var history = CrashAnalyzer.GetReports(dir);
            Require(history.Count == 20 && history[0].ExitCode == 21 && history[^1].ExitCode == 2);
        });
        await check("Crash tail reading is bounded and tolerates missing logs", async () =>
        {
            var path = Path.Combine(Area("tail"), "log");
            await File.WriteAllTextAsync(path, new string('x', 200_000) + "\nlast line\n");
            var tail = await CrashAnalyzer.ReadTailAsync(path);
            Require(tail.Length <= 128_000 && tail.Contains("last line"));
            Require(await CrashAnalyzer.ReadTailAsync(path + ".missing") == "");
        });
        await check("Screenshots stay within the instance and sort by newest first", () =>
        {
            var dir = Area("screenshots");
            var media = Path.Combine(dir, "screenshots");
            Directory.CreateDirectory(Path.Combine(media, "nested"));
            File.WriteAllText(Path.Combine(media, "old.png"), "a");
            File.SetLastWriteTimeUtc(Path.Combine(media, "old.png"), DateTime.UtcNow.AddDays(-1));
            File.WriteAllText(Path.Combine(media, "new.JPG"), "b");
            File.WriteAllText(Path.Combine(media, "ignore.txt"), "c");
            File.WriteAllText(Path.Combine(media, "nested", "ignore.png"), "d");
            var images = InstanceMedia.GetScreenshots(dir);
            Require(images.Count == 2 && images[0].Name == "new.JPG");
            return Task.CompletedTask;
        });
        await check("Instance covers and accents persist and survive duplication", () =>
        {
            var manager = new InstanceManager(Area("appearance"));
            var instance = manager.CreateInstance("Beautiful", "1.21.1");
            var image = Path.Combine(Area("appearance-source"), "cover.png");
            File.WriteAllBytes(image, Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/lS8AAAAASUVORK5CYII="));
            manager.SaveAppearance(instance, image, "#507EAA");
            var saved = manager.GetInstance(instance.Id)!;
            Require(saved.AccentColor == "#507EAA" && !Path.IsPathRooted(saved.CoverPath!) && manager.GetCoverAbsolutePath(saved) != null);
            var clone = manager.DuplicateInstance(saved.Id);
            Require(manager.GetCoverAbsolutePath(clone) != null && clone.AccentColor == saved.AccentColor);
            manager.SaveAppearance(saved, null, null);
            Require(manager.GetInstance(saved.Id)!.CoverPath == null);
            saved.CoverPath = "../../outside.png";
            Require(manager.GetCoverAbsolutePath(saved) == null && !InstanceMedia.IsAccent("red"));
            return Task.CompletedTask;
        });
        await check("Staging an instance icon keeps saved appearance intact until the full save succeeds", async () =>
        {
            var manager = new InstanceManager(Area("appearance-draft"));
            var instance = manager.CreateInstance("Personal", "1.21.1");
            var source = Path.Combine(Area("icon-source"), "icon.png");
            byte[] image = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/lS8AAAAASUVORK5CYII=");
            File.WriteAllBytes(source, image);
            manager.SetIconFromFile(instance, source);
            var savedPath = manager.GetIconAbsolutePath(instance)!;
            var previousIcon = instance.IconPath;
            manager.SetIconFromFile(instance, savedPath, save: false);
            Require(instance.IconPath != previousIcon && manager.GetInstance(instance.Id)!.IconPath == previousIcon);
            Require(File.ReadAllBytes(savedPath).SequenceEqual(image));
            var invalidCover = Path.Combine(Area("icon-source"), "invalid.png");
            File.WriteAllText(invalidCover, "invalid image");
            await Throws<InvalidDataException>(() => { manager.SaveAppearance(instance, invalidCover, "#507EAA"); return Task.CompletedTask; });
            Require(manager.GetInstance(instance.Id)!.IconPath == previousIcon && File.ReadAllBytes(savedPath).SequenceEqual(image));
            manager.SaveAppearance(instance, source, "#507EAA");
            var saved = manager.GetInstance(instance.Id)!;
            Require(saved.IconPath == instance.IconPath && saved.CoverPath != null && saved.AccentColor == "#507EAA");
            var clone = manager.DuplicateInstance(instance.Id);
            Require(manager.GetIconAbsolutePath(clone) != null && File.ReadAllBytes(manager.GetIconAbsolutePath(clone)!).SequenceEqual(image));
        });
        await check("Server addresses handle DNS, IPv4 and IPv6 and reject invalid ports or URLs", () =>
        {
            foreach (var valid in new[] { "play.example.org", "127.0.0.1:25566", "[::1]:25565", "::1" })
                Require(FavoriteServers.TryParseAddress(valid, out _, out _));
            foreach (var invalid in new[] { "", "https://example.org", "host:0", "host:65536", "host:no", "has space.org", "[::1]suffix" })
                Require(!FavoriteServers.TryParseAddress(invalid, out _, out _));
            Require(FavoriteServers.TryParseAddress("[::1]:25566", out var host, out var port) && host == "::1" && port == 25566);
            return Task.CompletedTask;
        });
        await check("Invalid cover data does not replace saved appearance", async () =>
        {
            var manager = new InstanceManager(Area("bad-cover"));
            var instance = manager.CreateInstance("Cover", "1.21.1");
            var path = Path.Combine(Area("bad-cover-source"), "not-image.png");
            File.WriteAllText(path, "not a picture");
            await Throws<InvalidDataException>(() => { manager.SaveAppearance(instance, path, "#507EAA"); return Task.CompletedTask; });
            Require(manager.GetInstance(instance.Id)!.CoverPath == null);
        });
        await check("Favorite servers preserve the chosen instance and recover the last backup", () =>
        {
            var dir = Area("servers-store");
            var store = new FavoriteServers(dir);
            var first = new FavoriteServer("id", "С друзьями", "example.org", 25565, "fabric-instance");
            store.Save([first]);
            Require(store.Load().Single().InstanceId == "fabric-instance");
            store.Save([first with { InstanceId = "forge-instance" }]);
            File.WriteAllText(Path.Combine(dir, "servers.json"), "{");
            Require(store.Load().Single() == first);
            return Task.CompletedTask;
        });
        await check("Java server status handles real packet framing, structured MOTD and pong", async () =>
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var peer = Task.Run(async () =>
            {
                using var client = await listener.AcceptTcpClientAsync(timeout.Token);
                await using var stream = client.GetStream();
                var handshake = await ServerStatusClient.ReadPacketAsync(stream, timeout.Token);
                using var fields = new MemoryStream(handshake);
                Require(await ServerStatusClient.ReadVarIntAsync(fields, timeout.Token) == 0);
                Require(await ServerStatusClient.ReadVarIntAsync(fields, timeout.Token) == -1);
                Require((await ServerStatusClient.ReadPacketAsync(stream, timeout.Token)).SequenceEqual(new byte[] { 0 }));
                var json = Encoding.UTF8.GetBytes("{\"version\":{\"name\":\"1.21.1\"},\"players\":{\"online\":3,\"max\":20},\"description\":{\"text\":\"§aHello \",\"extra\":[{\"text\":\"world\"}]}}");
                using var packet = new MemoryStream();
                ServerStatusClient.WriteVarInt(packet, 0); ServerStatusClient.WriteVarInt(packet, json.Length); packet.Write(json);
                await ServerStatusClient.WritePacketAsync(stream, packet.ToArray(), timeout.Token);
                var ping = await ServerStatusClient.ReadPacketAsync(stream, timeout.Token);
                Require(ping.Length == 9 && ping[0] == 1);
                await ServerStatusClient.WritePacketAsync(stream, ping, timeout.Token);
            }, timeout.Token);
            var result = await new ServerStatusClient().QueryAsync("127.0.0.1", port, timeout.Token);
            await peer;
            Require(result.Version == "1.21.1" && result.Online == 3 && result.Maximum == 20 && result.Description == "Hello world");
        });
        await check("Server status rejects oversized packets and malformed VarInts", async () =>
        {
            using var size = new MemoryStream();
            ServerStatusClient.WriteVarInt(size, 262145); size.Position = 0;
            await Throws<InvalidDataException>(() => ServerStatusClient.ReadPacketAsync(size, default));
            using var invalid = new MemoryStream([0xff, 0xff, 0xff, 0xff, 0x7f]);
            await Throws<InvalidDataException>(() => ServerStatusClient.ReadVarIntAsync(invalid, default));
        });
        await check("Server status cancellation closes an unresponsive connection", async () =>
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var cancel = new CancellationTokenSource();
            var query = new ServerStatusClient().QueryAsync("127.0.0.1", port, cancel.Token);
            using var peer = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(3));
            cancel.Cancel();
            await Throws<OperationCanceledException>(() => query);
        });
    }

    private static GameInstance Instance() => new() { McVersion = "1.21.1", Loader = LoaderType.Fabric, LoaderVersion = "0.16.0" };
    private static string Hash(string path) => Convert.ToHexString(SHA1.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static string MakeJar(string gameDir, string name, string metadata, string entry = "fabric.mod.json")
    {
        Directory.CreateDirectory(Path.Combine(gameDir, "mods"));
        var path = Path.Combine(gameDir, "mods", name + ".jar");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using var writer = new StreamWriter(zip.CreateEntry(entry).Open());
        writer.Write(metadata);
        return path;
    }
    private static void Require(bool value) { if (!value) throw new Exception("Feature assertion failed."); }
    private static async Task Throws<T>(Func<Task> action) where T : Exception
    {
        try { await action(); } catch (T) { return; }
        throw new Exception($"Expected {typeof(T).Name}");
    }
    private sealed class StallingStream(TaskCompletionSource started) : MemoryStream
    {
        private bool _sent;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_sent) { _sent = true; buffer.Span[..1024].Fill(1); return 1024; }
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
    }
}
