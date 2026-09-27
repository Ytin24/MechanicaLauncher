using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MechanicaLauncher.Core.Instances;
using MechanicaLauncher.Core.Mods;

internal static class ModpackPrivateStateTests
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> check, string root)
    {
        string Area(string name) => Directory.CreateDirectory(Path.Combine(root, "modpack-private-state", name)).FullName;

        await check("Modpack index cannot write launcher state before any HTTP or existing file change", async () =>
        {
            int number = 0;
            foreach (string path in PrivatePaths)
            {
                string area = Area("index-" + number++);
                string game = Path.Combine(area, "game");
                PreserveFiles(game);
                var before = Snapshot(game);
                string pack = Path.Combine(area, "forged.mrpack");
                CreatePack(pack, [PackFile("mods/allowed.jar"), PackFile(path)], new() { ["overrides/config/user.json"] = "untrusted" });
                using var handler = new FakeHttp(_ => Response());
                using var http = new HttpClient(handler);
                await Invalid(() => new ModpackInstaller(http).InstallAsync(pack, game));
                Require(handler.Calls == 0);
                RequireSame(before, Snapshot(game));
            }
        });

        await check("Modpack overrides cannot inject private journals or publish a failed import", async () =>
        {
            int number = 0;
            foreach (string prefix in new[] { "overrides/", "client-overrides/" })
            foreach (string path in PrivatePaths)
            {
                string area = Area("override-" + number++);
                string game = Path.Combine(area, "game");
                PreserveFiles(game);
                var before = Snapshot(game);
                string pack = Path.Combine(area, "forged.mrpack");
                CreatePack(pack, [PackFile("mods/allowed.jar")], new() { [prefix + path] = "forged journal", ["overrides/scripts/recipes.zs"] = "new script" });
                using var handler = new FakeHttp(_ => Response());
                using var http = new HttpClient(handler);
                var installer = new ModpackInstaller(http);
                await Invalid(() => installer.InstallAsync(pack, game));
                Require(handler.Calls == 0);
                RequireSame(before, Snapshot(game));
                var manager = new InstanceManager(Path.Combine(area, "instances"));
                await Invalid(() => installer.ImportAsync(pack, manager));
                Require(manager.GetAllInstances().Count == 0 && handler.Calls == 0);
            }
        });

        await check("Ignored server overrides and nested user mechanics remain ordinary modpack content", async () =>
        {
            string area = Area("ordinary");
            string pack = Path.Combine(area, "normal.mrpack");
            var expected = new Dictionary<string, string>
            {
                ["config/.mechanica/user.json"] = "nested user configuration",
                [".mechanica-public/readme.txt"] = "public pack folder",
                [".mechanica.json"] = "ordinary root file",
                ["scripts/recipes.zs"] = "crafttweaker",
                ["kubejs/server_scripts/recipe.js"] = "kubejs",
                ["defaultconfigs/settings.toml"] = "defaults"
            };
            var entries = expected.ToDictionary(e => "overrides/" + e.Key, e => e.Value);
            entries["server-overrides/.mechanica/server-sync-journal.json"] = "ignored server entry";
            CreatePack(pack, [], entries);
            using var handler = new FakeHttp(_ => throw new Exception("Unexpected download."));
            using var http = new HttpClient(handler);
            var installer = new ModpackInstaller(http);
            var manager = new InstanceManager(Path.Combine(area, "instances"));
            var imported = await installer.ImportAsync(pack, manager);
            var game = manager.GetGameDir(imported.Id);
            Require(imported.Loader == LoaderType.None && imported.McVersion == "1.21.1");
            foreach (var file in expected) Require(await File.ReadAllTextAsync(Path.Combine(game, file.Key)) == file.Value);
            Require(!Directory.Exists(Path.Combine(game, ".mechanica")) && handler.Calls == 0);
            string exported = Path.Combine(area, "roundtrip.mrpack");
            await ModpackInstaller.ExportAsync(imported, manager, exported);
            var roundtrip = await installer.ImportAsync(exported, manager);
            foreach (var file in expected) Require(await File.ReadAllTextAsync(Path.Combine(manager.GetGameDir(roundtrip.Id), file.Key)) == file.Value);
        });

        await check("Modpack export filters private state from stale imported-file sidecars and retains normal roundtrip", async () =>
        {
            string area = Area("export");
            var manager = new InstanceManager(Path.Combine(area, "instances"));
            var instance = manager.CreateInstance("Export private state", "1.21.1", LoaderType.Fabric, "0.19.3");
            string game = manager.GetGameDir(instance.Id);
            var expected = new Dictionary<string, string>
            {
                ["mods/personal.jar"] = "mod bytes",
                ["mods/disabled.jar.disabled"] = "disabled bytes",
                ["config/user.json"] = "user settings",
                ["config/.mechanica/keep.json"] = "nested configuration",
                ["saves/world/level.dat"] = "world bytes",
                ["kubejs/server_scripts/recipes.js"] = "recipe script",
                ["scripts/recipes.zs"] = "other script",
                ["defaultconfigs/server.toml"] = "defaults",
                ["custom/pack-settings.json"] = "imported custom file",
                ["options.txt"] = "personal settings"
            };
            foreach (var file in expected) Write(game, file.Key, file.Value);
            const string secret = "PRIVATE-LAUNCHER-STATE-MUST-NOT-EXPORT";
            foreach (string name in new[] { "server-sync.json", "server-sync-journal.json", "mod-update-journal.json", "content-index.json", "backups/copy.jar" })
                Write(game, ".mechanica/" + name, secret);
            var sidecar = expected.Keys.Concat(new[] { ".mechanica/server-sync.json", ".MECHANICA\\server-sync-journal.json", "mods/../.mechanica/mod-update-journal.json", ".mechanica/content-index.json", ".mechanica/backups/copy.jar" }).ToArray();
            string sidecarPath = Path.Combine(manager.GetInstanceDir(instance.Id), "modpack-files.json");
            await File.WriteAllTextAsync(sidecarPath, JsonSerializer.Serialize(sidecar));
            instance.IconPath = Path.GetRelativePath(manager.GetInstanceDir(instance.Id), Path.Combine(game, ".mechanica", "server-sync-journal.json"));
            byte[] sidecarBefore = await File.ReadAllBytesAsync(sidecarPath);
            string exported = Path.Combine(area, "exported.mrpack");
            await ModpackInstaller.ExportAsync(instance, manager, exported);
            using (var zip = ZipFile.OpenRead(exported))
            {
                Require(zip.Entries.All(e => !e.FullName.StartsWith("overrides/.mechanica/", StringComparison.OrdinalIgnoreCase)));
                Require(zip.GetEntry("icon.png") == null);
                foreach (var entry in zip.Entries)
                {
                    using var reader = new StreamReader(entry.Open());
                    Require(!(await reader.ReadToEndAsync()).Contains(secret, StringComparison.Ordinal));
                }
                Require(expected.Keys.All(path => zip.GetEntry("overrides/" + path) != null));
            }
            Require(sidecarBefore.SequenceEqual(await File.ReadAllBytesAsync(sidecarPath)));
            Require(await File.ReadAllTextAsync(Path.Combine(game, ".mechanica", "server-sync-journal.json")) == secret);
            using var handler = new FakeHttp(_ => throw new Exception("Roundtrip must be offline."));
            using var http = new HttpClient(handler);
            var roundtrip = await new ModpackInstaller(http).ImportAsync(exported, manager);
            string restored = manager.GetGameDir(roundtrip.Id);
            Require(roundtrip.McVersion == instance.McVersion && roundtrip.Loader == instance.Loader && roundtrip.LoaderVersion == instance.LoaderVersion);
            foreach (var file in expected) Require(await File.ReadAllTextAsync(Path.Combine(restored, file.Key)) == file.Value);
            Require(!Directory.Exists(Path.Combine(restored, ".mechanica")) && handler.Calls == 0);
        });
    }

    private static readonly string[] PrivatePaths = [".mechanica/server-sync-journal.json", ".MECHANICA/mod-update-journal.json", ".mechanica\\content-index.json",
        "./.mechanica/server-sync.json", "mods/../.mechanica/server-sync.json", ".mechanica. /server-sync-journal.json", ".mechanica"];
    private static readonly byte[] Download = "verified content"u8.ToArray();
    private static object PackFile(string path) => new { path, hashes = new { sha1 = Convert.ToHexString(SHA1.HashData(Download)), sha512 = Convert.ToHexString(SHA512.HashData(Download)) },
        fileSize = Download.Length, downloads = new[] { "https://example.test/file" } };
    private static HttpResponseMessage Response() => new(HttpStatusCode.OK) { Content = new ByteArrayContent(Download) };
    private static void CreatePack(string path, object[] files, Dictionary<string, string> entries)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using (var writer = new StreamWriter(zip.CreateEntry("modrinth.index.json").Open()))
            writer.Write(JsonSerializer.Serialize(new { formatVersion = 1, game = "minecraft", name = "Private state check", versionId = "1", files,
                dependencies = new Dictionary<string, string> { ["minecraft"] = "1.21.1" } }));
        foreach (var entry in entries)
        {
            using var writer = new StreamWriter(zip.CreateEntry(entry.Key).Open());
            writer.Write(entry.Value);
        }
    }
    private static void Write(string root, string relative, string value)
    {
        string path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, value);
    }
    private static void PreserveFiles(string game)
    {
        Write(game, ".mechanica/server-sync-journal.json", "existing private journal");
        Write(game, "saves/world/level.dat", "existing world");
        Write(game, "config/user.json", "existing config");
        Write(game, "mods/personal.jar", "existing mod");
    }
    private static Dictionary<string, string> Snapshot(string game) => Directory.EnumerateFiles(game, "*", SearchOption.AllDirectories)
        .ToDictionary(f => Path.GetRelativePath(game, f), f => Convert.ToHexString(SHA512.HashData(File.ReadAllBytes(f))), StringComparer.OrdinalIgnoreCase);
    private static void RequireSame(Dictionary<string, string> expected, Dictionary<string, string> actual) =>
        Require(expected.Count == actual.Count && expected.All(p => actual.TryGetValue(p.Key, out var hash) && hash == p.Value));
    private static void Require(bool condition) { if (!condition) throw new Exception("Modpack private-state assertion failed."); }
    private static async Task Invalid(Func<Task> action)
    {
        try { await action(); }
        catch (InvalidDataException) { return; }
        throw new Exception("Expected modpack private-state rejection.");
    }
}
