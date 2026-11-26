using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MechanicaLauncher.Core.Game;
using MechanicaLauncher.Core.Instances;
using MechanicaLauncher.Core.IO;
using MechanicaLauncher.Core.Models;

// Faster instance creation: shared client jar, verified-file index, parallel libraries and loader profile reuse.
internal static class BuildCreationTests
{
    internal static async Task RunAsync(Func<string, Func<Task>, Task> check, string root)
    {
        await check("A second instance copies the shared client jar instead of downloading it", async () =>
        {
            var manager = new InstanceManager(Path.Combine(root, "shared-client"));
            var client = "client bytes"u8.ToArray();
            var meta = new VersionMeta
            {
                Id = "1.21.1",
                Downloads = new() { ["client"] = new() { Url = "https://example.test/client.jar", Sha1 = Hash(client), Size = client.Length } }
            };
            using var handler = new FakeHttp(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(client) });
            using var http = new HttpClient(handler);
            foreach (var name in new[] { "First", "Second" })
            {
                var instance = manager.CreateInstance(name, "1.21.1");
                await new AssetDownloader(manager.SharedDir, manager.GetGameDir(instance.Id), http).DownloadVersionAsync(meta);
                Require(File.ReadAllBytes(Path.Combine(manager.GetGameDir(instance.Id), "versions", "1.21.1", "1.21.1.jar")).SequenceEqual(client),
                    "The instance client jar is missing or wrong.");
            }
            Require(handler.Calls == 1, $"The client jar was downloaded {handler.Calls} times.");
        });

        await check("Libraries download in parallel and each artifact only once", async () =>
        {
            var manager = new InstanceManager(Path.Combine(root, "parallel-libraries"));
            int active = 0, peak = 0;
            var gate = new object();
            using var handler = new FakeHttp(async (request, token) =>
            {
                var now = Interlocked.Increment(ref active);
                lock (gate) peak = Math.Max(peak, now);
                await Task.Delay(40, token);
                Interlocked.Decrement(ref active);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(request.RequestUri!.AbsolutePath)) };
            });
            using var http = new HttpClient(handler);
            var libraries = Enumerable.Range(0, 12).Select(i => new Library { Name = $"test:lib{i}:1", Url = "https://example.test/" })
                .Append(new Library { Name = "test:lib0:1", Url = "https://example.test/" }).ToList();
            var game = manager.GetGameDir(manager.CreateInstance("Parallel", "1.21.1").Id);
            await new AssetDownloader(manager.SharedDir, game, http).DownloadVersionAsync(new VersionMeta { Id = "1.21.1", Libraries = libraries });
            Require(handler.Calls == 12, $"Expected 12 requests, got {handler.Calls}.");
            Require(peak > 1, "Libraries were downloaded one at a time.");
        });

        await check("Verified files are trusted until they change on disk", async () =>
        {
            var path = Path.Combine(root, "verified", "asset.bin");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var data = "asset"u8.ToArray();
            await File.WriteAllBytesAsync(path, data);
            Require(await FileDownloader.IsValidAsync(path, Hash(data), data.Length), "A correct file was rejected.");
            await File.WriteAllBytesAsync(path, "ASSET"u8.ToArray());
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
            Require(!await FileDownloader.IsValidAsync(path, Hash(data), data.Length), "A changed file was trusted from the cache.");

            var store = Path.Combine(root, "verified", "index.json");
            VerifiedFiles.Attach(store);
            await File.WriteAllBytesAsync(path, data);
            Require(await FileDownloader.IsValidAsync(path, Hash(data), data.Length), "A restored file was rejected.");
            VerifiedFiles.Flush();
            Require(File.ReadAllText(store).Contains("asset.bin"), "The verified-file index was not saved.");
        });

        await check("A new instance reuses another instance's loader profile without reinstalling", async () =>
        {
            var manager = new InstanceManager(Path.Combine(root, "loader-reuse"));
            var produced = "installer output"u8.ToArray();
            var profile = JsonSerializer.Serialize(new
            {
                id = "1.21.1-forge-52.0.1", inheritsFrom = "1.21.1", mainClass = "cpw.mods.bootstraplauncher.BootstrapLauncher",
                libraries = new object[]
                {
                    new { name = "net.minecraftforge:forge:1.21.1-52.0.1:client", downloads = new { artifact = new { path = "net/minecraftforge/forge/client.jar", url = "", sha1 = Hash(produced), size = produced.Length } } }
                }
            });
            var first = manager.CreateInstance("Forge one", "1.21.1", LoaderType.Forge, "52.0.1");
            var versionId = first.GetEffectiveVersionId();
            var firstVersionDir = Path.Combine(manager.GetGameDir(first.Id), "versions", versionId);
            Directory.CreateDirectory(firstVersionDir);
            File.WriteAllText(Path.Combine(firstVersionDir, versionId + ".json"), profile);
            File.WriteAllText(Path.Combine(firstVersionDir, ".complete"), "1");

            var second = manager.CreateInstance("Forge two", "1.21.1", LoaderType.Forge, "52.0.1");
            using var handler = new FakeHttp(_ => throw new Exception("Unexpected network request"));
            using var http = new HttpClient(handler);
            var downloader = new AssetDownloader(manager.SharedDir, manager.GetGameDir(second.Id), http);
            Require(!await LoaderProfileCache.TryReuseAsync(second, manager, downloader, CancellationToken.None),
                "A profile was reused although the installer output is missing.");

            var producedPath = Path.Combine(manager.SharedLibrariesDir, "net", "minecraftforge", "forge", "client.jar");
            Directory.CreateDirectory(Path.GetDirectoryName(producedPath)!);
            File.WriteAllBytes(producedPath, produced);
            Require(await LoaderProfileCache.TryReuseAsync(second, manager, downloader, CancellationToken.None), "The installed loader was not reused.");
            var secondVersionDir = Path.Combine(manager.GetGameDir(second.Id), "versions", versionId);
            Require(File.ReadAllText(Path.Combine(secondVersionDir, versionId + ".json")) == profile && File.Exists(Path.Combine(secondVersionDir, ".complete")),
                "The reused profile was not written to the new instance.");
            Require(File.Exists(Path.Combine(manager.SharedDir, "loader-profiles", versionId + ".json")), "The profile was not kept in the shared cache.");
            Require(handler.Calls == 0, "Reusing a loader touched the network.");

            var other = manager.CreateInstance("Forge other", "1.21.1", LoaderType.Forge, "52.0.2");
            Require(!await LoaderProfileCache.TryReuseAsync(other, manager, downloader, CancellationToken.None), "A different loader version was reused.");
        });

        await check("The cached version list is available without network", async () =>
        {
            var shared = Path.Combine(root, "cached-manifest");
            Require(new VersionManager(shared).TryGetCachedManifest() == null, "An empty cache returned a manifest.");
            Directory.CreateDirectory(shared);
            File.WriteAllText(Path.Combine(shared, "version_manifest.json"),
                """{"latest":{"release":"1.21.1","snapshot":"1.21.1"},"versions":[{"id":"1.21.1","type":"release","url":"https://example.test/1.21.1.json"}]}""");
            Require(new VersionManager(shared).TryGetCachedManifest()?.Versions.Single().Id == "1.21.1", "The cached manifest was not read.");
        });
    }

    private static string Hash(byte[] data) => Convert.ToHexString(SHA1.HashData(data));
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
}
