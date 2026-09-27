using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Web;
using MechanicaLauncher.Core.Models;
using MechanicaLauncher.Core.Mods;

internal static class InstalledContentIndexTests
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> check, string root)
    {
        string Area(string name) => Directory.CreateDirectory(Path.Combine(root, "content-index", name)).FullName;

        await check("Installed content matches exact hashes after rename and disable and reuses persistent titles", async () =>
        {
            var game = Area("renamed");
            var file = MakeZip(game, "mods", "my-custom-name.jar", new() { ["fabric.mod.json"] = "{\"name\":\"Local Name\",\"version\":\"1.0\"}" });
            var hash = Hash(file);
            using var handler = new FakeHttp(async (request, ct) =>
            {
                if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/v2/version_files")
                {
                    Require((await Hashes(request, ct)).Single() == hash);
                    return Json(new Dictionary<string, ModrinthVersion> { [hash] = Version("project", "release", "2.0") });
                }
                Require(request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == "/v2/projects");
                Require(ProjectIds(request).Single() == "project");
                return Json(new[] { new ModrinthProjectInfo { Id = "project", Title = "Catalog Name", IconUrl = "https://example.test/icon.png" } });
            });
            using var http = Client(handler);
            var index = new InstalledContentIndex(new ModrinthClient(http));
            var first = (await index.ScanAsync(game, "mod")).Single();
            Require(first.DisplayName == "Catalog Name" && first.DisplayVersion == "2.0" && first.ProjectId == "project" && first.VersionId == "release");
            Require(first.Match == CatalogMatch.Matched && first.Sha1 == hash && first.Enabled && first.SizeBytes == new FileInfo(file).Length);
            var renamed = Path.Combine(Path.GetDirectoryName(file)!, "another-name.jar.disabled");
            File.Move(file, renamed);
            var second = (await new InstalledContentIndex(new ModrinthClient(http)).ScanAsync(game, "mod")).Single();
            Require(handler.Calls == 2 && !second.Enabled && second.FilePath == renamed && second.FileName == "another-name.jar");
            Require(second.ProjectId == first.ProjectId && second.VersionId == first.VersionId && second.IconUrl == first.IconUrl && second.DisplayName == first.DisplayName);
            var files = JsonNode.Parse(await File.ReadAllTextAsync(CachePath(game)))!["Files"]!.AsObject();
            Require(files.Count == 1 && files.Single().Key.EndsWith("another-name.jar.disabled", StringComparison.Ordinal));
        });

        await check("Replacing the same filename with equal size and timestamps invalidates the content identity", async () =>
        {
            var game = Area("replacement");
            var file = Path.Combine(Directory.CreateDirectory(Path.Combine(game, "mods")).FullName, "same.jar");
            await File.WriteAllBytesAsync(file, [1, 2, 3]);
            var firstHash = Hash(file);
            using var handler = new FakeHttp(async (request, ct) =>
            {
                if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/v2/version_files")
                {
                    var hash = (await Hashes(request, ct)).Single();
                    var id = hash == firstHash ? "old" : "new";
                    return Json(new Dictionary<string, ModrinthVersion> { [hash] = Version(id, id + "-version", id) });
                }
                Require(request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == "/v2/projects");
                return Json(ProjectIds(request).Select(id => new ModrinthProjectInfo { Id = id, Title = id + " title" }).ToArray());
            });
            using var http = Client(handler);
            var index = new InstalledContentIndex(new ModrinthClient(http));
            var first = (await index.ScanAsync(game, "mod")).Single();
            var timestamp = File.GetLastWriteTimeUtc(file);
            await File.WriteAllBytesAsync(file, [3, 2, 1]);
            File.SetLastWriteTimeUtc(file, timestamp);
            var second = (await index.ScanAsync(game, "mod")).Single();
            Require(first.ProjectId == "old" && second.ProjectId == "new" && second.VersionId == "new-version");
            Require(first.Sha1 != second.Sha1 && first.SizeBytes == second.SizeBytes && second.DisplayName == "new title" && handler.Calls == 4);
        });

        await check("Offline installed names come from Fabric Quilt Forge NeoForge and legacy metadata", async () =>
        {
            var game = Area("local-metadata");
            MakeZip(game, "mods", "fabric.jar", new() { ["fabric.mod.json"] = "{\"name\":\"Fabric Local\",\"version\":\"1.0\",\"description\":\"first\nsecond\"}" });
            MakeZip(game, "mods", "quilt.jar", new() { ["quilt.mod.json"] = "{\"quilt_loader\":{\"version\":\"2.0\",\"metadata\":{\"name\":\"Quilt Local\"}}}" });
            MakeZip(game, "mods", "forge.jar", new() { ["META-INF/mods.toml"] = "modLoader=\"javafml\"\n[[mods]]\nversion='3.0'\ndescription='''\n[[mods]]\ndisplayName='Wrong'\n'''\ndisplayName='Forge Local' # comment\n[[dependencies.example]]\nversion='wrong'" });
            MakeZip(game, "mods", "neo.jar", new() { ["META-INF/neoforge.mods.toml"] = "[[mods]]\ndisplayName=\"NeoForge \\\"Local\\\"\"\nversion=\"${file.jarVersion}\"",
                ["META-INF/MANIFEST.MF"] = "Manifest-Version: 1.0\r\nImplementation-Version: 4.0\r\n" });
            MakeZip(game, "mods", "legacy.jar", new() { ["mcmod.info"] = "[{\"name\":\"Legacy Local\",\"version\":\"5.0\"}]" });
            MakeZip(game, "mods", "wrapped.jar", new() { ["mcmod.info"] = "{\"modList\":[{\"name\":\"Wrapped Local\",\"version\":\"6.0\"}]}" });
            MakeZip(game, "mods", "unknown.jar", new() { ["other.json"] = "{}" });
            using var handler = new FakeHttp(request =>
            {
                Require(request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/v2/version_files");
                throw new HttpRequestException("offline");
            });
            using var http = Client(handler);
            var index = new InstalledContentIndex(new ModrinthClient(http));
            var content = (await index.ScanAsync(game, "mod")).ToDictionary(c => c.FileName);
            foreach (var (file, name, version) in new[] { ("fabric.jar", "Fabric Local", "1.0"), ("quilt.jar", "Quilt Local", "2.0"),
                ("forge.jar", "Forge Local", "3.0"), ("neo.jar", "NeoForge \"Local\"", "4.0"), ("legacy.jar", "Legacy Local", "5.0"),
                ("wrapped.jar", "Wrapped Local", "6.0"), ("unknown.jar", "unknown.jar", "") })
                Require(content[file].DisplayName == name && content[file].DisplayVersion == version);
            Require(content.Values.All(c => c.Match == CatalogMatch.Unavailable && c.ProjectId == null && c.VersionId == null));
            Require(handler.Calls == 1);
            await index.ScanAsync(game, "mod");
            Require(handler.Calls == 2);
        });

        await check("ZIP content uses pack descriptions and only the selected datapack world", async () =>
        {
            var game = Area("packs");
            foreach (var world in new[] { "Chosen", "Other" })
                await File.WriteAllTextAsync(Path.Combine(Directory.CreateDirectory(Path.Combine(game, "saves", world)).FullName, "level.dat"), "world");
            var entries = new Dictionary<string, string> { ["pack.mcmeta"] = "{\"pack\":{\"description\":{\"text\":\"§aClear\",\"extra\":[{\"text\":\" Skies\"}]}}}" };
            MakeZip(game, "resourcepacks", "resource.zip", entries);
            MakeZip(game, "shaderpacks", "shader.zip", entries);
            MakeZip(game, "saves/Chosen/datapacks", "selected.zip", entries);
            MakeZip(game, "saves/Other/datapacks", "other.zip", entries);
            using var handler = new FakeHttp(request =>
            {
                Require(request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/v2/version_files");
                return Json(new Dictionary<string, ModrinthVersion>());
            });
            using var http = Client(handler);
            var index = new InstalledContentIndex(new ModrinthClient(http));
            foreach (var type in new[] { "resourcepack", "shader", "datapack" })
            {
                var item = (await index.ScanAsync(game, type, type == "datapack" ? "Chosen" : null)).Single();
                Require(item.DisplayName == "Clear Skies" && item.Match == CatalogMatch.NotFound && item.ProjectId == null);
                if (type == "datapack") Require(item.FileName == "selected.zip");
            }
            var cached = JsonNode.Parse(await File.ReadAllTextAsync(CachePath(game)))!["Files"]!.AsObject();
            Require(cached.Count == 3);
        });

        await check("Network failure is not negative cached and successful misses expire after fifteen minutes", async () =>
        {
            var game = Area("negative-cache");
            var file = MakeZip(game, "mods", "local.jar", new() { ["fabric.mod.json"] = "{\"name\":\"Local\"}" });
            int mode = 0;
            using var handler = new FakeHttp(request =>
            {
                if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == "/v2/projects")
                    return Json(new[] { new ModrinthProjectInfo { Id = "known", Title = "Known Project" } });
                Require(request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/v2/version_files");
                if (mode == 0) throw new HttpRequestException("offline");
                return Json(mode == 1 ? new Dictionary<string, ModrinthVersion>() : new() { [Hash(file)] = Version("known", "v1", "1") });
            });
            using var http = Client(handler);
            var index = new InstalledContentIndex(new ModrinthClient(http));
            Require((await index.ScanAsync(game, "mod")).Single().Match == CatalogMatch.Unavailable);
            mode = 1;
            Require((await index.ScanAsync(game, "mod")).Single().Match == CatalogMatch.NotFound);
            Require((await index.ScanAsync(game, "mod")).Single().Match == CatalogMatch.NotFound && handler.Calls == 2);
            var cache = JsonNode.Parse(await File.ReadAllTextAsync(CachePath(game)))!;
            foreach (var entry in cache["Files"]!.AsObject()) entry.Value!["NotFoundAt"] = DateTimeOffset.UtcNow.AddMinutes(-16).ToString("O");
            await File.WriteAllTextAsync(CachePath(game), cache.ToJsonString());
            mode = 0;
            Require((await index.ScanAsync(game, "mod")).Single().Match == CatalogMatch.Unavailable && handler.Calls == 3);
            mode = 2;
            Require((await index.ScanAsync(game, "mod")).Single().Match == CatalogMatch.Matched && handler.Calls == 5);
            mode = 0;
            var retained = (await index.ScanAsync(game, "mod")).Single();
            Require(retained.Match == CatalogMatch.Matched && retained.DisplayName == "Known Project" && handler.Calls == 5);
        });

        await check("Project metadata failure retains exact installed IDs and retries only project metadata", async () =>
        {
            var game = Area("project-offline");
            var file = MakeZip(game, "mods", "custom.jar", new() { ["fabric.mod.json"] = "{\"name\":\"Local Name\",\"version\":\"local\"}" });
            bool offline = true;
            int hashCalls = 0, projectCalls = 0;
            using var handler = new FakeHttp(request =>
            {
                if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/v2/version_files")
                {
                    hashCalls++;
                    return Json(new Dictionary<string, ModrinthVersion> { [Hash(file)] = Version("project", "exact-version", "2.0") });
                }
                Require(request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == "/v2/projects");
                projectCalls++;
                return offline ? new(HttpStatusCode.ServiceUnavailable) : Json(new[] { new ModrinthProjectInfo { Id = "project", Title = "Project Title" } });
            });
            using var http = Client(handler);
            var first = (await new InstalledContentIndex(new ModrinthClient(http)).ScanAsync(game, "mod")).Single();
            Require(first.Match == CatalogMatch.Matched && first.ProjectId == "project" && first.VersionId == "exact-version" && first.DisplayName == "Local Name");
            offline = false;
            var second = (await new InstalledContentIndex(new ModrinthClient(http)).ScanAsync(game, "mod")).Single();
            Require(second.Match == CatalogMatch.Matched && second.DisplayName == "Project Title" && hashCalls == 1 && projectCalls == 2);
        });

        await check("Cancelling an index scan preserves the previous cache and a later scan retries", async () =>
        {
            var game = Area("cancel");
            MakeZip(game, "mods", "cached.jar", new() { ["fabric.mod.json"] = "{\"name\":\"Cached\"}" });
            bool cancelRequest = false;
            using var cancel = new CancellationTokenSource();
            using var handler = new FakeHttp(async (request, ct) =>
            {
                Require(request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/v2/version_files");
                if (cancelRequest)
                {
                    cancel.Cancel();
                    await Task.Delay(Timeout.Infinite, ct);
                }
                return Json(new Dictionary<string, ModrinthVersion>());
            });
            using var http = Client(handler);
            var index = new InstalledContentIndex(new ModrinthClient(http));
            await index.ScanAsync(game, "mod");
            var previous = await File.ReadAllTextAsync(CachePath(game));
            MakeZip(game, "mods", "new.jar", new() { ["fabric.mod.json"] = "{\"name\":\"New\"}" });
            cancelRequest = true;
            await Throws<OperationCanceledException>(() => index.ScanAsync(game, "mod", cancellationToken: cancel.Token));
            Require(await File.ReadAllTextAsync(CachePath(game)) == previous);
            Require(Directory.GetFiles(Path.GetDirectoryName(CachePath(game))!, "*.tmp").Length == 0);
            cancelRequest = false;
            Require((await index.ScanAsync(game, "mod")).Count == 2 && handler.Calls == 3);
        });

        await check("Bulk project metadata deduplicates IDs and batches at one hundred", async () =>
        {
            var sizes = new List<int>();
            using var handler = new FakeHttp(request =>
            {
                Require(request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == "/v2/projects");
                var ids = ProjectIds(request);
                sizes.Add(ids.Length);
                return Json(ids.Select(id => new ModrinthProjectInfo { Id = id, Title = id }).ToArray());
            });
            using var http = Client(handler);
            var ids = Enumerable.Range(0, 205).Select(i => "project" + i).ToArray();
            var projects = await new ModrinthClient(http).GetProjectsAsync(ids.Concat(ids.Take(3)).Append(""));
            Require(projects.Count == 205 && sizes.SequenceEqual([100, 100, 5]));
        });

        await check("Local names are reported before a slow catalog response", async () =>
        {
            var game = Area("local-progress");
            MakeZip(game, "mods", "custom.jar", new() { ["fabric.mod.json"] = "{\"name\":\"Local Preview\",\"version\":\"1\"}" });
            var ready = new TaskCompletionSource<IReadOnlyList<IndexedContent>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var handler = new FakeHttp(async (request, ct) =>
            {
                Require(request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/v2/version_files");
                await release.Task.WaitAsync(ct);
                return Json(new Dictionary<string, ModrinthVersion>());
            });
            using var http = Client(handler);
            var scan = new InstalledContentIndex(new ModrinthClient(http)).ScanAsync(game, "mod",
                progress: new SnapshotProgress(value => ready.TrySetResult(value)));
            try
            {
                var local = (await ready.Task.WaitAsync(TimeSpan.FromSeconds(3))).Single();
                Require(!scan.IsCompleted && local.DisplayName == "Local Preview" && local.DisplayVersion == "1" && local.Match == CatalogMatch.Unavailable);
            }
            finally { release.TrySetResult(); }
            Require((await scan).Single().Match == CatalogMatch.NotFound);
        });

        await check("A null catalog lookup response is unavailable and can be retried", async () =>
        {
            var game = Area("invalid-response");
            MakeZip(game, "mods", "manual.jar", new() { ["fabric.mod.json"] = "{\"name\":\"Manual\"}" });
            using var handler = new FakeHttp(request =>
            {
                Require(request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/v2/version_files");
                return Json<object?>(null);
            });
            using var http = Client(handler);
            var index = new InstalledContentIndex(new ModrinthClient(http));
            Require((await index.ScanAsync(game, "mod")).Single().Match == CatalogMatch.Unavailable);
            Require((await index.ScanAsync(game, "mod")).Single().Match == CatalogMatch.Unavailable && handler.Calls == 2);
        });
    }

    private static string MakeZip(string game, string folder, string file, Dictionary<string, string> entries)
    {
        var path = Path.Combine(Directory.CreateDirectory(Path.Combine(game, folder)).FullName, file);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
        {
            using var writer = new StreamWriter(zip.CreateEntry(name).Open());
            writer.Write(content);
        }
        return path;
    }
    private static string Hash(string file) => Convert.ToHexString(SHA1.HashData(File.ReadAllBytes(file))).ToLowerInvariant();
    private static string CachePath(string game) => Path.Combine(game, ".mechanica", "content-index.json");
    private static HttpClient Client(FakeHttp handler) => new(handler) { BaseAddress = new Uri("https://example.test") };
    private static HttpResponseMessage Json<T>(T value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
    private static ModrinthVersion Version(string project, string version, string number) => new() { ProjectId = project, Id = version, VersionNumber = number };
    private static string[] ProjectIds(HttpRequestMessage request) => JsonSerializer.Deserialize<string[]>(HttpUtility.ParseQueryString(request.RequestUri!.Query)["ids"]!)!;
    private static async Task<string[]> Hashes(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
        Require(body.RootElement.GetProperty("algorithm").GetString() == "sha1");
        return body.RootElement.GetProperty("hashes").EnumerateArray().Select(h => h.GetString()!).ToArray();
    }
    private static void Require(bool value) { if (!value) throw new Exception("Installed content index assertion failed."); }
    private static async Task Throws<T>(Func<Task> action) where T : Exception
    {
        try { await action(); } catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }
    private sealed class SnapshotProgress(Action<IReadOnlyList<IndexedContent>> report) : IProgress<IReadOnlyList<IndexedContent>>
    {
        public void Report(IReadOnlyList<IndexedContent> value) => report(value);
    }
}
