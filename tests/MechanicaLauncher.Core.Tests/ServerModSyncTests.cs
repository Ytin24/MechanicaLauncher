using System.IO.Compression;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MechanicaLauncher.Core.Instances;
using MechanicaLauncher.Core.Models;
using MechanicaLauncher.Core.Servers;

internal static class ServerModSyncTests
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> check, string root)
    {
        await check("Server sync installs only required jars and preserves personal content and is idempotent", async () =>
        {
            using var f = new Fixture(root, "install");
            f.Add("required");
            f.Add("optional", side: "optional");
            f.Add("server", side: "unsupported");
            var personal = f.Local("personal.jar", Jar("personal", "1"));
            string world = Path.Combine(Directory.CreateDirectory(Path.Combine(f.Game, "saves", "world")).FullName, "level.dat");
            await File.WriteAllTextAsync(world, "world-data");
            string options = Path.Combine(f.Game, "options.txt");
            await File.WriteAllTextAsync(options, "personal settings");
            var engine = f.Engine();
            var plan = await f.Plan(engine);
            Require(plan.HasChanges && plan.Changes.Count == 1 && plan.Conflicts.Count == 0 && plan.DownloadBytes == f.Data["required.jar"].Length);
            var stage = await engine.StageAsync(plan, f.Staging);
            Require(!File.Exists(Path.Combine(f.Mods, "required.jar")) && stage.Files.Count == 1);
            await engine.ApplyAsync(stage, () => false);
            byte[] registry = await File.ReadAllBytesAsync(f.StatePath);
            await engine.ApplyAsync(stage, () => true);
            Require(registry.SequenceEqual(await File.ReadAllBytesAsync(f.StatePath)));
            Require(File.Exists(personal) && await File.ReadAllTextAsync(world) == "world-data" && await File.ReadAllTextAsync(options) == "personal settings");
            Require(!File.Exists(Path.Combine(f.Mods, "optional.jar")) && !File.Exists(Path.Combine(f.Mods, "server.jar")));
            Require(f.Requests.Count(u => u.AbsolutePath.StartsWith("/files/", StringComparison.Ordinal)) == 1);
            var again = await f.Plan(engine);
            Require(!again.HasChanges && again.DownloadBytes == 0 && again.Conflicts.Count == 0);
            Require((await ServerModSync.GetManagedFileNamesAsync(f.Game)).SetEquals(["required.jar"]));
        });

        await check("Server sync verifies pinned Modrinth files and required dependency versions", async () =>
        {
            using var f = new Fixture(root, "modrinth");
            var lib = f.Add("library", modrinth: true);
            var main = f.Add("main", modrinth: true);
            main["dependencies"] = new JsonArray("library");
            f.Versions["mainVersion"].Dependencies = [new() { ProjectId = "libraryProject", VersionId = "libraryVersion", DependencyType = "required" }];
            var engine = f.Engine();
            var plan = await f.Plan(engine);
            var stage = await engine.StageAsync(plan, f.Staging);
            await engine.ApplyAsync(stage, () => false);
            Require(f.Requests.Count(u => u.Host == "api.modrinth.com") == 2 && f.Requests.Count(u => u.Host == "cdn.modrinth.com") == 2);
            Require(File.Exists(Path.Combine(f.Mods, "main.jar")) && File.Exists(Path.Combine(f.Mods, "library.jar")));
        });

        await check("Server sync rejects wrong API identity hashes targets and omitted required catalog dependencies", async () =>
        {
            foreach (string scenario in new[] { "project", "version", "hash", "minecraft", "loader", "dependency" })
            {
                using var f = new Fixture(root, "bad-api-" + scenario);
                f.Add("main", modrinth: true);
                var version = f.Versions["mainVersion"];
                switch (scenario)
                {
                    case "project": version.ProjectId = "wrong"; break;
                    case "version": version.Id = "wrong"; break;
                    case "hash": version.Files[0].Hashes["sha512"] = new string('0', 128); break;
                    case "minecraft": version.GameVersions = ["1.20.1"]; break;
                    case "loader": version.Loaders = ["forge"]; break;
                    case "dependency": version.Dependencies = [new() { ProjectId = "missing", DependencyType = "required" }]; break;
                }
                await Error("invalid_manifest", () => f.Plan(f.Engine()));
                Require(!f.Requests.Any(u => u.Host == "cdn.modrinth.com") && !Directory.Exists(f.Mods));
            }
        });

        await check("Server sync reuses a renamed exact personal jar without acquiring deletion ownership", async () =>
        {
            using var f = new Fixture(root, "renamed");
            f.Add("main");
            string path = f.Local("my-name.jar", f.Data["main.jar"]);
            var engine = f.Engine();
            var plan = await f.Plan(engine);
            Require(!plan.HasChanges && plan.Conflicts.Count == 0 && plan.DownloadBytes == 0);
            await engine.ApplyAsync(await engine.StageAsync(plan, f.Staging), () => false);
            Require((await ServerModSync.GetManagedFileNamesAsync(f.Game)).Count == 0);
            f.Files.Clear(); f.Revision++;
            var removed = await f.Plan(engine);
            Require(!removed.HasChanges && File.Exists(path) && !f.Requests.Any(u => u.AbsolutePath.StartsWith("/files/", StringComparison.Ordinal)));
        });

        await check("Server sync reports disabled matches and personal mod ID conflicts without overwriting", async () =>
        {
            foreach (bool disabled in new[] { true, false })
            {
                using var f = new Fixture(root, "personal-conflict-" + disabled);
                f.Add("main");
                byte[] original = disabled ? f.Data["main.jar"] : Jar("main", "personal-version");
                string path = f.Local(disabled ? "renamed.jar.disabled" : "personal-version.jar", original);
                var engine = f.Engine();
                var plan = await f.Plan(engine);
                Require(plan.Conflicts.Count > 0 && plan.Conflicts.Any(c => c.FileName == Path.GetFileName(path)));
                await Error("conflict", () => engine.StageAsync(plan, f.Staging));
                Require(original.SequenceEqual(await File.ReadAllBytesAsync(path)) && !File.Exists(f.StatePath));
            }
        });

        await check("Server sync replaces and removes only unchanged owned entries", async () =>
        {
            using var f = new Fixture(root, "managed-update");
            f.Add("main"); f.Add("obsolete");
            byte[] personal = Jar("personal", "1");
            string personalPath = f.Local("personal.jar", personal);
            var engine = f.Engine();
            await f.Install(engine);
            f.Files.Clear(); f.Revision++;
            f.Add("main", version: "2", filename: "main-new.jar"); f.Add("added");
            var plan = await f.Plan(engine);
            Require(plan.Changes.Count == 3 && plan.Changes.Count(c => c.Kind == ServerSyncChangeKind.Replace) == 1 && plan.Changes.Count(c => c.Kind == ServerSyncChangeKind.Remove) == 1);
            await engine.ApplyAsync(await engine.StageAsync(plan, f.Staging), () => false);
            Require(!File.Exists(Path.Combine(f.Mods, "main.jar")) && !File.Exists(Path.Combine(f.Mods, "obsolete.jar")) && File.Exists(Path.Combine(f.Mods, "main-new.jar")));
            Require(personal.SequenceEqual(await File.ReadAllBytesAsync(personalPath)) && !File.Exists(f.JournalPath));
            Require((await ServerModSync.GetManagedFileNamesAsync(f.Game)).SetEquals(["main-new.jar", "added.jar"]));
        });

        await check("Server sync refuses to remove a manually modified managed mod", async () =>
        {
            using var f = new Fixture(root, "modified-managed");
            f.Add("main");
            var engine = f.Engine();
            await f.Install(engine);
            byte[] personal = Jar("main", "user-edit");
            await File.WriteAllBytesAsync(Path.Combine(f.Mods, "main.jar"), personal);
            f.Files.Clear(); f.Revision++;
            var plan = await f.Plan(engine);
            Require(plan.Conflicts.Any(c => c.FileName == "main.jar") && !plan.Changes.Any(c => c.Kind == ServerSyncChangeKind.Remove));
            await Error("conflict", () => engine.StageAsync(plan, f.Staging));
            Require(personal.SequenceEqual(await File.ReadAllBytesAsync(Path.Combine(f.Mods, "main.jar"))));
        });

        await check("Server sync rejects manifest byte mismatch unsupported capabilities and exact target mismatch", async () =>
        {
            using var mismatch = new Fixture(root, "manifest-hash");
            mismatch.Add("main"); mismatch.DescriptorHash = new string('0', 128);
            await Error("hash_mismatch", () => mismatch.Plan(mismatch.Engine()));
            using var protocol = new Fixture(root, "protocol");
            protocol.DescriptorEdit = d => d["protocols"]![0]!["requiredCapabilities"] = new JsonArray("mods-v1", "run-scripts");
            await Error("unsupported_protocol", () => protocol.Plan(protocol.Engine()));
            Require(protocol.Requests.Count == 1);
            using var target = new Fixture(root, "target");
            target.Instance.LoaderVersion = "0.19.2";
            await Error("target_mismatch", () => target.Plan(target.Engine()));
            Require(target.Requests.Count == 1);
        });

        await check("Server sync rejects missing and optional-only dependency closure and duplicate mod IDs", async () =>
        {
            foreach (string scenario in new[] { "missing", "optional", "duplicate" })
            {
                using var f = new Fixture(root, "closure-" + scenario);
                var main = f.Add("main");
                if (scenario == "duplicate") f.Add("other")["modIds"] = new JsonArray("main");
                else
                {
                    main["dependencies"] = new JsonArray("library");
                    if (scenario == "optional") f.Add("library", side: "optional");
                }
                await Error("invalid_manifest", () => f.Plan(f.Engine()));
                Require(!f.Requests.Any(u => u.AbsolutePath.StartsWith("/files/", StringComparison.Ordinal)));
            }
        });

        await check("Server sync rejects file path tricks case collisions size limits and excessive JSON depth", async () =>
        {
            int i = 0;
            foreach (string name in new[] { "../escape.jar", "C:\\escape.jar", "CON.jar", "valid.jar:payload", "bad..jar", "e\u0301.jar" })
            {
                using var f = new Fixture(root, "path-" + i++);
                f.Add("main")["filename"] = name;
                await Error("invalid_manifest", () => f.Plan(f.Engine()));
            }
            using var collision = new Fixture(root, "case-collision");
            collision.Add("one")["filename"] = "One.jar";
            collision.Add("two")["filename"] = "one.jar";
            await Error("invalid_manifest", () => collision.Plan(collision.Engine()));
            using var size = new Fixture(root, "size-limit");
            size.Add("main")["size"] = ServerModSync.MaxFileBytes + 1;
            await Error("invalid_manifest", () => size.Plan(size.Engine()));
            using var deep = new Fixture(root, "depth-limit");
            deep.ManifestEdit = m => { JsonNode value = JsonValue.Create(1)!; for (int j = 0; j < 20; j++) value = new JsonArray(value); m["unused"] = value; };
            await Error("invalid_manifest", () => deep.Plan(deep.Engine()));
        });

        await check("Server sync rejects unapproved external origins redirects nonliteral local HTTP and oversized JSON", async () =>
        {
            using var external = new Fixture(root, "unapproved");
            external.Add("main");
            await Error("conflict", () => external.Plan(external.Engine(approveExternal: false)));
            using var redirect = new Fixture(root, "redirect");
            redirect.Respond = _ => new(HttpStatusCode.Redirect) { Headers = { Location = new Uri("http://127.0.0.1/private") } };
            await Error("invalid_manifest", () => redirect.Plan(redirect.Engine()));
            Require(redirect.Requests.Count == 1);
            using var local = new Fixture(root, "local-http", "http://127.0.0.1:39851");
            local.Add("main");
            await Error("invalid_manifest", () => local.Plan(local.Engine(), allowLocal: false));
            var plan = await local.Plan(local.Engine(), allowLocal: true);
            Require(plan.Conflicts.Count == 0 && plan.HasChanges);
            await Error("invalid_manifest", () => local.Engine().PlanAsync(new("http://localhost:39851/descriptor.json"), local.Instance, local.Game, true));
            using var large = new Fixture(root, "json-limit");
            large.Respond = _ => Response(new byte[ServerModSync.MaxJsonBytes + 1]);
            await Error("invalid_manifest", () => large.Plan(large.Engine()));
            Require(large.Requests.Count == 1);
        });

        await check("Server sync never publishes corrupt or cancelled downloads into the game", async () =>
        {
            using var corrupt = new Fixture(root, "corrupt-download");
            corrupt.Add("main");
            var engine = corrupt.Engine();
            var plan = await corrupt.Plan(engine);
            byte[] bad = corrupt.Data["main.jar"].ToArray(); bad[^1] ^= 1;
            corrupt.Data["main.jar"] = bad;
            await Error("hash_mismatch", () => engine.StageAsync(plan, corrupt.Staging));
            Require(!Directory.Exists(corrupt.Mods) && !File.Exists(corrupt.StatePath) && !Directory.EnumerateFiles(corrupt.Staging, "*.jar", SearchOption.AllDirectories).Any());
            using var cancel = new Fixture(root, "cancel-download");
            cancel.Add("main"); var cancelEngine = cancel.Engine(); var cancelPlan = await cancel.Plan(cancelEngine);
            using var cts = new CancellationTokenSource();
            cancel.OnRequest = u => { if (u.AbsolutePath.StartsWith("/files/", StringComparison.Ordinal)) cts.Cancel(); };
            await Cancelled(() => cancelEngine.StageAsync(cancelPlan, cancel.Staging, cts.Token));
            Require(!Directory.Exists(cancel.Mods) && !File.Exists(cancel.StatePath));
        });

        await check("Server sync offline preflight and cancellation leave mods untouched", async () =>
        {
            using var f = new Fixture(root, "offline");
            byte[] original = Jar("personal", "1"); string path = f.Local("personal.jar", original);
            f.Respond = _ => throw new HttpRequestException("offline");
            await Error("network_error", () => f.Plan(f.Engine()));
            Require(f.Requests.Count == 3 && original.SequenceEqual(await File.ReadAllBytesAsync(path)) && !File.Exists(f.StatePath));
            using var cts = new CancellationTokenSource(); cts.Cancel();
            await Cancelled(() => f.Engine().PlanAsync(f.Endpoint, f.Instance, f.Game, cancellationToken: cts.Token));
            Require(f.Requests.Count == 3);
        });

        await check("Server sync repeats local and staged hashes before apply and rejects running games", async () =>
        {
            using var local = new Fixture(root, "changed-after-plan");
            local.Add("main"); byte[] personal = Jar("personal", "1"); string path = local.Local("personal.jar", personal);
            var engine = local.Engine(); var stage = await engine.StageAsync(await local.Plan(engine), local.Staging);
            byte[] changed = Jar("personal", "2"); await File.WriteAllBytesAsync(path, changed);
            await Error("conflict", () => engine.ApplyAsync(stage, () => false));
            Require(changed.SequenceEqual(await File.ReadAllBytesAsync(path)) && !File.Exists(Path.Combine(local.Mods, "main.jar")));
            using var staged = new Fixture(root, "changed-staged"); staged.Add("main");
            var se = staged.Engine(); var ss = await se.StageAsync(await staged.Plan(se), staged.Staging);
            await File.WriteAllBytesAsync(ss.Files["main"], Jar("main", "tampered"));
            await Error("hash_mismatch", () => se.ApplyAsync(ss, () => false));
            Require(!Directory.Exists(staged.Mods));
            using var running = new Fixture(root, "running"); running.Add("main");
            var re = running.Engine(); var rs = await re.StageAsync(await running.Plan(re), running.Staging);
            await Error("busy", () => re.ApplyAsync(rs, () => true));
            Require(!Directory.Exists(running.Mods) && !File.Exists(running.StatePath));
        });

        await check("Server sync cancellation after the first replacement rolls files and registry back", async () =>
        {
            using var f = new Fixture(root, "rollback-cancel"); f.Add("one"); f.Add("two");
            var engine = f.Engine(); await f.Install(engine);
            byte[] oldOne = await File.ReadAllBytesAsync(Path.Combine(f.Mods, "one.jar"));
            byte[] oldTwo = await File.ReadAllBytesAsync(Path.Combine(f.Mods, "two.jar"));
            byte[] oldState = await File.ReadAllBytesAsync(f.StatePath);
            f.Files.Clear(); f.Revision++; f.Add("one", version: "2"); f.Add("two", version: "2");
            var stage = await engine.StageAsync(await f.Plan(engine), f.Staging);
            using var cts = new CancellationTokenSource(); int gates = 0; bool sawReplacement = false;
            await Cancelled(() => engine.ApplyAsync(stage, () =>
            {
                if (++gates == 4) { sawReplacement = !oldOne.SequenceEqual(File.ReadAllBytes(Path.Combine(f.Mods, "one.jar"))); cts.Cancel(); }
                return false;
            }, cts.Token));
            Require(sawReplacement && oldOne.SequenceEqual(await File.ReadAllBytesAsync(Path.Combine(f.Mods, "one.jar"))) && oldTwo.SequenceEqual(await File.ReadAllBytesAsync(Path.Combine(f.Mods, "two.jar"))));
            Require(oldState.SequenceEqual(await File.ReadAllBytesAsync(f.StatePath)) && !File.Exists(f.JournalPath));
            await engine.RecoverAsync(f.Game);
        });

        await check("Server sync recovers an interrupted replacement and preserves intervening personal changes", async () =>
        {
            using var f = new Fixture(root, "recovery"); f.Add("main");
            var engine = f.Engine(); await f.Install(engine);
            byte[] old = await File.ReadAllBytesAsync(Path.Combine(f.Mods, "main.jar"));
            byte[] oldState = await File.ReadAllBytesAsync(f.StatePath);
            f.Files.Clear(); f.Revision++; f.Add("main", version: "2"); f.Add("added");
            var plan = await f.Plan(engine);
            var journal = new ServerSyncJournal
            {
                PlanId = plan.PlanId, PreviousState = Encoding.UTF8.GetString(oldState),
                NextStateHash = Hash(JsonSerializer.SerializeToUtf8Bytes(plan.NextState, JsonSettings)),
                Entries = [new("main.jar", Hash(old), old.Length, Hash(f.Data["main.jar"]), f.Data["main.jar"].Length),
                    new("added.jar", null, 0, Hash(f.Data["added.jar"]), f.Data["added.jar"].Length)]
            };
            string backup = Directory.CreateDirectory(Path.Combine(f.Game, ".mechanica", "server-sync-backups", plan.PlanId.ToString("N"))).FullName;
            await File.WriteAllBytesAsync(Path.Combine(backup, "main.jar.bak"), old);
            f.Local("main.jar", f.Data["main.jar"]); f.Local("added.jar", f.Data["added.jar"]);
            await File.WriteAllTextAsync(f.JournalPath, JsonSerializer.Serialize(journal, JsonSettings));
            byte[] personal = Jar("added", "personal-edit"); f.Local("added.jar", personal);
            await Error("conflict", () => engine.RecoverAsync(f.Game));
            Require(personal.SequenceEqual(await File.ReadAllBytesAsync(Path.Combine(f.Mods, "added.jar"))) && File.Exists(f.JournalPath));
            f.Local("added.jar", f.Data["added.jar"]);
            int requests = f.Requests.Count;
            await f.Engine().RecoverAsync(f.Game);
            Require(old.SequenceEqual(await File.ReadAllBytesAsync(Path.Combine(f.Mods, "main.jar"))) && !File.Exists(Path.Combine(f.Mods, "added.jar")));
            Require(oldState.SequenceEqual(await File.ReadAllBytesAsync(f.StatePath)) && !File.Exists(f.JournalPath) && f.Requests.Count == requests);
            await engine.RecoverAsync(f.Game);
        });

        await check("Server sync rejects old plans changed same revisions staging in game and competing transactions", async () =>
        {
            using var f = new Fixture(root, "stale"); f.Add("main");
            var engine = f.Engine(); var old = await f.Plan(engine);
            await Error("invalid_request", () => engine.StageAsync(old, f.Game));
            f.Revision++;
            var newer = await f.Plan(engine);
            await Error("request_expired", () => engine.StageAsync(old, f.Staging));
            f.Files[0]!["filename"] = "changed.jar";
            await Error("invalid_manifest", () => f.Plan(engine));
            using (ServerModSync.AcquireInstanceLock(f.Game)) await Error("busy", () => engine.StageAsync(newer, f.Staging));
            Require(!Directory.Exists(f.Mods) && !File.Exists(f.StatePath));
        });

        await check("Server sync stages validate actual Fabric dependency ranges before applying", async () =>
        {
            using var f = new Fixture(root, "metadata-dependency");
            f.Add("main", data: Jar("main", "1", "{\"library\":\">=2\"}"))["dependencies"] = new JsonArray("library");
            f.Add("library", version: "1");
            var engine = f.Engine(); var plan = await f.Plan(engine);
            await Error("conflict", () => engine.StageAsync(plan, f.Staging));
            Require(!Directory.Exists(f.Mods) && !File.Exists(f.StatePath));
        });

        await check("Server sync accepts Java nanosecond RFC3339 expiry and rejects expired or invalid timestamps", async () =>
        {
            using var nanos = new Fixture(root, "expiry-nanos"); nanos.Add("main");
            string nanoseconds = DateTime.UtcNow.AddHours(1).ToString("yyyy-MM-dd'T'HH:mm:ss") + ".123456789Z";
            nanos.ManifestEdit = m => m["expiresUtc"] = nanoseconds;
            var plan = await nanos.Plan(nanos.Engine());
            Require(plan.ExpiresUtc > DateTimeOffset.UtcNow && plan.ExpiresUtc <= DateTimeOffset.UtcNow.AddMinutes(15));
            using var expired = new Fixture(root, "expiry-old");
            expired.ManifestEdit = m => m["expiresUtc"] = "2020-01-01T00:00:00Z";
            await Error("request_expired", () => expired.Plan(expired.Engine()));
            using var malformed = new Fixture(root, "expiry-invalid");
            malformed.ManifestEdit = m => m["expiresUtc"] = "2026-99-01T00:00:00Z";
            await Error("invalid_manifest", () => malformed.Plan(malformed.Engine()));
        });

        await check("Server sync refuses junctions without inspecting or modifying their targets", async () =>
        {
            using var f = new Fixture(root, "junction"); f.Add("main");
            string outside = Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(f.Game)!, "outside")).FullName;
            byte[] original = Jar("personal", "1");
            string path = Path.Combine(outside, "personal.jar");
            await File.WriteAllBytesAsync(path, original);
            string script = Path.Combine(Path.GetDirectoryName(f.Game)!, "junction.ps1");
            await File.WriteAllTextAsync(script, "param($Link, $Target)\n$ErrorActionPreference = 'Stop'\nNew-Item -ItemType Junction -Path $Link -Value $Target | Out-Null\n");
            var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
            foreach (string argument in new[] { "-NoProfile", "-NonInteractive", "-File", script, "-Link", f.Mods, "-Target", outside }) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            await process.WaitForExitAsync(); Require(process.ExitCode == 0);
            try
            {
                await Error("conflict", () => f.Plan(f.Engine()));
                Require(original.SequenceEqual(await File.ReadAllBytesAsync(path)) && Directory.GetFiles(outside).Length == 1);
            }
            finally { Directory.Delete(f.Mods); }
        });

        await check("Server sync checks IDs on reused jars and reads Forge sections with comments and legacy case", async () =>
        {
            using var reused = new Fixture(root, "reused-id-mismatch");
            reused.Add("main")["modIds"] = new JsonArray("different");
            reused.Local("renamed.jar", reused.Data["main.jar"]);
            await Error("invalid_manifest", () => reused.Plan(reused.Engine()));
            using var forge = new Fixture(root, "forge-id-conflict");
            forge.Instance.Loader = LoaderType.Forge; forge.Instance.LoaderVersion = "47.4.0";
            forge.DescriptorEdit = d => { var target = d["protocols"]![0]!["targets"]![0]!; target["loader"] = "forge"; target["loaderVersion"] = "47.4.0"; };
            forge.ManifestEdit = m => { m["loader"] = "forge"; m["loaderVersion"] = "47.4.0"; };
            forge.Add("main")["modIds"] = new JsonArray("LegacyMod");
            using var memory = new MemoryStream();
            using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true))
            using (var writer = new StreamWriter(zip.CreateEntry("META-INF/mods.toml").Open()))
                writer.Write("[[mods]] # mandatory\nmodId = 'LegacyMod'\nversion = '1'\n[[dependencies.LegacyMod]]\nmodId = 'unrelated_dependency'\n");
            forge.Local("personal-forge.jar", memory.ToArray());
            var plan = await forge.Plan(forge.Engine());
            Require(plan.Conflicts.Any(c => c.FileName == "personal-forge.jar"));
        });

        await check("Server sync refuses pending mod updater journals including replay of an already applied plan", async () =>
        {
            using var f = new Fixture(root, "foreign-journal"); f.Add("main");
            var engine = f.Engine(); var stage = await engine.StageAsync(await f.Plan(engine), f.Staging);
            await engine.ApplyAsync(stage, () => false);
            byte[] before = await File.ReadAllBytesAsync(Path.Combine(f.Mods, "main.jar"));
            string journal = Path.Combine(f.Game, ".mechanica", "mod-update-journal.json");
            await File.WriteAllTextAsync(journal, "{}");
            await Error("busy", () => f.Plan(engine));
            await Error("busy", () => engine.ApplyAsync(stage, () => false));
            Require(before.SequenceEqual(await File.ReadAllBytesAsync(Path.Combine(f.Mods, "main.jar"))) && await File.ReadAllTextAsync(journal) == "{}");
        });
    }

    private static readonly JsonSerializerOptions JsonSettings = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA512.HashData(bytes)).ToLowerInvariant();
    private static HttpResponseMessage Response(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
    private static void Require(bool condition) { if (!condition) throw new Exception("Server sync assertion failed."); }
    private static async Task Error(string code, Func<Task> action)
    {
        try { await action(); }
        catch (ServerModSyncException ex) { if (ex.Code == code) return; throw new Exception($"Expected {code}, got {ex.Code}: {ex.Message}", ex); }
        throw new Exception("Expected server sync error " + code);
    }
    private static async Task Cancelled(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { return; }
        throw new Exception("Expected cancellation.");
    }
    private static byte[] Jar(string id, string version, string? dependencies = null)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        using (var writer = new StreamWriter(zip.CreateEntry("fabric.mod.json").Open(), new UTF8Encoding(false)))
            writer.Write($"{{\"schemaVersion\":1,\"id\":\"{id}\",\"version\":\"{version}\",\"name\":\"{id}\",\"depends\":{dependencies ?? "{}"}}}");
        return memory.ToArray();
    }

    private sealed class Fixture : IDisposable
    {
        private readonly HttpClient http;
        private readonly FakeHttp handler;
        private readonly string origin;
        private readonly string expiry = DateTime.UtcNow.AddHours(1).ToString("O");
        private static readonly Guid ServerId = Guid.Parse("b3a38c5e-593c-4bdd-9046-e019b3453eb0");
        public string Game { get; }
        public string Mods => Path.Combine(Game, "mods");
        public string Staging { get; }
        public string StatePath => Path.Combine(Game, ".mechanica", "server-sync.json");
        public string JournalPath => Path.Combine(Game, ".mechanica", "server-sync-journal.json");
        public Uri Endpoint => new(origin + "/descriptor.json?fixture=1");
        public GameInstance Instance { get; } = new() { Id = "fixture", McVersion = "1.21.1", Loader = LoaderType.Fabric, LoaderVersion = "0.19.3" };
        public long Revision = 1;
        public JsonArray Files { get; } = [];
        public Dictionary<string, byte[]> Data { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, ModrinthVersion> Versions { get; } = new(StringComparer.Ordinal);
        public List<Uri> Requests { get; } = [];
        public string? DescriptorHash;
        public Action<JsonObject>? DescriptorEdit;
        public Action<JsonObject>? ManifestEdit;
        public Action<Uri>? OnRequest;
        public Func<HttpRequestMessage, HttpResponseMessage>? Respond;

        public Fixture(string root, string name, string origin = "https://sync.example.test")
        {
            this.origin = origin;
            string area = Path.Combine(root, "server-sync", name);
            Game = Directory.CreateDirectory(Path.Combine(area, "game")).FullName;
            Staging = Path.Combine(area, "staging");
            handler = new FakeHttp(request =>
            {
                var uri = request.RequestUri!;
                Requests.Add(uri); OnRequest?.Invoke(uri);
                if (Respond != null) return Respond(request);
                if (uri.AbsolutePath == "/descriptor.json")
                {
                    Require(request.RequestUri == Endpoint && request.Headers.Accept.Any(a => a.MediaType == "application/json"));
                    var descriptor = JsonSerializer.SerializeToNode(new
                    {
                        descriptorVersion = 1, serverId = ServerId,
                        protocols = new[] { new { major = 1, minor = 0, requiredCapabilities = new[] { "mods-v1" }, targets = new[] {
                            new { targetId = "fabric-1.21.1", minecraft = "1.21.1", loader = "fabric", loaderVersion = "0.19.3", manifestUrl = origin + "/manifest.json", sha512 = DescriptorHash ?? Hash(ManifestBytes()) }
                        } } }
                    })!.AsObject();
                    DescriptorEdit?.Invoke(descriptor);
                    return Response(Encoding.UTF8.GetBytes(descriptor.ToJsonString()));
                }
                if (uri.AbsolutePath == "/manifest.json") return Response(ManifestBytes());
                if (uri.Host == "api.modrinth.com" && uri.AbsolutePath.StartsWith("/v2/version/", StringComparison.Ordinal))
                    return Response(JsonSerializer.SerializeToUtf8Bytes(Versions[uri.Segments[^1]]));
                if (uri.AbsolutePath.StartsWith("/files/", StringComparison.Ordinal)) return Response(Data[Uri.UnescapeDataString(uri.Segments[^1])]);
                throw new Exception("Unexpected fake HTTP request " + uri);
            });
            http = new HttpClient(handler);
        }

        public ServerModSync Engine(bool approveExternal = true) => new(http, approvedExternalOrigins: approveExternal ? [new Uri(origin)] : []);
        public Task<ServerSyncPlan> Plan(ServerModSync engine, bool allowLocal = false) => engine.PlanAsync(Endpoint, Instance, Game, allowLocal);
        public async Task Install(ServerModSync engine) => await engine.ApplyAsync(await engine.StageAsync(await Plan(engine), Staging), () => false);

        public JsonObject Add(string id, string version = "1", string side = "required", string? filename = null, bool modrinth = false, byte[]? data = null)
        {
            filename ??= id + ".jar";
            data ??= Jar(id, version);
            Data[filename] = data;
            string url = (modrinth ? "https://cdn.modrinth.com" : origin) + "/files/" + Uri.EscapeDataString(filename);
            JsonObject source = modrinth ? new() { ["type"] = "modrinth", ["projectId"] = id + "Project", ["versionId"] = id + "Version" }
                : new() { ["type"] = "external", ["url"] = url };
            var file = new JsonObject { ["artifactId"] = id, ["modIds"] = new JsonArray(id), ["client"] = side,
                ["filename"] = filename, ["size"] = data.Length, ["sha512"] = Hash(data), ["dependencies"] = new JsonArray(), ["source"] = source };
            Files.Add(file);
            if (modrinth) Versions[id + "Version"] = new() { Id = id + "Version", ProjectId = id + "Project", GameVersions = ["1.21.1"], Loaders = ["fabric"],
                Files = [new() { Filename = filename, Size = data.Length, Url = url, Hashes = new() { ["sha512"] = Hash(data) } }] };
            return file;
        }

        public string Local(string filename, byte[] bytes)
        {
            Directory.CreateDirectory(Mods);
            string path = Path.Combine(Mods, filename);
            File.WriteAllBytes(path, bytes);
            return path;
        }

        private byte[] ManifestBytes()
        {
            var manifest = JsonSerializer.SerializeToNode(new { type = "Manifest", protocolMajor = 1, protocolMinor = 0, serverId = ServerId,
                targetId = "fabric-1.21.1", revision = Revision, expiresUtc = expiry, minecraft = "1.21.1", loader = "fabric", loaderVersion = "0.19.3", files = Files })!.AsObject();
            ManifestEdit?.Invoke(manifest);
            return Encoding.UTF8.GetBytes(manifest.ToJsonString());
        }
        public void Dispose() { http.Dispose(); handler.Dispose(); }
    }
}
