using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MechanicaLauncher.Core.Instances;
using MechanicaLauncher.Core.Mods;
using MechanicaLauncher.Core.Presets;

internal static class PresetTests
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> check, string root)
    {
        string Area(string name) => Directory.CreateDirectory(Path.Combine(root, "presets", name)).FullName;

        await check("Preset links parse every shareable spelling and round-trip", () =>
        {
            Require(PresetLink.TryParse("mechanica://pack?modrinth=fabulously-optimized&version=6.1.0&server=play.example.com:25566", out var modrinth));
            Require(modrinth.Source == PresetSource.Modrinth && modrinth.Target == "fabulously-optimized" && modrinth.Version == "6.1.0" && modrinth.Server == "play.example.com:25566");
            Require(PresetLink.TryParse(modrinth.ProtocolUri, out var again) && again == modrinth);
            Require(PresetLink.TryParse(modrinth.WebUri, out var web) && web == modrinth);
            Require(modrinth.WebUri.StartsWith(PresetLink.WebBase + "?modrinth=fabulously-optimized"));

            Require(PresetLink.TryParse("https://modrinth.com/modpack/cobblemon-fabric/version/1.5.2", out var page));
            Require(page.Source == PresetSource.Modrinth && page.Target == "cobblemon-fabric" && page.Version == "1.5.2");
            Require(PresetLink.TryParse("  https://cdn.example.com/packs/My%20Pack.mrpack  ", out var direct));
            Require(direct.Source == PresetSource.Url && direct.SourceHost == "cdn.example.com");

            string sha = new string('a', 128);
            var url = PresetLink.FromUrl("https://cdn.example.com/p.mrpack?token=1&x=2", sha);
            Require(PresetLink.TryParse(url.ProtocolUri, out var parsedUrl) && parsedUrl.Target == "https://cdn.example.com/p.mrpack?token=1&x=2" && parsedUrl.Sha512 == sha);
            Require(PresetLink.TryParse(PresetLink.WebBase + "#" + url.Query.TrimStart('?'), out var fragment) && fragment == url);
            Require(PresetLink.ParseArgs(["MechanicaLauncher.exe", "mechanica://connect?server=a&version=1.21", url.ProtocolUri]) == url);
            return Task.CompletedTask;
        });

        await check("Preset links reject unsafe or malformed targets", () =>
        {
            foreach (var text in new[]
            {
                "mechanica://pack?url=http://cdn.example.com/p.mrpack",
                "mechanica://pack?url=file:///C:/Windows/p.mrpack",
                "mechanica://pack?url=https://user:pass@cdn.example.com/p.mrpack",
                "mechanica://pack?url=javascript:alert(1)",
                "mechanica://pack?modrinth=../../etc",
                "mechanica://pack?modrinth=ok&version=a/b",
                "mechanica://pack?modrinth=ok&server=bad host",
                "mechanica://pack?url=https://cdn.example.com/p.mrpack&sha512=xyz",
                "mechanica://event?url=https://cdn.example.com/p.mrpack",
                "mechanica://pack",
                "https://evil.example/MechanicaLauncher/p/?modrinth=ok",
                "https://cdn.example.com/readme.txt",
                "not a link",
            })
                Require(!PresetLink.TryParse(text, out _));
            return Task.CompletedTask;
        });

        await check("Preset summary counts mods and worlds and reads mechanica.json", () =>
        {
            string area = Area("inspect");
            string pack = Path.Combine(area, "creator.mrpack");
            CreatePack(pack, "Creator Pack", [ModFile("mods/sodium.jar"), ModFile("mods/server-only.jar", clientUnsupported: true), ModFile("resourcepacks/x.zip")], new()
            {
                ["overrides/mods/extra.jar"] = "jar",
                ["overrides/saves/Hub World/level.dat"] = "level",
                ["overrides/saves/Hub World/region/r.0.0.mca"] = "region",
                ["client-overrides/saves/Parkour/level.dat"] = "level",
                ["mechanica.json"] = JsonSerializer.Serialize(new
                {
                    formatVersion = 1, author = "Channel", url = "https://youtube.com/@channel", description = "Line 1\nLine 2",
                    server = new { name = "Creator SMP", address = "smp.example.com:25570", syncUrl = "https://smp.example.com/mods.json" }
                })
            });
            var summary = PresetInstaller.Inspect(pack);
            Require(summary.Name == "Creator Pack" && summary.Minecraft == "1.21.1" && summary.Loader == LoaderType.Fabric);
            Require(summary.Mods == 2);
            Require(summary.Worlds.SequenceEqual(["Hub World", "Parkour"]));
            Require(summary.Details.Author == "Channel" && summary.Details.Url == "https://youtube.com/@channel" && summary.Details.Description == "Line 1\nLine 2");
            Require(summary.Details.Server == new PresetServer("Creator SMP", "smp.example.com:25570", "https://smp.example.com/mods.json"));

            string hostile = Path.Combine(area, "hostile.mrpack");
            CreatePack(hostile, "Hostile", [], new()
            {
                ["mechanica.json"] = JsonSerializer.Serialize(new { author = "a\u0007b", url = "javascript:alert(1)", server = new { address = "bad host", syncUrl = "http://x" } })
            });
            var safe = PresetInstaller.Inspect(hostile).Details;
            Require(safe.Author == null && safe.Url == null && safe.Server == null);
            return Task.CompletedTask;
        });

        await check("URL preset downloads, verifies sha512 and imports a world", async () =>
        {
            string area = Area("url");
            string pack = Path.Combine(area, "source.mrpack");
            CreatePack(pack, "World Pack", [], new() { ["overrides/saves/Spawn/level.dat"] = "level", ["overrides/config/a.toml"] = "a=1" });
            byte[] bytes = await File.ReadAllBytesAsync(pack);
            string sha512 = Convert.ToHexString(SHA512.HashData(bytes)).ToLowerInvariant();
            using var handler = new FakeHttp(request =>
            {
                Require(request.RequestUri!.Host == "cdn.example.test");
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
            });
            using var http = new HttpClient(handler);
            var instances = new InstanceManager(Path.Combine(area, "instances"));
            var result = await new PresetInstaller(http).InstallAsync(PresetLink.FromUrl("https://cdn.example.test/world.mrpack", sha512, "play.example.test"), instances);
            Require(result.Instance.Name == "World Pack" && result.Summary.Worlds.SequenceEqual(["Spawn"]));
            Require(File.Exists(Path.Combine(instances.GetGameDir(result.Instance.Id), "saves", "Spawn", "level.dat")));
            Require(result.Server == new PresetServer("World Pack", "play.example.test"));

            int before = instances.GetAllInstances().Count;
            var tampered = PresetLink.FromUrl("https://cdn.example.test/world.mrpack", new string('0', 128));
            bool rejected = false;
            try { await new PresetInstaller(http).InstallAsync(tampered, instances); }
            catch (InvalidDataException) { rejected = true; }
            Require(rejected && instances.GetAllInstances().Count == before);
        });

        await check("Modrinth preset picks the newest release .mrpack", async () =>
        {
            string area = Area("modrinth");
            string pack = Path.Combine(area, "release.mrpack");
            CreatePack(pack, "Release Pack", [], new() { ["overrides/options.txt"] = "fov:1" });
            byte[] bytes = await File.ReadAllBytesAsync(pack);
            string sha1 = Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant();
            string sha512 = Convert.ToHexString(SHA512.HashData(bytes)).ToLowerInvariant();
            string versions = JsonSerializer.Serialize(new object[]
            {
                new { id = "beta1", name = "Beta", version_number = "2.0-beta", version_type = "beta", files = new[] { new { url = "https://cdn.modrinth.test/beta.mrpack", filename = "beta.mrpack", primary = true, size = 1, hashes = new { sha1 = new string('1', 40), sha512 = new string('1', 128) } } } },
                new { id = "rel1", name = "Release", version_number = "1.0", version_type = "release", files = new[] { new { url = "https://cdn.modrinth.test/release.mrpack", filename = "release.mrpack", primary = true, size = bytes.Length, hashes = new { sha1, sha512 } } } },
            });
            using var handler = new FakeHttp(request => request.RequestUri!.AbsolutePath switch
            {
                "/v2/project/release-pack/version" => Json(versions),
                "/release.mrpack" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) },
                var path => throw new Exception("Unexpected request " + path)
            });
            using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.modrinth.test") };
            var instances = new InstanceManager(Path.Combine(area, "instances"));
            var result = await new PresetInstaller(http, new ModrinthClient(http)).InstallAsync(PresetLink.Modrinth("release-pack"), instances);
            Require(result.Instance.Name == "Release Pack" && result.Server == null);
            Require(File.ReadAllText(Path.Combine(instances.GetGameDir(result.Instance.Id), "options.txt")) == "fov:1");
        });
    }

    private static object ModFile(string path, bool clientUnsupported = false) => new
    {
        path, downloads = new[] { "https://cdn.example.test/" + Path.GetFileName(path) }, fileSize = 1,
        hashes = new { sha1 = new string('1', 40), sha512 = new string('1', 128) },
        env = clientUnsupported ? new Dictionary<string, string> { ["client"] = "unsupported", ["server"] = "required" } : null
    };

    private static void CreatePack(string path, string name, object[] files, Dictionary<string, string> entries)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        Write("modrinth.index.json", JsonSerializer.Serialize(new
        {
            formatVersion = 1, game = "minecraft", versionId = "1", name, files,
            dependencies = new Dictionary<string, string> { ["minecraft"] = "1.21.1", ["fabric-loader"] = "0.16.10" }
        }));
        foreach (var (entry, text) in entries) Write(entry, text);
        void Write(string entry, string text)
        {
            using var writer = new StreamWriter(zip.CreateEntry(entry).Open(), new UTF8Encoding(false));
            writer.Write(text);
        }
    }

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    private static void Require(bool condition) { if (!condition) throw new Exception("Preset assertion failed."); }
}
