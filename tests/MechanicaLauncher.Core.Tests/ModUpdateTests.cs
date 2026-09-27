using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MechanicaLauncher.Core.Instances;
using MechanicaLauncher.Core.Models;
using MechanicaLauncher.Core.Mods;
using MechanicaLauncher.Core.Servers;

internal static class ModUpdateTests
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> check, string root)
    {
        await check("Mod updates identify exact hashes and request stable releases for the exact target", async () =>
        {
            using var f = new Fixture(root, "available");
            var old = f.Version("main", 1); var next = f.Version("main", 2);
            f.Install(old, "renamed.jar"); f.Offer(old, next);
            var scan = await f.Check();
            Require(scan.Errors.Count == 0 && scan.Items.Single().State == ModUpdateState.Available);
            Require(scan.Items[0].CurrentVersion == "1.0.0" && scan.Items[0].TargetVersion == "2.0.0");
            Require(f.LookupRequests == 1 && f.UpdateRequests == 1 && f.Downloads == 0);
        });

        await check("Mod updates never downgrade or reinstall the same catalog version", async () =>
        {
            foreach (int candidate in new[] { 1, 2 })
            {
                using var f = new Fixture(root, "current-" + candidate);
                var current = f.Version("main", 2); var offered = f.Version("main", candidate);
                f.Install(current); f.Offer(current, offered);
                var scan = await f.Check();
                Require(scan.Items.Single().State == ModUpdateState.Current && scan.Items[0].TargetVersion == null);
                await Error("conflict", () => f.Service.PlanAsync(scan, scan.Items.Select(i => i.FilePath)));
            }
        });

        await check("Mod updates reject wrong projects targets prereleases dates hashes and paths", async () =>
        {
            foreach (string scenario in new[] { "project", "minecraft", "loader", "beta", "date", "hash", "path", "url" })
            {
                using var f = new Fixture(root, "invalid-" + scenario);
                var old = f.Version("main", 1); var next = f.Version("main", 2);
                f.Install(old); f.Offer(old, next);
                switch (scenario)
                {
                    case "project": next.ProjectId = "other"; break;
                    case "minecraft": next.GameVersions = ["1.20.1"]; break;
                    case "loader": next.Loaders = ["neoforge"]; break;
                    case "beta": next.VersionType = "beta"; break;
                    case "date": next.DatePublished = null; break;
                    case "hash": next.Files[0].Hashes["sha512"] = "invalid"; break;
                    case "path": next.Files[0].Filename = "../outside.jar"; break;
                    case "url": next.Files[0].Url = "http://cdn.modrinth.com/main.jar"; break;
                }
                Require((await f.Check()).Items.Single().State == ModUpdateState.Incompatible);
                Require(f.Downloads == 0);
            }
        });

        await check("Mod updates distinguish unknown files from failed hash and update lookups", async () =>
        {
            foreach (string scenario in new[] { "unknown", "lookup", "update", "identity", "date" })
            {
                using var f = new Fixture(root, "lookup-" + scenario);
                var old = f.Version("main", 1); f.Install(old); f.Offer(old, f.Version("main", 2));
                switch (scenario)
                {
                    case "unknown": f.Current.Clear(); break;
                    case "lookup": f.FailurePath = "/v2/version_files"; break;
                    case "update": f.FailurePath = "/v2/version_files/update"; break;
                    case "identity": old.Files[0].Hashes["sha512"] = new string('0', 128); break;
                    case "date": old.DatePublished = null; break;
                }
                var scan = await f.Check();
                Require(scan.Items.Single().State == (scenario == "unknown" ? ModUpdateState.Unknown : ModUpdateState.Unavailable));
                Require((scan.Errors.Count > 0) == (scenario is "lookup" or "update"));
            }
        });

        await check("Mod updates apply only selected files preserving disabled names personal content and backups", async () =>
        {
            using var f = new Fixture(root, "selected-disabled");
            var a = f.Version("main", 1); var a2 = f.Version("main", 2);
            var b = f.Version("other", 1); var b2 = f.Version("other", 2);
            string path = f.Install(a, "custom-name.jar.disabled"), other = f.Install(b);
            f.Offer(a, a2); f.Offer(b, b2);
            string personal = f.Local("personal.jar", Jar("personal", "1.0.0"));
            byte[] personalBytes = await File.ReadAllBytesAsync(personal);
            string options = Path.Combine(f.Game, "options.txt"); await File.WriteAllTextAsync(options, "settings");
            var plan = await f.Plan(path);
            Require(plan.Changes.Count == 1 && !plan.Changes[0].Enabled && plan.Conflicts.Count == 0);
            var result = await f.Service.ApplyAsync(plan, () => false);
            Require(result.UpdatedCount == 1 && Bytes(path, f.Data(a2)) && Bytes(other, f.Data(b)));
            Require(Bytes(personal, personalBytes) && await File.ReadAllTextAsync(options) == "settings");
            Require(Bytes(Path.Combine(result.BackupDirectory, "custom-name.jar.disabled"), f.Data(a)));
            Require(!File.Exists(Path.Combine(f.Mods, a2.Files[0].Filename)) && !File.Exists(f.Journal));
            Require(!Directory.EnumerateDirectories(f.Transactions, "prepared", SearchOption.AllDirectories).Any());
        });

        await check("Mod update plans expose required dependency changes and omit optional dependencies", async () =>
        {
            using var f = new Fixture(root, "dependencies");
            var old = f.Version("main", 1); var next = f.Version("main", 2);
            var library = f.Version("library", 1); var library2 = f.Version("library", 2);
            var required = f.Version("required", 1); var optional = f.Version("optional", 1);
            next.Dependencies = [Dependency(library2, "required", exact: false), Dependency(required, "required"), Dependency(optional, "optional")];
            string mainPath = f.Install(old), libraryPath = f.Install(library, "renamed-library.jar"); f.Offer(old, next);
            var plan = await f.Plan(mainPath);
            Require(plan.Conflicts.Count == 0 && plan.Changes.Count == 3);
            var change = plan.Changes.Single(c => c.FileName == "renamed-library.jar");
            Require(change.Dependency && change.CurrentVersion == "1.0.0" && change.TargetVersion == "2.0.0");
            Require(plan.Changes.Count(c => c.Dependency) == 2 && !plan.Changes.Any(c => c.Name == optional.Name));
            await f.Service.ApplyAsync(plan, () => false);
            Require(Bytes(libraryPath, f.Data(library2)) && Bytes(Path.Combine(f.Mods, required.Files[0].Filename), f.Data(required)));
            Require(!File.Exists(Path.Combine(f.Mods, optional.Files[0].Filename)) && f.Downloads == 3);
        });

        await check("Mod update incompatibility checks use the final selected versions", async () =>
        {
            using var f = new Fixture(root, "incompatible-old");
            var a = f.Version("main", 1); var a2 = f.Version("main", 2);
            var b = f.Version("other", 1); var b2 = f.Version("other", 2);
            a2.Dependencies = [Dependency(b, "incompatible")];
            string pa = f.Install(a), pb = f.Install(b); f.Offer(a, a2); f.Offer(b, b2);
            Require((await f.Plan(pa)).Conflicts.Count > 0);
            Require((await f.Plan(pa, pb)).Conflicts.Count == 0);
        });

        await check("Mod updates do not enable disabled dependencies or downgrade pinned dependencies", async () =>
        {
            foreach (bool disabled in new[] { true, false })
            {
                using var f = new Fixture(root, "dependency-conflict-" + disabled);
                var old = f.Version("main", 1); var next = f.Version("main", 2);
                var library = f.Version("library", disabled ? 1 : 2); var needed = disabled ? library : f.Version("library", 1);
                next.Dependencies = [Dependency(needed, "required")];
                string main = f.Install(old); f.Install(library, disabled ? "library.jar.disabled" : "library.jar"); f.Offer(old, next);
                var plan = await f.Plan(main);
                Require(plan.Conflicts.Count > 0);
                await Error("conflict", () => f.Service.ApplyAsync(plan, () => false));
                Require(f.Downloads == 0 && Bytes(main, f.Data(old)));
            }
        });

        await check("Mod updates protect server managed files by both name and exact renamed hash", async () =>
        {
            foreach (bool renamed in new[] { true, false })
            {
                using var f = new Fixture(root, "managed-" + renamed);
                var old = f.Version("main", 1); f.Offer(old, f.Version("main", 2));
                string path = f.Install(old, renamed ? "personal-name.jar" : "server.jar.disabled");
                await f.Managed("server.jar", old);
                var scan = await f.Check();
                Require(scan.Items.Single().State == ModUpdateState.Managed && f.UpdateRequests == 0);
                await Error("conflict", () => f.Service.PlanAsync(scan, [path]));
            }
        });

        await check("Mod updates recheck server ownership before committing a previously personal file", async () =>
        {
            using var f = new Fixture(root, "became-managed");
            var old = f.Version("main", 1); f.Offer(old, f.Version("main", 2)); string path = f.Install(old);
            var plan = await f.Plan(path); await f.Managed(Path.GetFileName(path), old);
            await Error("conflict", () => f.Service.ApplyAsync(plan, () => false));
            Require(Bytes(path, f.Data(old)) && f.Downloads == 0);
        });

        await check("Mod update plans refuse stale files and instance settings", async () =>
        {
            foreach (string scenario in new[] { "bytes", "added", "settings" })
            {
                using var f = new Fixture(root, "stale-" + scenario);
                var old = f.Version("main", 1); f.Offer(old, f.Version("main", 2)); string path = f.Install(old);
                var scan = await f.Check();
                if (scenario == "bytes") await File.WriteAllBytesAsync(path, Jar("main", "personal"));
                if (scenario == "added") f.Local("new.jar", Jar("new", "1.0.0"));
                if (scenario == "settings") f.Instance.McVersion = "1.20.1";
                await Error("conflict", () => f.Service.PlanAsync(scan, [path]));
                Require(f.Downloads == 0);
            }
        });

        await check("Mod update hash failures preserve originals clean staging and allow retrying the same plan", async () =>
        {
            using var f = new Fixture(root, "hash-retry");
            var old = f.Version("main", 1); var next = f.Version("main", 2); string path = f.Install(old); f.Offer(old, next);
            var plan = await f.Plan(path); byte[] correct = f.Data(next);
            f.Payloads[next.Files[0].Url] = [1, 2, 3];
            await Throws<InvalidDataException>(() => f.Service.ApplyAsync(plan, () => false));
            Require(Bytes(path, f.Data(old)) && !File.Exists(f.Journal));
            Require(!Directory.EnumerateDirectories(f.Transactions, "prepared", SearchOption.AllDirectories).Any());
            f.Payloads[next.Files[0].Url] = correct;
            await f.Service.ApplyAsync(plan, () => false);
            Require(Bytes(path, correct));
        });

        await check("Mod updates reject local edits and a running game after downloads", async () =>
        {
            foreach (bool running in new[] { true, false })
            {
                using var f = new Fixture(root, "after-download-" + running);
                var old = f.Version("main", 1); var next = f.Version("main", 2); string path = f.Install(old); f.Offer(old, next);
                var plan = await f.Plan(path); byte[] personal = Jar("main", "personal"); bool started = false;
                f.OnDownload = async _ => { started = running; if (!running) await File.WriteAllBytesAsync(path, personal); };
                await Error(running ? "busy" : "conflict", () => f.Service.ApplyAsync(plan, () => started));
                Require(Bytes(path, running ? f.Data(old) : personal) && !File.Exists(f.Journal));
            }
        });

        await check("Mod updates refuse to stage while the game is already running", async () =>
        {
            using var f = new Fixture(root, "running"); var old = f.Version("main", 1);
            string path = f.Install(old); f.Offer(old, f.Version("main", 2)); var plan = await f.Plan(path);
            await Error("busy", () => f.Service.ApplyAsync(plan, () => true));
            Require(f.Downloads == 0 && Bytes(path, f.Data(old)));
        });

        await check("Mod update cancellation during staging leaves originals intact", async () =>
        {
            using var f = new Fixture(root, "cancel-download"); var old = f.Version("main", 1);
            string path = f.Install(old); f.Offer(old, f.Version("main", 2)); var plan = await f.Plan(path);
            using var cancel = new CancellationTokenSource();
            f.OnDownload = token => { cancel.Cancel(); token.ThrowIfCancellationRequested(); return Task.CompletedTask; };
            await Throws<OperationCanceledException>(() => f.Service.ApplyAsync(plan, () => false, cancel.Token));
            Require(Bytes(path, f.Data(old)) && !File.Exists(f.Journal));
            Require(!Directory.EnumerateDirectories(f.Transactions, "prepared", SearchOption.AllDirectories).Any());
        });

        await check("Mod update cancellation during commit restores every original and retains backups", async () =>
        {
            using var f = new Fixture(root, "cancel-commit");
            var a = f.Version("main", 1); var a2 = f.Version("main", 2); string pa = f.Install(a); f.Offer(a, a2);
            var b = f.Version("other", 1); var b2 = f.Version("other", 2); string pb = f.Install(b); f.Offer(b, b2);
            var plan = await f.Plan(pa, pb); using var cancel = new CancellationTokenSource();
            bool Running() { if (Bytes(pa, f.Data(a2)) || Bytes(pb, f.Data(b2))) cancel.Cancel(); return false; }
            await Throws<OperationCanceledException>(() => f.Service.ApplyAsync(plan, Running, cancel.Token));
            Require(cancel.IsCancellationRequested && Bytes(pa, f.Data(a)) && Bytes(pb, f.Data(b)) && !File.Exists(f.Journal));
            Require(Directory.EnumerateFiles(f.Transactions, "*.jar", SearchOption.AllDirectories).Count() == 2);
        });

        await check("Mod update commit IO failure rolls back earlier replacements", async () =>
        {
            if (!OperatingSystem.IsWindows()) return;
            using var f = new Fixture(root, "locked-commit");
            var a = f.Version("main", 1); string pa = f.Install(a); f.Offer(a, f.Version("main", 2));
            var b = f.Version("other", 1); string pb = f.Install(b); f.Offer(b, f.Version("other", 2));
            var plan = await f.Plan(pa, pb);
            using var locked = new FileStream(Path.Combine(f.Mods, plan.Changes[1].FileName), FileMode.Open, FileAccess.Read, FileShare.Read);
            bool refused = false;
            try { await f.Service.ApplyAsync(plan, () => false); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { refused = true; }
            Require(refused);
            Require(Bytes(pa, f.Data(a)) && Bytes(pb, f.Data(b)) && !File.Exists(f.Journal));
        });

        await check("Mod updates verify the staged mod metadata against personal installed mods", async () =>
        {
            using var f = new Fixture(root, "compatibility");
            var old = f.Version("main", 1); var next = f.Version("main", 2); string path = f.Install(old); f.Offer(old, next);
            f.Local("duplicate.jar", Jar("main", "3.0.0")); var plan = await f.Plan(path);
            await Error("incompatible", () => f.Service.ApplyAsync(plan, () => false));
            Require(Bytes(path, f.Data(old)) && !File.Exists(f.Journal));
        });

        await check("Mod updates refuse missing required local Fabric dependencies before replacing originals", async () =>
        {
            using var f = new Fixture(root, "missing-required");
            var old = f.Version("main", 1); var next = f.Version("main", 2, Jar("main", "2.0.0", "required_library"));
            string path = f.Install(old); f.Offer(old, next); var plan = await f.Plan(path);
            Require(plan.Changes.Count == 1 && next.Dependencies.Count == 0);
            await Error("incompatible", () => f.Service.ApplyAsync(plan, () => false));
            Require(Bytes(path, f.Data(old)) && !File.Exists(f.Journal));
        });

        await check("Mod updates honor explicit Fabric dependency overrides during staging", async () =>
        {
            using var f = new Fixture(root, "dependency-override");
            var old = f.Version("main", 1); var next = f.Version("main", 2, Jar("main", "2.0.0", "removed_library"));
            string path = f.Install(old); f.Offer(old, next);
            Directory.CreateDirectory(Path.Combine(f.Game, "config"));
            await File.WriteAllTextAsync(Path.Combine(f.Game, "config", "fabric_loader_dependencies.json"),
                "{\"version\":1,\"overrides\":{\"main\":{\"-depends\":{\"removed_library\":\"*\"}}}}");
            await f.Service.ApplyAsync(await f.Plan(path), () => false);
            Require(Bytes(path, f.Data(next)) && !File.Exists(f.Journal));
        });

        await check("Mod updates reject incompatible Forge and NeoForge loader requirements before committing", async () =>
        {
            foreach (var loader in new[] { LoaderType.Forge, LoaderType.NeoForge })
            {
                using var f = new Fixture(root, "toml-loader-" + loader);
                f.Instance.Loader = loader; f.Instance.McVersion = loader == LoaderType.Forge ? "1.20.1" : "1.21.1";
                f.Instance.LoaderVersion = loader == LoaderType.Forge ? "47.4.0" : "21.1.10";
                var old = f.Version("main", 1, LoaderJar(loader, "1.0.0", "[1,)"));
                var next = f.Version("main", 2, LoaderJar(loader, "2.0.0", loader == LoaderType.Forge ? "[48,)" : "[21.1.200,)"));
                string path = f.Install(old); f.Offer(old, next); var plan = await f.Plan(path);
                await Error("incompatible", () => f.Service.ApplyAsync(plan, () => false));
                Require(Bytes(path, f.Data(old)) && !File.Exists(f.Journal));
            }
        });

        await check("Mod update recovery restores only known transaction bytes and preserves later user edits", async () =>
        {
            foreach (bool edited in new[] { true, false })
            {
                using var f = new Fixture(root, "recover-" + edited);
                var old = f.Version("main", 1); var next = f.Version("main", 2); string path = f.Install(old);
                Guid transaction = Guid.NewGuid(); string backup = Path.Combine(f.Transactions, transaction.ToString("N"), "backup");
                Directory.CreateDirectory(backup); await File.WriteAllBytesAsync(Path.Combine(backup, Path.GetFileName(path)), f.Data(old));
                byte[] actual = edited ? Jar("main", "personal") : f.Data(next); await File.WriteAllBytesAsync(path, actual);
                await File.WriteAllTextAsync(f.Journal, JsonSerializer.Serialize(new { version = 1, planId = Guid.NewGuid(), transactionId = transaction,
                    committed = false, files = new[] { new { fileName = Path.GetFileName(path), beforeHash = Sha512(f.Data(old)), beforeSize = f.Data(old).Length,
                        afterHash = Sha512(f.Data(next)), afterSize = f.Data(next).Length } } }));
                if (edited)
                {
                    await Throws<AggregateException>(() => f.Service.RecoverAsync(f.Game, () => false));
                    Require(Bytes(path, actual) && File.Exists(f.Journal));
                }
                else
                {
                    await Error("busy", () => f.Service.RecoverAsync(f.Game, () => true));
                    await f.Service.RecoverAsync(f.Game, () => false);
                    Require(Bytes(path, f.Data(old)) && !File.Exists(f.Journal));
                }
                Require(Bytes(Path.Combine(backup, Path.GetFileName(path)), f.Data(old)));
            }
        });

        await check("Mod update checks fail closed while recovery is pending", async () =>
        {
            using var f = new Fixture(root, "pending-journal");
            f.Install(f.Version("main", 1)); Directory.CreateDirectory(Path.GetDirectoryName(f.Journal)!);
            await File.WriteAllTextAsync(f.Journal, "{}");
            await Error("busy", () => f.Check()); Require(f.LookupRequests == 0);
        });
    }

    private static ModrinthDependency Dependency(ModrinthVersion version, string type, bool exact = true) => new()
        { ProjectId = version.ProjectId, VersionId = exact ? version.Id : null, DependencyType = type };
    private static string Sha1(byte[] bytes) => Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant();
    private static string Sha512(byte[] bytes) => Convert.ToHexString(SHA512.HashData(bytes)).ToLowerInvariant();
    private static bool Bytes(string path, byte[] expected) => File.Exists(path) && File.ReadAllBytes(path).SequenceEqual(expected);
    private static void Require(bool condition) { if (!condition) throw new Exception("Mod update assertion failed."); }
    private static async Task Error(string code, Func<Task> action)
    {
        try { await action(); }
        catch (ModUpdateException ex) { if (ex.Code == code) return; throw new Exception($"Expected {code}, got {ex.Code}: {ex.Message}", ex); }
        throw new Exception("Expected mod update error " + code);
    }
    private static async Task Throws<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }
    private static byte[] Jar(string id, string version, string? required = null)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        using (var writer = new StreamWriter(zip.CreateEntry("fabric.mod.json").Open(), new UTF8Encoding(false)))
            writer.Write(JsonSerializer.Serialize(new { schemaVersion = 1, id, version, name = id,
                depends = required == null ? null : new Dictionary<string, string> { [required] = "*" } }));
        return memory.ToArray();
    }

    private static byte[] LoaderJar(LoaderType loader, string version, string loaderRange)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        using (var writer = new StreamWriter(zip.CreateEntry(loader == LoaderType.Forge ? "META-INF/mods.toml" : "META-INF/neoforge.mods.toml").Open(), new UTF8Encoding(false)))
            writer.Write("modLoader='javafml'\nloaderVersion='[1,)'\n[[mods]]\nmodId='main'\nversion='" + version + "'\n"
                + "[[dependencies.main]]\nmodId='" + loader.ToString().ToLowerInvariant() + "'\n"
                + (loader == LoaderType.Forge ? "mandatory=true" : "type='required'") + "\nversionRange='" + loaderRange + "'\nside='BOTH'\n");
        return memory.ToArray();
    }

    private sealed class Fixture : IDisposable
    {
        private readonly HttpClient http;
        public string Game { get; }
        public string Mods => Path.Combine(Game, "mods");
        public string Journal => Path.Combine(Game, ".mechanica", "mod-update-journal.json");
        public string Transactions => Path.Combine(Game, ".mechanica", "mod-updates");
        public GameInstance Instance { get; } = new() { Id = "fixture", McVersion = "1.21.1", Loader = LoaderType.Fabric, LoaderVersion = "0.19.3" };
        public ModUpdateService Service { get; }
        public Dictionary<string, ModrinthVersion> Versions { get; } = [];
        public Dictionary<string, ModrinthVersion> Current { get; } = [];
        public Dictionary<string, ModrinthVersion> Updates { get; } = [];
        public Dictionary<string, byte[]> Payloads { get; } = [];
        public Func<CancellationToken, Task>? OnDownload;
        public string? FailurePath;
        public int LookupRequests, UpdateRequests, Downloads;

        public Fixture(string root, string name)
        {
            Game = Directory.CreateDirectory(Path.Combine(root, "mod-updates", name)).FullName;
            http = new(new Handler(this)) { BaseAddress = new("https://api.modrinth.com") };
            Service = new(new ModrinthClient(http), http);
        }
        public ModrinthVersion Version(string project, int number, byte[]? contents = null)
        {
            string id = project + number, filename = id + ".jar", url = "https://cdn.modrinth.com/" + filename;
            if (Versions.TryGetValue(id, out var existing)) return existing;
            byte[] bytes = contents ?? Jar(project, number + ".0.0"); Payloads[url] = bytes;
            var version = new ModrinthVersion { Id = id, ProjectId = project, Name = project, VersionNumber = number + ".0.0",
                DatePublished = new DateTimeOffset(2025, 1, number, 0, 0, 0, TimeSpan.Zero), GameVersions = [Instance.McVersion], Loaders = [Instance.Loader.ToString().ToLowerInvariant()],
                Files = [new() { Filename = filename, Url = url, Size = bytes.Length, Primary = true,
                    Hashes = new() { ["sha1"] = Sha1(bytes), ["sha512"] = Sha512(bytes) } }] };
            Versions[id] = version; return version;
        }
        public byte[] Data(ModrinthVersion version) => Payloads[version.Files[0].Url];
        public string Local(string name, byte[] bytes)
        {
            Directory.CreateDirectory(Mods); string path = Path.Combine(Mods, name); File.WriteAllBytes(path, bytes); return path;
        }
        public string Install(ModrinthVersion version, string? name = null)
        {
            Current[version.Files[0].Hashes["sha1"]] = version;
            return Local(name ?? version.Files[0].Filename, Data(version));
        }
        public void Offer(ModrinthVersion from, ModrinthVersion to) => Updates[from.Files[0].Hashes["sha1"]] = to;
        public Task<ModUpdateScan> Check() => Service.CheckAsync(Instance, Game);
        public async Task<ModUpdatePlan> Plan(params string[] files) => await Service.PlanAsync(await Check(), files);
        public async Task Managed(string filename, ModrinthVersion version)
        {
            string path = Path.Combine(Game, ".mechanica", "server-sync.json"); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var state = new ServerSyncState { Servers = [new(Guid.NewGuid(), "https://sync.example.test",
                new("fabric-1.21.1", "1.21.1", "fabric", "0.19.3"), 1, new string('a', 128), Guid.NewGuid(),
                [new("main", filename, Data(version).Length, Sha512(Data(version)))])] };
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(state, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        }
        public void Dispose() => http.Dispose();

        private sealed class Handler(Fixture f) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string path = request.RequestUri!.AbsolutePath;
                if (request.RequestUri.Host == "cdn.modrinth.com")
                {
                    f.Downloads++; if (f.OnDownload != null) await f.OnDownload(cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    return new(HttpStatusCode.OK) { Content = new ByteArrayContent(f.Payloads[request.RequestUri.AbsoluteUri]) };
                }
                if (path == f.FailurePath) return new(HttpStatusCode.ServiceUnavailable);
                if (path is "/v2/version_files" or "/v2/version_files/update")
                {
                    Require(request.Method == HttpMethod.Post);
                    using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                    var body = document.RootElement; Require(body.GetProperty("algorithm").GetString() == "sha1");
                    var hashes = body.GetProperty("hashes").EnumerateArray().Select(v => v.GetString()!).ToArray();
                    Require(hashes.Length is > 0 and <= 100 && hashes.All(h => h.Length == 40));
                    bool update = path.EndsWith("/update", StringComparison.Ordinal);
                    if (update)
                    {
                        f.UpdateRequests++;
                        Require(body.GetProperty("game_versions").EnumerateArray().Single().GetString() == f.Instance.McVersion);
                        Require(body.GetProperty("loaders").EnumerateArray().Single().GetString() == f.Instance.Loader.ToString().ToLowerInvariant());
                        Require(body.GetProperty("version_types").EnumerateArray().Single().GetString() == "release");
                    }
                    else f.LookupRequests++;
                    return Json((update ? f.Updates : f.Current).Where(p => hashes.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value));
                }
                if (path.StartsWith("/v2/version/", StringComparison.Ordinal)) return Json(f.Versions[path.Split('/')[^1]]);
                if (path.StartsWith("/v2/project/", StringComparison.Ordinal))
                {
                    string query = Uri.UnescapeDataString(request.RequestUri.Query);
                    Require(query.Contains(f.Instance.McVersion) && query.Contains(f.Instance.Loader.ToString().ToLowerInvariant()));
                    return Json(f.Versions.Values.Where(v => v.ProjectId == path.Split('/')[3]).ToArray());
                }
                throw new Exception("Unexpected mod update HTTP request " + request.RequestUri);
            }
            private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
                { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
        }
    }
}
