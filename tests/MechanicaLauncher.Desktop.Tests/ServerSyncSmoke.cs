using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
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
    private static int ServerSyncSmoke(string[] args, Pump context)
    {
        string Value(string name, string fallback)
        {
            int index = Array.IndexOf(args, name);
            return index < 0 ? fallback : index + 1 < args.Length ? args[index + 1] : throw new ArgumentException("Missing " + name);
        }
        string Argument(string name)
        {
            int index = Array.IndexOf(args, name);
            if (index < 0 || index + 1 >= args.Length) throw new ArgumentException("Missing " + name);
            return Path.GetFullPath(args[index + 1]);
        }
        if (!args.Contains("--accept-eula")) throw new ArgumentException("The real server smoke requires --accept-eula.");
        string root = Argument("--smoke-root"), serverDir = Argument("--server-dir"), shared = Argument("--shared-cache");
        string java = Argument("--java"), bridge = Argument("--bridge"), fixture = Argument("--fixture");
        string minecraft = Value("--minecraft", "1.21.1"), loaderVersion = Value("--loader-version", "0.19.3");
        var loader = Enum.Parse<LoaderType>(Value("--loader", "Fabric"));
        string[] serverArguments = args.Contains("--server-arguments")
            ? JsonSerializer.Deserialize<string[]>(File.ReadAllText(Argument("--server-arguments"))) ?? throw new InvalidDataException("Missing server arguments.")
            : ["-jar", "fabric-server-launch.jar", "nogui"];
        if (loader == LoaderType.None || serverArguments.Length == 0) throw new ArgumentException("The bridge smoke requires a mod loader and server arguments.");
        string data = Path.Combine(root, "data"), serverLog = Path.Combine(root, "server-console.log");
        string resultFile = Path.Combine(root, "result.json");
        string descriptorFile = Path.Combine(root, "served-descriptor.json");
        if (!serverDir.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The server directory must be inside the isolated smoke root.");
        if (File.Exists(resultFile) || Directory.Exists(Path.Combine(data, "instances")) || Directory.Exists(Path.Combine(serverDir, "smoke-world")))
            throw new ArgumentException("The smoke requires a fresh client and server world.");
        foreach (string file in new[] { java, bridge, fixture })
            if (!File.Exists(file)) throw new FileNotFoundException("Smoke prerequisite is missing.", file);
        if ((File.GetAttributes(Path.Combine(data, "shared")) & FileAttributes.ReparsePoint) != 0)
            throw new ArgumentException("The smoke shared directory must be local so staged mods stay inside the smoke root.");
        foreach (string directory in new[] { "assets", "libraries", "versions" })
        {
            var sharedLink = new DirectoryInfo(Path.Combine(data, "shared", directory)).ResolveLinkTarget(true);
            if (sharedLink == null || !Path.TrimEndingDirectorySeparator(sharedLink.FullName).Equals(Path.Combine(shared, directory), StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("The smoke shared cache junction is missing or points to another cache: " + directory);
        }

        Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("MECHANICA_DATA_DIR", data);
        var launches = new ConcurrentQueue<ServerSmokeLaunch>();
        var exits = new ConcurrentQueue<ServerSmokeExit>();
        var eventErrors = new ConcurrentQueue<string>();
        var ownedClients = new ConcurrentDictionary<int, Process>();
        var serverLines = new ConcurrentQueue<string>();
        var instances = new InstanceManager(data);
        using var sessions = new GameSessions(new LauncherSettings { Username = "SyncSmoke", AuthMode = "offline", DiscordRpc = false }, instances);
        Process? server = null;
        TextWriter? serverWriter = null;
        Exception? failure = null;
        int? serverExitCode = null;
        int gamePort = FreeServerSmokePort(), httpPort;
        do { httpPort = FreeServerSmokePort(); } while (httpPort == gamePort);
        string baseUrl = "http://127.0.0.1:" + httpPort;
        string descriptorUrl = baseUrl + "/mechanica/descriptor.json";
        string instanceId = "", installed = "", gameDir = "";
        string fixtureName = Path.GetFileName(fixture);
        string fixtureHash = ServerSmokeHash(fixture);
        int initialChecks = checks;
        int fileRequestsAfterSync = 0;

        void Until(Func<bool> predicate, string operation, int seconds = 180, bool requireServer = true)
        {
            var timer = Stopwatch.StartNew();
            while (!predicate())
            {
                context.Drain();
                if (!eventErrors.IsEmpty) throw new InvalidOperationException(string.Join("\n", eventErrors));
                if (sessions.Log.Contains("MECHANICA_SYNC_PREPARATION_FAILED", StringComparison.Ordinal))
                    throw new InvalidOperationException("The client bridge rejected server preparation. See " + Path.Combine(gameDir, "logs", "launcher-latest.log"));
                if (exits.FirstOrDefault(exit => exit.Code != 0) is { } failed)
                    throw new InvalidOperationException($"Minecraft exited with {failed.Code}. See {failed.LogPath}");
                if (requireServer && server?.HasExited == true) throw new InvalidOperationException("Server exited during " + operation + ": " + server.ExitCode);
                if (launches.TryPeek(out var first) && installed.Length > 0 && File.Exists(installed) &&
                    ownedClients.TryGetValue(first.Pid, out var original) && !original.HasExited)
                    throw new InvalidOperationException("The fixture was installed while the original game process was still running.");
                if (timer.Elapsed.TotalSeconds > seconds)
                    throw new TimeoutException(operation + "; launcher status: " + sessions.Status + "; starts=" + launches.Count + "; exits=" + exits.Count);
                Thread.Sleep(25);
            }
            context.Drain();
        }
        void Await(Task task, string operation, int seconds = 180)
        {
            Until(() => task.IsCompleted, operation, seconds);
            task.GetAwaiter().GetResult();
        }
        int FileRequests() => serverLines.Count(line => line.Contains("MECHANICA_SYNC_FILE_REQUEST", StringComparison.Ordinal));
        void Started(string id, int pid)
        {
            if (id != instanceId) { eventErrors.Enqueue("An unrelated instance was started by the smoke session."); return; }
            try
            {
                var process = Process.GetProcessById(pid);
                _ = process.Handle;
                ownedClients[pid] = process;
                launches.Enqueue(new(pid, DateTimeOffset.UtcNow, File.Exists(installed)));
                Console.WriteLine("CLIENT_STARTED pid=" + pid + " fixture=" + File.Exists(installed));
            }
            catch (Exception ex) { eventErrors.Enqueue("Could not retain the owned client process: " + ex); }
        }
        void Exited(string id, int pid, int code)
        {
            if (id != instanceId) { eventErrors.Enqueue("An unrelated instance exited in the smoke session."); return; }
            try
            {
                string log = File.ReadAllText(Path.Combine(gameDir, "logs", "launcher-latest.log"));
                string saved = Path.Combine(root, "client-" + pid + ".log");
                File.WriteAllText(saved, log, new UTF8Encoding(false));
                exits.Enqueue(new(pid, code, DateTimeOffset.UtcNow, saved));
                Console.WriteLine("CLIENT_EXITED pid=" + pid + " exit=" + code);
            }
            catch (Exception ex) { eventErrors.Enqueue("Could not preserve the exited client log: " + ex); }
        }
        sessions.ProcessStarted += Started;
        sessions.ProcessExited += Exited;
        try
        {
            Directory.CreateDirectory(Path.Combine(serverDir, "config"));
            Directory.CreateDirectory(Path.Combine(serverDir, "mods"));
            Directory.CreateDirectory(Path.Combine(serverDir, "client-pack"));
            File.Copy(bridge, Path.Combine(serverDir, "mods", Path.GetFileName(bridge)));
            File.Copy(fixture, Path.Combine(serverDir, "mods", fixtureName));
            File.Copy(fixture, Path.Combine(serverDir, "client-pack", fixtureName));
            File.WriteAllText(Path.Combine(serverDir, "eula.txt"), "eula=true\n", new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(serverDir, "server.properties"),
                $"server-ip=127.0.0.1\nserver-port={gamePort}\nonline-mode=false\nenforce-secure-profile=false\n" +
                "level-name=smoke-world\nlevel-type=minecraft:flat\nlevel-seed=0\ngenerate-structures=false\n" +
                "view-distance=2\nsimulation-distance=2\nspawn-protection=0\nmax-players=2\nwhite-list=false\n" +
                "enable-rcon=false\nenable-query=false\nallow-nether=false\nmotd=Mechanica sync smoke\n", new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(serverDir, "config", "mechanica-sync.json"), JsonSerializer.Serialize(new
            {
                serverId = Guid.NewGuid(), revision = 1, expiresUtc = DateTime.UtcNow.AddDays(1),
                targetId = loader.ToString().ToLowerInvariant() + "-" + minecraft, minecraft, loader = loader.ToString().ToLowerInvariant(), loaderVersion,
                baseUrl, bindAddress = "127.0.0.1", port = httpPort,
                files = new[] { new { artifactId = "mechanica-sync-fixture", modIds = new[] { "mechanica_sync_fixture" },
                    client = "required", filename = fixtureName, dependencies = Array.Empty<string>() } }
            }, new JsonSerializerOptions { WriteIndented = true }));

            var start = new ProcessStartInfo(java)
            {
                WorkingDirectory = serverDir, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            foreach (string argument in new[] { "-Xms256M", "-Xmx1536M" }.Concat(serverArguments)) start.ArgumentList.Add(argument);
            serverWriter = TextWriter.Synchronized(new StreamWriter(serverLog, false, new UTF8Encoding(false)) { AutoFlush = true });
            void AppendServer(string? line)
            {
                if (line == null) return;
                serverLines.Enqueue(line);
                serverWriter.WriteLine(line);
                if (line.Contains("MECHANICA_SYNC_", StringComparison.Ordinal) || line.Contains("Done (", StringComparison.Ordinal)) Console.WriteLine("SERVER " + line);
            }
            server = Process.Start(start) ?? throw new InvalidOperationException("Could not start the isolated " + loader + " server.");
            Console.WriteLine("SERVER_STARTED pid=" + server.Id + " gamePort=" + gamePort + " httpPort=" + httpPort);
            server.OutputDataReceived += (_, e) => AppendServer(e.Data);
            server.ErrorDataReceived += (_, e) => AppendServer(e.Data);
            server.BeginOutputReadLine(); server.BeginErrorReadLine();
            Until(() => serverLines.Any(line => line.Contains("Done (", StringComparison.Ordinal)) &&
                serverLines.Any(line => line.Contains("MECHANICA_SYNC_SERVER_READY", StringComparison.Ordinal)), "waiting for the real " + loader + " server and its HTTP publisher");
            Check(serverLines.Any(line => line.Contains("MECHANICA_SYNC_FIXTURE_INIT side=SERVER", StringComparison.Ordinal)), "real server loaded the fixture mod");
            using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) })
            {
                var fetch = http.GetStringAsync(descriptorUrl);
                Await(fetch, "reading the server mod descriptor");
                File.WriteAllText(descriptorFile, fetch.Result, new UTF8Encoding(false));
            }
            Check(FileRequests() == 0, "the fixture was not downloaded during preparation");

            var instance = instances.CreateInstance("Server sync smoke", minecraft, loader, loaderVersion);
            instance.UseServerModSync = true;
            instanceId = instance.Id;
            instance.JavaPath = java; instance.MinMemoryMb = 512; instance.MaxMemoryMb = 2048;
            instance.WindowWidth = 840; instance.WindowHeight = 620;
            instance.JvmArgs = "-Dmechanica.smoke=true";
            if (minecraft == "1.16.5")
            {
                // Authlib 2.1.28 otherwise drops --server for the synthetic offline token.
                string offlineEndpoint = "http://127.0.0.1:" + FreeServerSmokePort();
                foreach (string host in new[] { "auth", "account", "session", "services" })
                    instance.JvmArgs += " -Dminecraft.api." + host + ".host=" + offlineEndpoint;
            }
            instances.SaveInstance(instance);
            gameDir = instances.GetGameDir(instanceId);
            if (args.Contains("--client-jar"))
            {
                string versionDirectory = Path.Combine(gameDir, "versions", minecraft);
                Directory.CreateDirectory(versionDirectory);
                File.Copy(Argument("--client-jar"), Path.Combine(versionDirectory, minecraft + ".jar"));
            }
            string mods = Path.Combine(gameDir, "mods");
            installed = Path.Combine(mods, fixtureName);
            string sentinelMod = Path.Combine(mods, "personal-sentinel.jar.disabled");
            using (var archive = ZipFile.Open(sentinelMod, ZipArchiveMode.Create))
            using (var writer = new StreamWriter(archive.CreateEntry("fabric.mod.json").Open(), new UTF8Encoding(false)))
                writer.Write("{\"schemaVersion\":1,\"id\":\"mechanica_personal_smoke\",\"version\":\"1.0.0\",\"name\":\"Personal smoke sentinel\",\"environment\":\"client\"}");
            string sentinelHash = ServerSmokeHash(sentinelMod);
            string sentinelFile = Path.Combine(gameDir, "config", "personal-sentinel.txt");
            string sentinelText = Guid.NewGuid().ToString("N");
            File.WriteAllText(sentinelFile, sentinelText);
            File.WriteAllText(Path.Combine(gameDir, "options.txt"),
                "skipMultiplayerWarning:true\nonboardAccessibility:false\nrenderDistance:2\nsimulationDistance:2\n" +
                "maxFps:30\nenableVsync:false\nsoundCategory_master:0.0\npauseOnLostFocus:false\nfullscreen:false\n");
            new FavoriteServers(data).Save([new("sync-smoke", "Sync smoke", "127.0.0.1", gamePort, instanceId)
            {
                SyncManifestUrl = descriptorUrl, AutoSync = true, AllowLocalSync = true
            }]);
            Check(!File.Exists(installed), "fresh client deliberately starts without the required fixture");
            Await(sessions.LaunchAsync(instance, "127.0.0.1", gamePort, detail => Task.FromException<bool>(new InvalidOperationException("Unexpected compatibility confirmation: " + detail))),
                "preparing the first real client", 300);
            Until(() => exits.Count >= 2 && !sessions.IsBusy(instanceId), "missing mod, orderly restart, actual server join and client shutdown", 300);
            var firstLaunches = launches.ToArray();
            var firstExits = exits.ToArray();
            Check(firstLaunches.Length == 2 && firstExits.Length == 2, "missing server content causes exactly two real client launches");
            Check(firstLaunches[0].Pid != firstLaunches[1].Pid, "restarted Minecraft has a different process ID");
            Check(!firstLaunches[0].FixtureInstalled && firstLaunches[1].FixtureInstalled, "fixture is installed between the first and second JVM starts");
            Check(firstExits.All(exit => exit.Code == 0), "both clients exit normally with code zero");
            Check(firstExits[0].At <= firstLaunches[1].At, "the original JVM exits before the restarted JVM starts");
            string firstLog = File.ReadAllText(firstExits[0].LogPath), secondLog = File.ReadAllText(firstExits[1].LogPath);
            Check(!firstLog.Contains("MECHANICA_SYNC_FIXTURE_INIT side=CLIENT", StringComparison.Ordinal), "the first JVM did not load the missing fixture");
            Check(secondLog.Contains("MECHANICA_SYNC_FIXTURE_INIT side=CLIENT", StringComparison.Ordinal) &&
                secondLog.Contains("MECHANICA_SYNC_FIXTURE_CLIENT_JOIN", StringComparison.Ordinal), "the restarted game loads the real downloaded mod and joins");
            Check(serverLines.Any(line => line.Contains("MECHANICA_SYNC_FIXTURE_SERVER_JOIN", StringComparison.Ordinal)), "the real server confirms the player joined");
            Check(ServerSmokeHash(installed) == fixtureHash, "installed fixture SHA512 equals the server allowlisted JAR");
            Check(ServerSmokeHash(Path.Combine(mods, "mechanica-server-sync-1.0.0.jar")) == ServerSmokeHash(bridge),
                "the launcher installed the exact bridge package used by the tested server");
            Check(ServerSmokeHash(sentinelMod) == sentinelHash && File.ReadAllText(sentinelFile) == sentinelText, "personal mod and configuration survive synchronization");
            fileRequestsAfterSync = FileRequests();
            Check(fileRequestsAfterSync == 1, "the real server serves the fixture exactly once");

            DateTime installedWriteTime = File.GetLastWriteTimeUtc(installed);
            int joins = serverLines.Count(line => line.Contains("MECHANICA_SYNC_FIXTURE_SERVER_JOIN", StringComparison.Ordinal));
            Await(sessions.LaunchAsync(instance, "127.0.0.1", gamePort, detail => Task.FromException<bool>(new InvalidOperationException("Unexpected compatibility confirmation: " + detail))),
                "preparing a repeat join", 300);
            Until(() => exits.Count >= 3 && !sessions.IsBusy(instanceId), "repeat join and orderly shutdown", 240);
            var repeat = exits.Last();
            Check(launches.Count == 3 && exits.Count == 3, "repeat join needs one JVM with no synchronization restart");
            Check(repeat.Code == 0 && File.ReadAllText(repeat.LogPath).Contains("MECHANICA_SYNC_FIXTURE_CLIENT_JOIN", StringComparison.Ordinal), "repeat join succeeds and exits normally");
            Check(serverLines.Count(line => line.Contains("MECHANICA_SYNC_FIXTURE_SERVER_JOIN", StringComparison.Ordinal)) == joins + 1, "server observes exactly one repeat join");
            Check(FileRequests() == fileRequestsAfterSync && File.GetLastWriteTimeUtc(installed) == installedWriteTime &&
                ServerSmokeHash(installed) == fixtureHash, "repeat join neither downloads nor rewrites the already installed fixture");
            Check(ServerSmokeHash(sentinelMod) == sentinelHash && File.ReadAllText(sentinelFile) == sentinelText, "repeat join preserves personal files");
            instance = instances.GetInstance(instanceId)!;
            instance.UseServerModSync = false;
            instances.SaveInstance(instance);
            Await(sessions.LaunchAsync(instance, "127.0.0.1", gamePort, detail => Task.FromException<bool>(new InvalidOperationException("Unexpected compatibility confirmation: " + detail))),
                "preparing a join with server sync disabled", 300);
            Until(() => exits.Count >= 4 && !sessions.IsBusy(instanceId), "ordinary connection with server sync disabled", 240);
            string disabledLog = File.ReadAllText(exits.Last().LogPath);
            Check(launches.Count == 4 && exits.Last().Code == 0, "disabling server sync still starts and exits Minecraft normally");
            Check(disabledLog.Contains("MECHANICA_SYNC_FIXTURE_CLIENT_JOIN", StringComparison.Ordinal) &&
                !disabledLog.Contains("MECHANICA_SYNC_PREPARE", StringComparison.Ordinal), "disabled bridge does not intercept an ordinary connection");
            Check(FileRequests() == fileRequestsAfterSync && ServerSmokeHash(installed) == fixtureHash, "disabled server sync leaves server content unchanged");
            Check(eventErrors.IsEmpty, "all process events and complete per-launch logs were preserved");
            server.StandardInput.WriteLine("stop"); server.StandardInput.Flush();
            Until(() => server.HasExited, "stopping the isolated server", 60, false);
            server.WaitForExit();
            serverExitCode = server.ExitCode;
            Check(serverExitCode == 0, "the real server exits normally after stdin stop");
        }
        catch (Exception ex)
        {
            failure = ex;
            Console.Error.WriteLine("FAIL server sync smoke: " + ex);
            Console.Error.WriteLine("Launcher status: " + sessions.Status + "\n" + sessions.Log);
        }
        finally
        {
            sessions.Cancel();
            try { if (instanceId.Length > 0 && sessions.IsRunning(instanceId)) sessions.Stop(instanceId); }
            catch (Exception ex) { eventErrors.Enqueue("Client cleanup: " + ex.Message); }
            foreach (var process in ownedClients.Values)
            {
                try { if (!process.HasExited) { process.Kill(true); process.WaitForExit(10000); } }
                catch (Exception ex) { eventErrors.Enqueue("Owned client cleanup: " + ex.Message); }
                finally { process.Dispose(); }
            }
            if (server != null)
            {
                try
                {
                    if (!server.HasExited)
                    {
                        try { server.StandardInput.WriteLine("stop"); server.StandardInput.Flush(); }
                        catch (IOException ex) { eventErrors.Enqueue("Server stdin cleanup: " + ex.Message); }
                        if (!server.WaitForExit(30000)) { server.Kill(true); server.WaitForExit(10000); }
                    }
                    if (server.HasExited) { server.WaitForExit(); serverExitCode = server.ExitCode; }
                }
                catch (Exception ex)
                {
                    eventErrors.Enqueue("Server cleanup: " + ex.Message);
                    try { if (!server.HasExited) { server.Kill(true); server.WaitForExit(10000); } }
                    catch (Exception cleanup) { eventErrors.Enqueue("Owned server cleanup: " + cleanup.Message); }
                }
                finally { server.Dispose(); }
            }
            serverWriter?.Dispose();
            sessions.ProcessStarted -= Started; sessions.ProcessExited -= Exited;
            if (failure == null && !eventErrors.IsEmpty) failure = new InvalidOperationException(string.Join("\n", eventErrors));
            File.WriteAllText(resultFile, JsonSerializer.Serialize(new
            {
                passed = failure == null, checks = checks - initialChecks, minecraft, loader = loader.ToString(), loaderVersion,
                server = new { gamePort, descriptorUrl, exitCode = serverExitCode },
                bridge = new { filename = Path.GetFileName(bridge), sha512 = ServerSmokeHash(bridge) },
                fixture = new { filename = fixtureName, sha512 = fixtureHash, fileRequests = FileRequests() },
                launches = launches.ToArray(), exits = exits.ToArray(), error = failure?.ToString(), eventErrors = eventErrors.ToArray()
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        Console.WriteLine((failure == null ? "PASS " : "FAIL ") + (checks - initialChecks) + " real server sync checks");
        Console.WriteLine(resultFile);
        return failure == null ? 0 : 1;
    }

    private static int FreeServerSmokePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }
    private static string ServerSmokeHash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA512.HashData(stream)).ToLowerInvariant();
    }
    private sealed record ServerSmokeLaunch(int Pid, DateTimeOffset At, bool FixtureInstalled);
    private sealed record ServerSmokeExit(int Pid, int Code, DateTimeOffset At, string LogPath);
}
