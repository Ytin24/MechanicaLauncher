using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MechanicaLauncher.Core.Game;
using MechanicaLauncher.Core.Instances;
using MechanicaLauncher.Core.IO;
using MechanicaLauncher.Core.Models;
using MechanicaLauncher.Core.Mods;
using MechanicaLauncher.Core.Profiles;

if (args.FirstOrDefault() == "--smoke")
    return await SmokeTests.RunAsync(args.Skip(1).ToArray());
if (args.FirstOrDefault() == "--modpack-smoke")
    return await ModpackSmokeTests.RunAsync(args.Skip(1).ToArray());
if (args.FirstOrDefault() == "--mod-updates-smoke")
    return await ModUpdateSmokeTests.RunAsync(args.Skip(1).ToArray());
if (args.FirstOrDefault() == "--discord-smoke")
    return await DiscordLiveTest.RunAsync();

if (args.Length > 1 && args[0] == "-jar")
{
    Console.WriteLine("Installer stdout");
    Console.Error.WriteLine("Installer final stderr");
    Console.WriteLine($"PID:{Environment.ProcessId}");
    if (args[1].EndsWith("wait.jar")) await Task.Delay(Timeout.Infinite);
    return args[1].EndsWith("fail.jar") ? 7 : 0;
}

var testParent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "MechanicaLauncher.Tests"));
var testRoot = Path.Combine(testParent, Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(testRoot);
int passed = 0, failed = 0;

await Check("Valid cached file does not require network", async () =>
{
    var path = FilePath("cached", "client.jar");
    var data = Encoding.UTF8.GetBytes("cached client");
    await File.WriteAllBytesAsync(path, data);
    using var handler = new FakeHttp(_ => throw new Exception("Unexpected network request"));
    using var http = new HttpClient(handler);
    await FileDownloader.EnsureAsync(http, "https://example.test/client.jar", path, Hash(data), data.Length);
    Equal(0, handler.Calls);
});

await Check("Same-size damaged cache is replaced after hash verification", async () =>
{
    var path = FilePath("damaged", "client.jar");
    await File.WriteAllTextAsync(path, "wrong");
    var data = Encoding.UTF8.GetBytes("right");
    using var handler = new FakeHttp(_ => Response(data));
    using var http = new HttpClient(handler);
    await FileDownloader.EnsureAsync(http, "https://example.test/client.jar", path, Hash(data), data.Length);
    Equal("right", await File.ReadAllTextAsync(path));
    Equal(1, handler.Calls);
});

await Check("Wrong hash never replaces the original and retries are bounded", async () =>
{
    var path = FilePath("bad-hash", "client.jar");
    await File.WriteAllTextAsync(path, "original");
    using var handler = new FakeHttp(_ => Response("bad"u8.ToArray()));
    using var http = new HttpClient(handler);
    await Throws<InvalidDataException>(() => FileDownloader.EnsureAsync(http, "https://example.test/client.jar", path, Hash("good"u8.ToArray())));
    Equal("original", await File.ReadAllTextAsync(path));
    Equal(3, handler.Calls);
    Equal(0, Directory.GetFiles(Path.GetDirectoryName(path)!, "*.part").Length);
});

await Check("503 is retried and can recover", async () =>
{
    int request = 0;
    using var handler = new FakeHttp(_ => ++request == 1
        ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : Response("ok"u8.ToArray()));
    using var http = new HttpClient(handler);
    var path = FilePath("retry", "file.jar");
    await FileDownloader.EnsureAsync(http, "https://example.test/file.jar", path);
    Equal(2, handler.Calls);
    Equal("ok", await File.ReadAllTextAsync(path));
});

await Check("404 fails once without a completed file", async () =>
{
    using var handler = new FakeHttp(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
    using var http = new HttpClient(handler);
    var path = FilePath("404", "file.jar");
    await Throws<HttpRequestException>(() => FileDownloader.EnsureAsync(http, "https://example.test/file.jar", path));
    Equal(1, handler.Calls);
    Equal(false, File.Exists(path));
});

await Check("Truncated response is retried before publication", async () =>
{
    int request = 0;
    using var handler = new FakeHttp(_ =>
    {
        var response = Response("complete"u8.ToArray());
        if (++request == 1) response.Content.Headers.ContentLength = 100;
        return response;
    });
    using var http = new HttpClient(handler);
    var path = FilePath("truncated", "file.jar");
    await FileDownloader.EnsureAsync(http, "https://example.test/file.jar", path);
    Equal(2, handler.Calls);
    Equal("complete", await File.ReadAllTextAsync(path));
});

await Check("Cancellation preserves the cache without retries", async () =>
{
    using var handler = new FakeHttp(async (_, token) =>
    {
        await Task.Delay(Timeout.Infinite, token);
        return Response([]);
    });
    using var http = new HttpClient(handler);
    var path = FilePath("cancel", "file.jar");
    await File.WriteAllTextAsync(path, "old");
    using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
    await Throws<OperationCanceledException>(() => FileDownloader.EnsureAsync(http, "https://example.test/file.jar", path,
        Hash("new"u8.ToArray()), cancellationToken: cancellation.Token));
    Equal(1, handler.Calls);
    Equal("old", await File.ReadAllTextAsync(path));
    Equal(0, Directory.GetFiles(Path.GetDirectoryName(path)!, "*.part").Length);
});

await Check("Known empty files are accepted by hash", async () =>
{
    using var handler = new FakeHttp(_ => Response([]));
    using var http = new HttpClient(handler);
    var path = FilePath("empty", "file");
    await FileDownloader.EnsureAsync(http, "https://example.test/file", path, Hash([]));
    Equal(true, File.Exists(path));
});

await Check("Download paths cannot escape their root", async () =>
{
    foreach (var path in new[] { "../outside.jar", @"..\outside.jar", @"C:\outside.jar", "file.jar:stream" })
        await Throws<InvalidDataException>(() => Task.FromResult(FileDownloader.GetPath(testRoot, path)));
});

await Check("Last matching rule wins and OS/version restrictions apply", () =>
{
    Equal(true, LaunchRules.Evaluate([new() { Action = "disallow" }, new() { Action = "allow" }]));
    Equal(false, LaunchRules.Evaluate([new() { Action = "allow" }, new() { Action = "disallow" }]));
    Equal(false, LaunchRules.Evaluate([new() { Action = "allow", Os = new() { Name = "linux" } }]));
    Equal(false, LaunchRules.Evaluate([new() { Action = "allow", Os = new() { Version = "^6\\." } }], osVersion: "10.0.26100"));
    Equal(true, LaunchRules.Evaluate([new() { Action = "allow", Os = new() { Name = "windows", Version = "^10\\." } }], osVersion: "10.0.26100"));
    Equal(true, LaunchRules.Evaluate([]));
    return Task.CompletedTask;
});

await Check("Architecture and feature rules are evaluated consistently", () =>
{
    var rules = new List<Rule> { new() { Action = "allow", Os = new() { Arch = "x86" } } };
    Equal(false, LaunchRules.Evaluate(rules, architecture: Architecture.X64));
    Equal(true, LaunchRules.Evaluate(rules, architecture: Architecture.X86));
    rules = [new() { Action = "allow", Features = new() { ["is_demo_user"] = true } }];
    Equal(false, LaunchRules.Evaluate(rules));
    Equal(true, LaunchRules.Evaluate(rules, new Dictionary<string, bool> { ["is_demo_user"] = true }));
    Equal(RuntimeInformation.OSArchitecture == Architecture.X64,
        AssetDownloader.ShouldIncludeLibrary(new() { Name = "org.lwjgl:lwjgl:3:natives-windows" }));
    Equal(RuntimeInformation.OSArchitecture == Architecture.Arm64,
        AssetDownloader.ShouldIncludeLibrary(new() { Name = "org.lwjgl:lwjgl:3:natives-windows-arm64" }));
    return Task.CompletedTask;
});

await Check("Java 8 and modern release versions are parsed", () =>
{
    Equal(8, JavaFinder.ParseMajorVersion("1.8.0_452"));
    Equal(17, JavaFinder.ParseMajorVersion("17.0.12"));
    Equal(21, JavaFinder.ParseMajorVersion("21+35"));
    Equal(25, JavaFinder.ParseMajorVersion("25-ea"));
    Equal(0, JavaFinder.ParseMajorVersion("unknown"));
    return Task.CompletedTask;
});

await Check("Automatic Java selection matches the required major", () =>
{
    (string Path, int MajorVersion)[] installations = [("newest", 25), ("preferred", 17), ("legacy", 8)];
    Equal("legacy", JavaFinder.SelectJava(installations, "preferred", 8));
    Equal<string?>(null, JavaFinder.SelectJava(installations, null, 21));
    return Task.CompletedTask;
});

await Check("Wrong manual Java fails before launch", async () =>
{
    var java = FilePath("java-validation", "bin/javaw.exe");
    File.Copy(Environment.ProcessPath!, java);
    await File.WriteAllTextAsync(Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(java)!)!, "release"), "JAVA_VERSION=\"1.8.0_452\"");
    JavaFinder.ValidateJava(java, 8);
    await Throws<InvalidOperationException>(() => { JavaFinder.ValidateJava(java, 21); return Task.CompletedTask; });
});

await Check("Existing client does not hide missing libraries or legacy natives", async () =>
{
    var game = Path.GetDirectoryName(FilePath("repair-game", "placeholder"))!;
    var shared = Path.Combine(game, "shared");
    var clientPath = FilePath("repair-game", "versions/1.12.2/1.12.2.jar");
    await File.WriteAllTextAsync(clientPath, "client");
    var archive = NativeArchive("repaired native");
    var nativePath = FilePath("repair-game", "versions/1.12.2/natives/test.dll");
    await File.WriteAllTextAsync(nativePath, "wrong architecture");
    using var handler = new FakeHttp(request => Response(request.RequestUri!.AbsolutePath.EndsWith("native.jar")
        ? archive : "library"u8.ToArray()));
    using var http = new HttpClient(handler);
    var meta = new VersionMeta
    {
        Id = "1.12.2",
        Downloads = new() { ["client"] = new() { Url = "https://example.test/client.jar", Sha1 = Hash("client"u8.ToArray()) } },
        Libraries =
        [
            new() { Name = "test:library:1", Url = "https://example.test/" },
            new()
            {
                Name = "test:native:1", Natives = new() { ["windows"] = "natives-windows" },
                Downloads = new() { Classifiers = new() { ["natives-windows"] = new() { Path = "native.jar", Url = "https://example.test/native.jar", Sha1 = Hash(archive) } } }
            }
        ]
    };
    await new AssetDownloader(shared, game, http).DownloadVersionAsync(meta);
    Equal("repaired native", await File.ReadAllTextAsync(nativePath));
    Equal(2, handler.Calls);
    Equal(true, File.Exists(Path.Combine(shared, "libraries", "test", "library", "1", "library-1.jar")));
    await new AssetDownloader(shared, game, http).DownloadVersionAsync(meta);
    Equal(2, handler.Calls);
    var classpath = GameLauncher.BuildClasspath(meta, clientPath, Path.Combine(shared, "libraries"), true);
    Equal(false, classpath.Contains("native.jar"));
});

await Check("Missing classpath entries fail before starting Java", async () =>
{
    var meta = new VersionMeta { Libraries = [new() { Name = "test:missing:1", Url = "https://example.test/" }] };
    await Throws<FileNotFoundException>(() => Task.FromResult(GameLauncher.BuildClasspath(meta, "client.jar", testRoot, true)));
});

await Check("Fabric includes vanilla jar; BootstrapLauncher excludes it", async () =>
{
    var game = Path.GetDirectoryName(FilePath("launch-info", "placeholder"))!;
    var java = FilePath("launch-info", "javaw.exe");
    await File.WriteAllTextAsync(java, "test executable");
    var client = FilePath("launch-info", "versions/1.21.1/1.21.1.jar");
    await File.WriteAllTextAsync(client, "client");
    var meta = new VersionMeta { Id = "modded", InheritsFrom = "1.21.1", MainClass = "net.fabricmc.loader.impl.launch.knot.KnotClient" };
    var launcher = new GameLauncher(game, Path.Combine(game, "shared"));
    var start = launcher.CreateStartInfo(meta, java, "Player");
    Equal(true, start.ArgumentList.Contains(client));
    meta.MainClass = "cpw.mods.bootstraplauncher.BootstrapLauncher";
    start = launcher.CreateStartInfo(meta, java, "Player");
    Equal(false, start.ArgumentList.Contains(client));
});

await Check("Quoted JVM arguments preserve paths and reject incomplete quotes", async () =>
{
    var args = GameLauncher.SplitArguments("-Dpath=\"C:\\Program Files\\Java\" -Xmx4G");
    Equal(2, args.Count);
    Equal(@"-Dpath=C:\Program Files\Java", args[0]);
    Equal("-Xmx4G", args[1]);
    await Throws<ArgumentException>(() => Task.FromResult(GameLauncher.SplitArguments("-Dpath=\"unfinished")));
});

foreach (var loader in new[] { "Fabric", "Quilt" })
{
    await Check($"{loader} failure is not marked complete and a retry repairs it", async () =>
    {
        var game = Path.GetDirectoryName(FilePath(loader, "placeholder"))!;
        var shared = Path.Combine(game, "shared");
        var profile = new VersionMeta
        {
            Id = "test", MainClass = "loader.Main", InheritsFrom = "1.21.1",
            Libraries = [new() { Name = "test:first:1", Url = "https://example.test/" }, new() { Name = "test:second:1", Url = "https://example.test/" }]
        };
        bool failSecond = true;
        int firstDownloads = 0;
        using var handler = new FakeHttp(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/profile/json")) return Response(JsonSerializer.SerializeToUtf8Bytes(profile));
            if (path.Contains("/first/")) firstDownloads++;
            return failSecond && path.Contains("/second/")
                ? new HttpResponseMessage(HttpStatusCode.NotFound) : Response("jar"u8.ToArray());
        });
        using var http = new HttpClient(handler);
        Func<Task> install = loader == "Fabric"
            ? () => new FabricInstaller(shared, game, http).InstallAsync("1.21.1", "1")
            : () => new QuiltInstaller(shared, game, http).InstallAsync("1.21.1", "1");
        var versionId = $"{loader.ToLowerInvariant()}-loader-1-1.21.1";
        var versionDir = Path.Combine(game, "versions", versionId);
        await Throws<HttpRequestException>(install);
        Equal(false, File.Exists(Path.Combine(versionDir, $"{versionId}.json")));
        Equal(false, File.Exists(Path.Combine(versionDir, ".complete")));
        failSecond = false;
        await install();
        Equal(true, File.Exists(Path.Combine(versionDir, $"{versionId}.json")));
        Equal(true, File.Exists(Path.Combine(versionDir, ".complete")));
        Equal(1, firstDownloads);
    });
}

await Check("Cached version metadata is available without network", async () =>
{
    var shared = Path.GetDirectoryName(FilePath("offline-version", "placeholder"))!;
    var path = FilePath("offline-version", "versions/1.21.1/1.21.1.json");
    await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new VersionMeta { Id = "1.21.1", MainClass = "game.Main" }));
    using var handler = new FakeHttp(_ => throw new HttpRequestException("offline"));
    using var http = new HttpClient(handler);
    var meta = await new VersionManager(shared, http).GetVersionMetaAsync("1.21.1");
    Equal("1.21.1", meta.Id);
    Equal(0, handler.Calls);
});

await Check("Corrupt version cache is repaired after successful parsing", async () =>
{
    var shared = Path.GetDirectoryName(FilePath("metadata-repair", "placeholder"))!;
    var path = FilePath("metadata-repair", "versions/1.21.1/1.21.1.json");
    await File.WriteAllTextAsync(path, "{");
    using var handler = new FakeHttp(_ => Response(JsonSerializer.SerializeToUtf8Bytes(new VersionMeta { Id = "1.21.1", MainClass = "game.Main" })));
    using var http = new HttpClient(handler);
    var meta = await new VersionManager(shared, http).GetVersionMetaAsync(new VersionEntry { Id = "1.21.1", Url = "https://example.test/version.json" });
    Equal("game.Main", meta.MainClass);
    Equal("1.21.1", JsonSerializer.Deserialize<VersionMeta>(await File.ReadAllTextAsync(path))!.Id);
});

await Check("Invalid remote version does not overwrite an existing cache", async () =>
{
    var shared = Path.GetDirectoryName(FilePath("metadata-invalid", "placeholder"))!;
    var path = FilePath("metadata-invalid", "versions/1.21.1/1.21.1.json");
    await File.WriteAllTextAsync(path, "old malformed cache");
    using var handler = new FakeHttp(_ => Response("{}"u8.ToArray()));
    using var http = new HttpClient(handler);
    await Throws<InvalidDataException>(() => new VersionManager(shared, http).GetVersionMetaAsync(new VersionEntry { Id = "1.21.1", Url = "https://example.test/version.json" }));
    Equal("old malformed cache", await File.ReadAllTextAsync(path));
});

await Check("Instance backup restores corrupt metadata without touching worlds", async () =>
{
    var manager = new InstanceManager(Path.GetDirectoryName(FilePath("instance-backup", "placeholder"))!);
    var instance = manager.CreateInstance("Test", "1.21.1");
    var world = Path.Combine(manager.GetGameDir(instance.Id), "saves", "world.dat");
    await File.WriteAllTextAsync(world, "world content");
    instance.Name = "Renamed";
    manager.SaveInstance(instance);
    var path = Path.Combine(manager.GetInstanceDir(instance.Id), "instance.json");
    await File.WriteAllTextAsync(path, "{");
    Equal("Test", manager.GetInstance(instance.Id)!.Name);
    Equal(1, manager.GetAllInstances().Count);
    Equal("world content", await File.ReadAllTextAsync(world));
});

await Check("Installer process drains stdout and final stderr", async () =>
{
    var path = FilePath("process-ok", "installer.log");
    await LoaderInstaller.RunProcessAsync("ok.jar", testRoot, Environment.ProcessPath!, path, null, CancellationToken.None);
    var log = await File.ReadAllTextAsync(path);
    Equal(true, log.Contains("Installer stdout"));
    Equal(true, log.Contains("Installer final stderr"));
});

await Check("Installer failure retains the exit code and log", async () =>
{
    var path = FilePath("process-failed", "installer.log");
    await Throws<InvalidOperationException>(() => LoaderInstaller.RunProcessAsync("fail.jar", testRoot,
        Environment.ProcessPath!, path, null, CancellationToken.None));
    Equal(true, (await File.ReadAllTextAsync(path)).Contains("Installer final stderr"));
});

await Check("Cancelling an installer terminates its child process", async () =>
{
    var path = FilePath("process-cancelled", "installer.log");
    using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(1));
    await Throws<OperationCanceledException>(() => LoaderInstaller.RunProcessAsync("wait.jar", testRoot,
        Environment.ProcessPath!, path, null, cancellation.Token));
    var pidLine = (await File.ReadAllLinesAsync(path)).FirstOrDefault(line => line.StartsWith("PID:"));
    if (pidLine != null)
    {
        try
        {
            using var process = Process.GetProcessById(int.Parse(pidLine[4..]));
            Equal(true, process.HasExited);
        }
        catch (ArgumentException) { }
    }
});

await Check("Java architecture is read from the executable and damaged Java is rejected", async () =>
{
    var java = FilePath("java-architecture", "bin/javaw.exe");
    File.Copy(Environment.ProcessPath!, java);
    Equal<Architecture?>(RuntimeInformation.ProcessArchitecture, JavaFinder.GetArchitecture(java));
    await File.WriteAllTextAsync(Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(java)!)!, "release"), "JAVA_VERSION=\"21.0.1\"");
    await File.WriteAllTextAsync(java, "not a PE executable");
    Equal<Architecture?>(null, JavaFinder.GetArchitecture(java));
    await Throws<InvalidOperationException>(() => { JavaFinder.ValidateJava(java, 21); return Task.CompletedTask; });
});

await Check("Offline identity uses Minecraft's name-based UUID instead of zero", () =>
{
    Equal("b50ad385-829d-3141-a216-7e7d7539ba7f", GameLauncher.OfflineUuid("Notch"));
    Equal(false, GameLauncher.OfflineUuid("Player") == GameLauncher.OfflineUuid("OtherPlayer"));
    var game = Path.GetDirectoryName(FilePath("launch-info", "placeholder"))!;
    var info = new GameLauncher(game, testRoot).CreateStartInfo(
        new VersionMeta { Id = "1.21.1", MainClass = "net.minecraft.client.main.Main" }, Environment.ProcessPath!, "Notch");
    var uuidPosition = info.ArgumentList.IndexOf("--uuid") + 1;
    Equal(GameLauncher.OfflineUuid("Notch"), info.ArgumentList[uuidPosition]);
    return Task.CompletedTask;
});

await Check("Mojang logging configuration is verified and passed as one JVM argument", async () =>
{
    var game = Path.GetDirectoryName(FilePath("logging config", "placeholder"))!;
    var shared = Path.Combine(game, "shared");
    var config = "<Configuration/>"u8.ToArray();
    var meta = new VersionMeta
    {
        Id = "test", MainClass = "net.minecraft.client.main.Main",
        Logging = new() { ["client"] = new() { Argument = "-Dlog4j.configurationFile=${path}",
            File = new() { Id = "client.xml", Url = "https://example.test/log.xml", Sha1 = Hash(config), Size = config.Length } } }
    };
    using var handler = new FakeHttp(_ => Response(config));
    using var http = new HttpClient(handler);
    var downloader = new AssetDownloader(shared, game, http);
    await downloader.DownloadVersionAsync(meta);
    await downloader.DownloadVersionAsync(meta);
    Equal(1, handler.Calls);
    await File.WriteAllTextAsync(FilePath("logging config", "versions/test/test.jar"), "client");
    var info = new GameLauncher(game, shared).CreateStartInfo(meta, Environment.ProcessPath!, "Player");
    Equal(true, info.ArgumentList.Contains("-Dlog4j.configurationFile=" + Path.Combine(shared, "assets", "log_configs", "client.xml")));
});

await Check("A failed modpack import stays hidden and preserves existing worlds", async () =>
{
    var manager = new InstanceManager(Path.GetDirectoryName(FilePath("pack-failure", "placeholder"))!);
    var existing = manager.CreateInstance("Existing", "1.21.1");
    var world = Path.Combine(manager.GetGameDir(existing.Id), "saves", "world.dat");
    await File.WriteAllTextAsync(world, "my world");
    var pack = FilePath("pack-failure", "failed.mrpack");
    CreatePack(pack, [PackFile("mods/required.jar", "good"u8.ToArray())]);
    using var handler = new FakeHttp(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
    using var http = new HttpClient(handler);
    await Throws<IOException>(() => new ModpackInstaller(http).ImportAsync(pack, manager));
    Equal(1, manager.GetAllInstances().Count);
    Equal("my world", await File.ReadAllTextAsync(world));
    Equal(0, Directory.GetDirectories(Path.Combine(manager.InstancesDir, ".imports")).Length);
});

await Check("Modpack path escapes are rejected before downloading", async () =>
{
    var pack = FilePath("pack-escape", "bad.mrpack");
    CreatePack(pack, [PackFile("mods/test.jar", "good"u8.ToArray())], new() { ["overrides/../../outside.txt"] = "bad" });
    using var handler = new FakeHttp(_ => throw new Exception("Unexpected download"));
    using var http = new HttpClient(handler);
    await Throws<InvalidDataException>(() => new ModpackInstaller(http).InstallAsync(pack, Path.Combine(testRoot, "pack-escape", "game")));
    Equal(0, handler.Calls);
    Equal(false, File.Exists(Path.Combine(testRoot, "outside.txt")));
});

await Check("Modpack verifies hashes, skips server files and prioritizes client overrides", async () =>
{
    var pack = FilePath("pack-success", "test.mrpack");
    var data = "verified mod"u8.ToArray();
    CreatePack(pack, [PackFile("mods/test.jar", data), new { path = "mods/server.jar", env = new { client = "unsupported" } }],
        new() { ["overrides/config/test.txt"] = "base", ["client-overrides/config/test.txt"] = "client", ["server-overrides/server.txt"] = "server" });
    var manager = new InstanceManager(Path.Combine(testRoot, "pack-success", "data"));
    using var handler = new FakeHttp(_ => Response(data));
    using var http = new HttpClient(handler);
    var instance = await new ModpackInstaller(http).ImportAsync(pack, manager);
    var game = manager.GetGameDir(instance.Id);
    Equal(1, manager.GetAllInstances().Count);
    Equal("client", await File.ReadAllTextAsync(Path.Combine(game, "config", "test.txt")));
    Equal(false, File.Exists(Path.Combine(game, "mods", "server.jar")));
    Equal(false, File.Exists(Path.Combine(game, "server.txt")));
    Equal(1, handler.Calls);
    await File.WriteAllTextAsync(Path.Combine(game, "saves", "world.dat"), "my world");
    var exported = FilePath("pack-success", "exported.mrpack");
    await ModpackInstaller.ExportAsync(instance, manager, exported);
    var imported = await new ModpackInstaller(http).ImportAsync(exported, manager);
    Equal(2, manager.GetAllInstances().Count);
    Equal("my world", await File.ReadAllTextAsync(Path.Combine(manager.GetGameDir(imported.Id), "saves", "world.dat")));
    Equal("client", await File.ReadAllTextAsync(Path.Combine(manager.GetGameDir(imported.Id), "config", "test.txt")));
});

await Check("SHA512 mismatch cannot publish a file even when SHA1 matches", async () =>
{
    var data = "good"u8.ToArray();
    using var handler = new FakeHttp(_ => Response(data));
    using var http = new HttpClient(handler);
    var path = FilePath("sha512", "mod.jar");
    await Throws<InvalidDataException>(() => FileDownloader.EnsureAsync(http, "https://example.test/mod.jar", path,
        Hash(data), data.Length, sha512: new string('0', 128)));
    Equal(false, File.Exists(path));
});

await Check("Modpack roundtrip preserves custom client files without including unrelated game data", async () =>
{
    var pack = FilePath("pack-custom", "test.mrpack");
    var data = "custom content"u8.ToArray();
    var entries = new Dictionary<string, string>
    {
        ["overrides/kubejs/server_scripts/recipes.js"] = "recipes",
        ["overrides/scripts/recipes.zs"] = "crafttweaker",
        ["overrides/defaultconfigs/server.toml"] = "defaults",
        ["overrides/custom/settings.json"] = "common",
        ["client-overrides/custom/settings.json"] = "client",
        ["client-overrides/pack-settings.json"] = "root settings",
        ["server-overrides/server-only.txt"] = "server"
    };
    CreatePack(pack, [PackFile("custom/content.bin", data)], entries);
    var manager = new InstanceManager(Path.Combine(testRoot, "pack-custom", "data"));
    using var handler = new FakeHttp(_ => Response(data));
    using var http = new HttpClient(handler);
    var installer = new ModpackInstaller(http);
    var instance = await installer.ImportAsync(pack, manager);
    var game = manager.GetGameDir(instance.Id);
    await File.WriteAllTextAsync(Path.Combine(game, "launcher_accounts.json"), "private");
    var exported = FilePath("pack-custom", "exported.mrpack");
    await ModpackInstaller.ExportAsync(instance, manager, exported);
    var imported = await installer.ImportAsync(exported, manager);
    var restored = manager.GetGameDir(imported.Id);
    foreach (var path in new[] { "kubejs/server_scripts/recipes.js", "scripts/recipes.zs", "defaultconfigs/server.toml", "custom/settings.json", "pack-settings.json", "custom/content.bin" })
        Equal(await File.ReadAllTextAsync(Path.Combine(game, path)), await File.ReadAllTextAsync(Path.Combine(restored, path)));
    Equal(false, File.Exists(Path.Combine(restored, "launcher_accounts.json")));
    Equal(false, File.Exists(Path.Combine(restored, "server-only.txt")));
    Equal(1, handler.Calls);
});

await Check("Locally created modpack exports scripts and defaults with its exact loader", async () =>
{
    var manager = new InstanceManager(Path.Combine(testRoot, "pack-local"));
    var instance = manager.CreateInstance("Local pack", "1.21.1", LoaderType.NeoForge, "21.1.250");
    foreach (var folder in new[] { "kubejs", "scripts", "defaultconfigs" })
    {
        var directory = Path.Combine(manager.GetGameDir(instance.Id), folder);
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "custom.txt"), folder);
    }
    var exported = FilePath("pack-local", "exported.mrpack");
    await ModpackInstaller.ExportAsync(instance, manager, exported);
    var imported = await new ModpackInstaller().ImportAsync(exported, manager);
    Equal(LoaderType.NeoForge, imported.Loader);
    Equal("21.1.250", imported.LoaderVersion);
    foreach (var folder in new[] { "kubejs", "scripts", "defaultconfigs" })
        Equal(folder, await File.ReadAllTextAsync(Path.Combine(manager.GetGameDir(imported.Id), folder, "custom.txt")));
});

await Check("Export cannot silently turn an unresolved mod loader into vanilla", async () =>
{
    var manager = new InstanceManager(Path.Combine(testRoot, "pack-unresolved-loader"));
    var instance = manager.CreateInstance("Unresolved", "1.21.1", LoaderType.Fabric);
    var output = FilePath("pack-unresolved-loader", "previous.mrpack");
    await File.WriteAllTextAsync(output, "previous export");
    await Throws<InvalidOperationException>(() => ModpackInstaller.ExportAsync(instance, manager, output));
    Equal("previous export", await File.ReadAllTextAsync(output));
});

await Check("Cancelling an active modpack export preserves the previous archive", async () =>
{
    var manager = new InstanceManager(Path.Combine(testRoot, "pack-export-cancel"));
    var instance = manager.CreateInstance("Cancel", "1.21.1");
    var data = new byte[32 * 1024 * 1024];
    RandomNumberGenerator.Fill(data);
    await File.WriteAllBytesAsync(Path.Combine(manager.GetGameDir(instance.Id), "mods", "large.jar"), data);
    var output = FilePath("pack-export-cancel", "previous.mrpack");
    await File.WriteAllTextAsync(output, "previous export");
    using var cancellation = new CancellationTokenSource();
    var exporting = ModpackInstaller.ExportAsync(instance, manager, output, cancellation.Token);
    Equal(false, exporting.IsCompleted);
    cancellation.Cancel();
    await Throws<OperationCanceledException>(() => exporting);
    Equal("previous export", await File.ReadAllTextAsync(output));
    Equal(0, Directory.GetFiles(Path.GetDirectoryName(output)!, "*.part").Length);
});

await Check("Pinned recursive mod dependencies and cycles install exactly once", async () =>
{
    var root = ModVersion("root");
    var dependency = ModVersion("pinned");
    var leaf = ModVersion("leaf");
    root.Dependencies.Add(new() { VersionId = dependency.Id, DependencyType = "required" });
    dependency.Dependencies.Add(new() { VersionId = leaf.Id, DependencyType = "required" });
    leaf.Dependencies.Add(new() { VersionId = root.Id, DependencyType = "required" });
    var versions = new[] { root, dependency, leaf }.ToDictionary(v => v.Id);
    using var handler = new FakeHttp(request =>
    {
        var path = request.RequestUri!.AbsolutePath;
        if (path.StartsWith("/v2/version/")) return Response(JsonSerializer.SerializeToUtf8Bytes(versions[path.Split('/').Last()]));
        return Response(Encoding.UTF8.GetBytes(Path.GetFileNameWithoutExtension(path)));
    });
    using var http = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
    var mods = Path.Combine(testRoot, "mod-dependencies");
    await new ModInstaller(http, new ModrinthClient(http)).InstallModAsync(root, mods, "1.21.1", "fabric");
    Equal(3, Directory.GetFiles(mods, "*.jar").Length);
    Equal("pinned", await File.ReadAllTextAsync(Path.Combine(mods, "pinned.jar")));
});

await Check("Required mod download failure does not publish a partial set", async () =>
{
    var root = ModVersion("root");
    var dependency = ModVersion("dep");
    root.Dependencies.Add(new() { VersionId = dependency.Id, DependencyType = "required" });
    using var handler = new FakeHttp(request => request.RequestUri!.AbsolutePath switch
    {
        "/v2/version/dep" => Response(JsonSerializer.SerializeToUtf8Bytes(dependency)),
        "/dep.jar" => Response("dep"u8.ToArray()),
        _ => new HttpResponseMessage(HttpStatusCode.NotFound)
    });
    using var http = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
    var mods = Path.Combine(testRoot, "mod-failed-dependency");
    await Throws<HttpRequestException>(() => new ModInstaller(http, new ModrinthClient(http)).InstallModAsync(root, mods));
    Equal(0, Directory.GetFiles(mods, "*.jar").Length);
});

await Check("Custom data directory isolates settings and recovers their backup", () =>
{
    var previous = Environment.GetEnvironmentVariable("MECHANICA_DATA_DIR");
    var dir = Path.Combine(testRoot, "isolated-data");
    try
    {
        Environment.SetEnvironmentVariable("MECHANICA_DATA_DIR", dir);
        new LauncherSettings { Username = "First" }.Save();
        new LauncherSettings { Username = "Second" }.Save();
        File.WriteAllText(Path.Combine(dir, "settings.json"), "{");
        Equal("First", LauncherSettings.Load().Username);
        Equal(Path.Combine(dir, "instances"), new InstanceManager().InstancesDir);
    }
    finally { Environment.SetEnvironmentVariable("MECHANICA_DATA_DIR", previous); }
    return Task.CompletedTask;
});

await Check("Quilt defaults to the newest stable version regardless of API order", () =>
{
    var versions = QuiltInstaller.OrderVersions(["0.20.0-beta.9", "0.29.2-beta.4", "0.28.0", "0.29.1", "0.29.1"]);
    Equal("0.29.1", versions[0]);
    Equal(4, versions.Count);
    return Task.CompletedTask;
});

await Check("Cancelled metadata requests preserve the existing manifest cache", async () =>
{
    var shared = Path.Combine(testRoot, "cancelled-manifest");
    Directory.CreateDirectory(shared);
    var path = Path.Combine(shared, "version_manifest.json");
    await File.WriteAllTextAsync(path, "original manifest");
    using var handler = new FakeHttp(async (_, token) => { await Task.Delay(Timeout.Infinite, token); return Response([]); });
    using var http = new HttpClient(handler);
    using var cancellation = new CancellationTokenSource(50);
    await Throws<OperationCanceledException>(() => new VersionManager(shared, http).GetManifestAsync(cancellation.Token));
    Equal("original manifest", await File.ReadAllTextAsync(path));
});

await Check("Failed export preserves the previously exported file", async () =>
{
    var manager = new InstanceManager(Path.Combine(testRoot, "export-failure"));
    var instance = manager.CreateInstance("Export", "1.21.1");
    var locked = Path.Combine(manager.GetGameDir(instance.Id), "config", "locked.txt");
    await File.WriteAllTextAsync(locked, "data");
    using var handle = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None);
    var output = Path.Combine(testRoot, "previous.mrpack");
    await File.WriteAllTextAsync(output, "previous export");
    await Throws<IOException>(() => ModpackInstaller.ExportAsync(instance, manager, output));
    Equal("previous export", await File.ReadAllTextAsync(output));
});

await Check("Modrinth search keeps encoded filters and cancels the underlying request", async () =>
{
    using var cancellation = new CancellationTokenSource();
    using var handler = new FakeHttp(async (request, ct) =>
    {
        var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query);
        Equal("shader & water", query["query"]);
        var facets = JsonSerializer.Deserialize<string[][]>(query["facets"]!)!;
        Equal("project_type:shader", facets[0][0]);
        Equal("versions:1.21.1", facets[1][0]);
        cancellation.Cancel();
        await Task.Delay(Timeout.Infinite, ct);
        throw new Exception("Cancelled search must not finish");
    });
    using var http = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
    await Throws<OperationCanceledException>(() => new ModrinthClient(http).SearchAsync("shader & water", "1.21.1",
        projectType: "shader", cancellationToken: cancellation.Token));
    Equal(1, handler.Calls);
});

await Check("Server launch selects Quick Play from metadata and keeps bridge secrets out of arguments", async () =>
{
    string game = Path.Combine(testRoot, "quickplay");
    await File.WriteAllTextAsync(FilePath("quickplay", "versions/test/test.jar"), "client");
    var meta = new VersionMeta { Id = "test", MainClass = "game.Main" };
    var launcher = new GameLauncher(game, testRoot);
    var legacy = launcher.CreateStartInfo(meta, Environment.ProcessPath!, "Player", server: "localhost", port: 25566);
    Equal("localhost", legacy.ArgumentList[legacy.ArgumentList.IndexOf("--server") + 1]);
    Equal("25566", legacy.ArgumentList[legacy.ArgumentList.IndexOf("--port") + 1]);
    meta.Arguments = JsonSerializer.Deserialize<VersionMeta>("""
        {"arguments":{"game":[{"rules":[{"action":"allow","features":{"is_quick_play_multiplayer":true}}],"value":["--quickPlayMultiplayer","${quickPlayMultiplayer}"]}],"jvm":[]}}
        """)!.Arguments;
    var modern = launcher.CreateStartInfo(meta, Environment.ProcessPath!, "Player", server: "::1", port: 25566,
        environment: new Dictionary<string, string> { ["MECHANICA_BRIDGE_TOKEN"] = "private-token" });
    Equal("[::1]:25566", modern.ArgumentList[modern.ArgumentList.IndexOf("--quickPlayMultiplayer") + 1]);
    Equal(false, modern.ArgumentList.Contains("--server"));
    Equal(false, modern.ArgumentList.Any(arg => arg.Contains("private-token")));
    Equal("private-token", modern.Environment["MECHANICA_BRIDGE_TOKEN"]);
    var menu = launcher.CreateStartInfo(meta, Environment.ProcessPath!, "Player");
    Equal(false, menu.ArgumentList.Contains("--quickPlayMultiplayer"));
});

await AuthTests.RunAsync(Check);
await GameBridgeTests.RunAsync(Check);
await ServerDiscoveryTests.RunAsync(Check);
await ServerModSyncTests.RunAsync(Check, testRoot);
await ModUpdateTests.RunAsync(Check, testRoot);
await ModpackPrivateStateTests.RunAsync(Check, testRoot);
await NeoForgeVersionTests.RunAsync(Check, testRoot);
await GameLibraryMergeTests.RunAsync(Check, testRoot);
await ModLoaderCompatibilityTests.RunAsync(Check, testRoot);
await CatalogTests.RunAsync(Check, testRoot);
await InstalledContentIndexTests.RunAsync(Check, testRoot);
await FeatureTests.RunAsync(Check, testRoot);
await DiscordTests.RunAsync(Check);
await TLauncherTests.RunAsync(Check, testRoot);

Console.WriteLine($"{passed} passed, {failed} failed.");
if (failed == 0 && Path.GetFullPath(testRoot).StartsWith(testParent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
    Directory.Delete(testRoot, recursive: true);
else
    Console.WriteLine($"Fixtures: {testRoot}");
return failed == 0 ? 0 : 1;

async Task Check(string name, Func<Task> action)
{
    try { await action(); passed++; Console.WriteLine($"PASS {name}"); }
    catch (Exception ex) { failed++; Console.WriteLine($"FAIL {name}\n{ex}"); }
}

object PackFile(string path, byte[] data) => new
{
    path, hashes = new { sha1 = Hash(data), sha512 = Convert.ToHexString(SHA512.HashData(data)).ToLowerInvariant() },
    fileSize = data.Length, downloads = new[] { "https://example.test/" + path }
};

void CreatePack(string path, object[] files, Dictionary<string, string>? entries = null)
{
    using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
    using (var writer = new StreamWriter(zip.CreateEntry("modrinth.index.json").Open()))
        writer.Write(JsonSerializer.Serialize(new { formatVersion = 1, game = "minecraft", name = "Test pack", versionId = "1",
            dependencies = new Dictionary<string, string> { ["minecraft"] = "1.21.1" }, files }));
    foreach (var (name, value) in entries ?? [])
    {
        using var writer = new StreamWriter(zip.CreateEntry(name).Open());
        writer.Write(value);
    }
}

ModrinthVersion ModVersion(string id) => new()
{
    Id = id, ProjectId = id, Name = id, GameVersions = ["1.21.1"], Loaders = ["fabric"],
    Files = [new() { Filename = id + ".jar", Url = "https://example.test/" + id + ".jar", Primary = true,
        Size = id.Length, Hashes = new() { ["sha1"] = Hash(Encoding.UTF8.GetBytes(id)) } }]
};

string FilePath(string scenario, string relativePath)
{
    var path = Path.Combine(testRoot, scenario, relativePath.Replace('/', Path.DirectorySeparatorChar));
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    return path;
}

static string Hash(byte[] data) => Convert.ToHexString(SHA1.HashData(data));
static HttpResponseMessage Response(byte[] data) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(data) };
static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}");
}

static async Task Throws<T>(Func<Task> action) where T : Exception
{
    try { await action(); }
    catch (T) { return; }
    throw new Exception($"Expected {typeof(T).Name}");
}

static byte[] NativeArchive(string content)
{
    using var stream = new MemoryStream();
    using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
    using (var writer = new StreamWriter(zip.CreateEntry("test.dll").Open()))
        writer.Write(content);
    return stream.ToArray();
}

sealed class FakeHttp : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;
    public int Calls { get; private set; }
    public FakeHttp(Func<HttpRequestMessage, HttpResponseMessage> handler) : this((request, _) => Task.FromResult(handler(request))) { }
    public FakeHttp(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) => _handler = handler;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Calls++;
        return _handler(request, cancellationToken);
    }
}
