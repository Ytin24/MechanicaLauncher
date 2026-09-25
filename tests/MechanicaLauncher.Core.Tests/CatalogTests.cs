using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Web;
using MechanicaLauncher.Core.Instances;
using MechanicaLauncher.Core.Mods;
using MechanicaLauncher.Core.Models;

internal static class CatalogTests
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> check, string root)
    {
        await check("Every catalog content type supports exact Minecraft filtering and pagination", async () =>
        {
            foreach (var type in new[] { "mod", "modpack", "shader", "resourcepack", "datapack" })
            {
                using var handler = new FakeHttp(request =>
                {
                    var query = HttpUtility.ParseQueryString(request.RequestUri!.Query);
                    var facets = JsonSerializer.Deserialize<string[][]>(query["facets"]!)!;
                    Require(facets.Any(f => f.SequenceEqual(new[] { "project_type:" + type })));
                    Require(facets.Any(f => f.SequenceEqual(new[] { "versions:1.21.1" })));
                    Require(query["offset"] == "20" && query["limit"] == "20");
                    Require(facets.Any(f => f.Contains("categories:fabric")) == (type == "mod"));
                    Require(facets.Any(f => f.Contains("categories:datapack")) == (type == "datapack"));
                    return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { hits = new object[0], total_hits = 0 }) };
                });
                using var http = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
                await new ModrinthClient(http).SearchAsync("", "1.21.1", type == "mod" ? "fabric" : null, type, offset: 20);
            }
        });
        await check("Modpack releases use the Minecraft filter without inheriting the instance loader", async () =>
        {
            using var handler = new FakeHttp(request =>
            {
                var query = HttpUtility.ParseQueryString(request.RequestUri!.Query);
                Require(JsonSerializer.Deserialize<string[]>(query["game_versions"]!)!.Single() == "1.21.1");
                Require(query["loaders"] == null);
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(new object[0]) };
            });
            using var http = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
            await new ModrinthClient(http).GetProjectVersionsAsync("pack", "1.21.1");
        });
        await check("Datapack search uses the dedicated project facet and datapack loader", async () =>
        {
            using var handler = new FakeHttp(request =>
            {
                var query = HttpUtility.ParseQueryString(request.RequestUri!.Query);
                var facets = JsonSerializer.Deserialize<string[][]>(query["facets"]!)!;
                Require(facets[0][0] == "project_type:datapack");
                Require(facets.Any(f => f.Contains("categories:datapack")));
                Require(!facets.Any(f => f.Contains("categories:fabric")));
                Require(facets.Any(f => f.Contains("versions:1.21.1")));
                Require(query["query"] == "water & sky");
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { hits = new object[0], total_hits = 0 }) };
            });
            using var http = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
            await new ModrinthClient(http).SearchAsync("water & sky", "1.21.1", "fabric", "datapack");
        });

        await check("Project metadata includes Markdown, screenshots and version filters are escaped", async () =>
        {
            using var handler = new FakeHttp(request =>
            {
                if (request.RequestUri!.AbsolutePath.EndsWith("/version"))
                {
                    var query = HttpUtility.ParseQueryString(request.RequestUri.Query);
                    Require(JsonSerializer.Deserialize<string[]>(query["loaders"]!)!.Single() == "fabric");
                    Require(JsonSerializer.Deserialize<string[]>(query["game_versions"]!)!.Single() == "1.21\"test");
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new[] {
                        new { id = "v1", version_type = "beta", date_published = "2026-09-01T00:00:00Z", files = new object[0] } }) };
                }
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new {
                    id = "p1", body = "# Details\n![Image](https://example.test/image.png)", updated = "2026-09-01T00:00:00Z",
                    gallery = new[] { new { url = "https://example.test/image.png", featured = true, title = "Landscape", ordering = 2 } }
                }) };
            });
            using var http = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
            var client = new ModrinthClient(http);
            var project = await client.GetProjectAsync("p1");
            Require(project!.Body.StartsWith("# Details") && project.Gallery.Single().Featured && project.Gallery[0].Ordering == 2);
            var versions = await client.GetProjectVersionsAsync("p1", "1.21\"test", "fabric");
            Require(versions.Single().VersionType == "beta" && versions[0].DatePublished?.Year == 2026);
        });

        await check("Description renders Markdown tables and images with isolated content policy in both themes", () =>
        {
            const string markdown = "# Heading\n\n**Bold** and [link](https://example.test)\n\n![Image](https://example.test/image.png)\n\n| A | B |\n|---|---|\n| 1 | 2 |";
            foreach (var dark in new[] { false, true })
            {
                var html = ProjectDescription.Render(markdown, dark);
                Require(html.Contains("<strong>Bold</strong>") && html.Contains("<table>") && html.Contains("<img"));
                Require(html.Contains("default-src 'none'") && html.Contains("form-action 'none'") && html.Contains("base-uri 'none'"));
                Require(html.Contains(dark ? "#252e28" : "#e0e6e0") && html.Contains("color: inherit !important"));
            }
            return Task.CompletedTask;
        });

        await check("WebView document URI preserves UTF8 HTML including non-ASCII descriptions", () =>
        {
            var html = ProjectDescription.Render("# Описание\n\nSymbols: < > &", true);
            var uri = ProjectDescription.GetNavigationUri(html);
            const string prefix = "data:text/html;charset=utf-8;base64,";
            Require(uri.StartsWith(prefix, StringComparison.Ordinal));
            Require(Encoding.UTF8.GetString(Convert.FromBase64String(uri[prefix.Length..])) == html);
            Require(uri != ProjectDescription.GetNavigationUri("<h1>Unrelated document</h1>"));
            return Task.CompletedTask;
        });

        await check("Shader, resource and datapack ZIPs install only to their respective folders", async () =>
        {
            var game = Path.Combine(root, "content-routes");
            Directory.CreateDirectory(Path.Combine(game, "saves", "Chosen"));
            Directory.CreateDirectory(Path.Combine(game, "saves", "Untouched"));
            await File.WriteAllTextAsync(Path.Combine(game, "saves", "Chosen", "level.dat"), "world");
            await File.WriteAllTextAsync(Path.Combine(game, "saves", "Untouched", "level.dat"), "other world");
            var bytes = Zip(new() { ["pack.mcmeta"] = "{}" });
            using var handler = new FakeHttp(_ => Bytes(bytes));
            using var http = new HttpClient(handler);
            var installer = new ModInstaller(http);
            foreach (var (type, relative) in new[] {
                ("shader", "shaderpacks"), ("resourcepack", "resourcepacks"), ("datapack", "saves/Chosen/datapacks") })
            {
                var version = Version(type, ".zip", bytes);
                version.Loaders = type == "datapack" ? ["datapack"] : [];
                await installer.InstallContentAsync(version, game, type, "1.21.1", worldName: "Chosen");
                Require(File.Exists(Path.Combine(game, relative, type + ".zip")));
            }
            Require(!Directory.Exists(Path.Combine(game, "mods")));
            Require(!Directory.Exists(Path.Combine(game, "saves", "Untouched", "datapacks")));
            Require(await File.ReadAllTextAsync(Path.Combine(game, "saves", "Chosen", "level.dat")) == "world");
            var resource = Path.Combine(game, "resourcepacks", "resourcepack.zip");
            ModInstaller.ToggleMod(resource);
            Require(!File.Exists(resource) && ModInstaller.GetInstalledMods(Path.GetDirectoryName(resource)!, ".zip").Single().Enabled == false);
            ModInstaller.ToggleMod(resource + ".disabled");
            Require(File.Exists(resource));
        });

        await check("Content install rejects vanilla JARs, wrong versions and invalid datapack worlds before downloads", async () =>
        {
            using var handler = new FakeHttp(_ => throw new Exception("Unexpected download"));
            using var http = new HttpClient(handler);
            var installer = new ModInstaller(http);
            var game = Path.Combine(root, "content-invalid");
            var version = Version("test", ".jar", [1, 2, 3]);
            await Expect<InvalidOperationException>(() => installer.InstallContentAsync(version, game, "mod", "1.21.1"));
            await Expect<InvalidDataException>(() => installer.InstallContentAsync(version, game, "mod", "1.20.1", "fabric"));
            await Expect<ArgumentException>(() => installer.InstallContentAsync(version, game, "datapack", "1.21.1", worldName: "../other"));
            await Expect<DirectoryNotFoundException>(() => installer.InstallContentAsync(version, game, "datapack", "1.21.1", worldName: "Missing"));
            await Expect<ArgumentException>(() => installer.InstallContentAsync(version, game, "modpack", "1.21.1"));
            Require(handler.Calls == 0);
        });

        await check("A required JAR cannot be silently put into a resourcepack folder", async () =>
        {
            var rootVersion = Version("pack", ".zip", Zip(new() { ["pack.mcmeta"] = "{}" }));
            var dependency = Version("dep", ".jar", [1, 2, 3]);
            rootVersion.Dependencies.Add(new() { VersionId = dependency.Id, DependencyType = "required" });
            using var handler = new FakeHttp(request =>
            {
                Require(request.RequestUri!.AbsolutePath == "/v2/version/dep");
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(dependency) };
            });
            using var http = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
            var game = Path.Combine(root, "content-wrong-dependency");
            await Expect<InvalidDataException>(() => new ModInstaller(http, new ModrinthClient(http))
                .InstallContentAsync(rootVersion, game, "resourcepack", "1.21.1"));
            Require(!Directory.Exists(Path.Combine(game, "resourcepacks")));
        });

        await check("Failed hash verification preserves the previous resourcepack", async () =>
        {
            var game = Path.Combine(root, "content-hash");
            Directory.CreateDirectory(Path.Combine(game, "resourcepacks"));
            var existing = Path.Combine(game, "resourcepacks", "pack.zip");
            await File.WriteAllTextAsync(existing, "previous");
            var version = Version("pack", ".zip", [1, 2, 3]);
            using var handler = new FakeHttp(_ => Bytes([3, 2, 1]));
            using var http = new HttpClient(handler);
            await Expect<InvalidDataException>(() => new ModInstaller(http).InstallContentAsync(version, game, "resourcepack", "1.21.1"));
            Require(await File.ReadAllTextAsync(existing) == "previous");
            Require(Directory.GetFiles(Path.GetDirectoryName(existing)!, "*", SearchOption.AllDirectories).Length == 1);
        });

        await check("Cancellation leaves no partial content files", async () =>
        {
            var game = Path.Combine(root, "content-cancel");
            var version = Version("pack", ".zip", [1, 2, 3]);
            using var cancellation = new CancellationTokenSource();
            using var handler = new FakeHttp(async (_, ct) =>
            {
                cancellation.Cancel();
                await Task.Delay(Timeout.Infinite, ct);
                throw new Exception("Cancelled request returned");
            });
            using var http = new HttpClient(handler);
            await Expect<OperationCanceledException>(() => new ModInstaller(http)
                .InstallContentAsync(version, game, "shader", "1.21.1", cancellationToken: cancellation.Token));
            Require(!Directory.Exists(game) || Directory.GetFiles(game, "*", SearchOption.AllDirectories).Length == 0);
        });

        await check("A downloaded modpack creates its own instance with its declared loader", async () =>
        {
            var manager = new InstanceManager(Path.Combine(root, "catalog-modpack"));
            var existing = manager.CreateInstance("Current", "1.20.1");
            var world = Path.Combine(manager.GetGameDir(existing.Id), "saves", "world.dat");
            await File.WriteAllTextAsync(world, "keep world");
            var index = JsonSerializer.Serialize(new {
                formatVersion = 1, game = "minecraft", versionId = "1", name = "Catalog pack",
                dependencies = new Dictionary<string, string> { ["minecraft"] = "1.21.1", ["fabric-loader"] = "0.16.10" },
                files = new object[0]
            });
            var bytes = Zip(new() { ["modrinth.index.json"] = index, ["overrides/config/example.txt"] = "pack config" });
            var version = Version("pack", ".mrpack", bytes);
            using var handler = new FakeHttp(_ => Bytes(bytes));
            using var http = new HttpClient(handler);
            var created = await new ModInstaller(http).ImportModpackAsync(version, manager);
            Require(created.Id != existing.Id && created.McVersion == "1.21.1" && created.Loader == LoaderType.Fabric && created.LoaderVersion == "0.16.10");
            Require(manager.GetAllInstances().Count == 2 && await File.ReadAllTextAsync(world) == "keep world");
            Require(File.Exists(Path.Combine(manager.GetGameDir(created.Id), "config", "example.txt")));
            Require(!File.Exists(Path.Combine(manager.GetGameDir(existing.Id), "config", "example.txt")));
        });
    }

    private static ModrinthVersion Version(string name, string extension, byte[] bytes) => new()
    {
        Id = name, ProjectId = name, Name = name, VersionNumber = "1", GameVersions = ["1.21.1"],
        Files = [new() { Filename = name + extension, Url = "https://example.test/" + name + extension,
            Size = bytes.Length, Primary = true, Hashes = new() {
                ["sha1"] = Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant(),
                ["sha512"] = Convert.ToHexString(SHA512.HashData(bytes)).ToLowerInvariant() } }]
    };

    private static byte[] Zip(Dictionary<string, string> entries)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
            foreach (var (name, text) in entries)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open());
                writer.Write(text);
            }
        return stream.ToArray();
    }

    private static HttpResponseMessage Bytes(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
    private static void Require(bool value) { if (!value) throw new Exception("Catalog assertion failed."); }
    private static async Task Expect<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }
}
