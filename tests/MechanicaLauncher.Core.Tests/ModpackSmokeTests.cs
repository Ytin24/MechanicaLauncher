using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using MechanicaLauncher.Core.Game;
using MechanicaLauncher.Core.Instances;
using MechanicaLauncher.Core.Models;
using MechanicaLauncher.Core.Mods;

internal static class ModpackSmokeTests
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length is < 3 or > 5)
        {
            Console.Error.WriteLine("--modpack-smoke <empty isolated directory> <local original.mrpack> <local Modrinth version.json> [shared test-cache directory] [previous imported game directory]");
            return 2;
        }
        string root = Path.GetFullPath(args[0]);
        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
            throw new InvalidOperationException("Modpack smoke tests require a new, empty directory.");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, ".mechanica-modpack-smoke"), "Isolated integration fixtures. Do not use as launcher data.");
        var report = new Report
        {
            StartedAt = DateTimeOffset.UtcNow,
            SourcePack = Path.GetFullPath(args[1]),
            SourceMetadata = Path.GetFullPath(args[2]),
            Limitations = "Checks installed bytes, textures, a game window, 15 seconds alive and clean close. Does not test worlds or servers. Runtime checks set fullscreen=false, renderDistance=4, maxFps=30 and initialTutorialCompleted=true."
        };
        string reportPath = Path.Combine(root, "result.json");
        DateTime lastProgress = DateTime.MinValue;
        void Progress(string status, double percent = -1)
        {
            if (DateTime.UtcNow - lastProgress < TimeSpan.FromSeconds(5)) return;
            lastProgress = DateTime.UtcNow;
            Console.WriteLine($"{DateTimeOffset.Now:HH:mm:ss} {status}");
        }
        async Task Phase(string phase)
        {
            report.Phase = phase;
            Console.WriteLine($"PHASE {phase}");
            await SaveReport();
        }
        Task SaveReport() => File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, JsonOptions));

        try
        {
            await Phase("validate fixtures");
            var version = JsonSerializer.Deserialize<ModrinthVersion>(await File.ReadAllTextAsync(report.SourceMetadata))
                ?? throw new InvalidDataException("Modrinth version metadata is empty.");
            var packFile = ModInstaller.SelectFile(version, ".mrpack");
            var sourceHash = await FingerprintFile(report.SourcePack);
            Require(sourceHash.Length == packFile.Size &&
                sourceHash.Sha1.Equals(packFile.Hashes.GetValueOrDefault("sha1"), StringComparison.OrdinalIgnoreCase) &&
                sourceHash.Sha512.Equals(packFile.Hashes.GetValueOrDefault("sha512"), StringComparison.OrdinalIgnoreCase),
                "The local mrpack does not match both hashes and size in the Modrinth version metadata.");
            var manifest = await ReadManifest(report.SourcePack);
            report.ProjectId = version.ProjectId;
            report.VersionId = version.Id;
            report.VersionNumber = version.VersionNumber;
            report.PackSha512 = sourceHash.Sha512;
            report.Dependencies = manifest.Dependencies;
            report.Minecraft = manifest.Minecraft;
            report.Loader = manifest.Loader.ToString();
            report.LoaderVersion = manifest.LoaderVersion;
            report.ManifestFiles = manifest.TotalFiles;
            report.ClientFiles = manifest.Downloads.Count;
            report.OverrideFiles = manifest.Overrides.Count;
            Require(version.GameVersions.Contains(manifest.Minecraft), "Catalog Minecraft versions do not include the pack dependency.");
            if (manifest.Loader != LoaderType.None)
                Require(version.Loaders.Contains(manifest.Loader.ToString().ToLowerInvariant()), "Catalog loaders do not include the pack dependency.");

            var manager = new InstanceManager(root);
            report.SharedCache = args.Length >= 4 ? Path.GetFullPath(args[3]) : manager.SharedDir;
            report.ContentCache = args.Length == 5 ? Path.GetFullPath(args[4]) : null;
            using var catalogHandler = new PackHttpHandler(report.SourcePack, packFile.Url, manifest.Downloads, report.ContentCache);
            using var catalogHttp = new HttpClient(catalogHandler) { Timeout = TimeSpan.FromMinutes(5) };
            catalogHttp.DefaultRequestHeaders.UserAgent.ParseAdd("MechanicaLauncher-ModpackSmoke");
            var catalog = new ModInstaller(catalogHttp);
            catalog.StatusChanged += status => Progress(status);

            await Phase("catalog import");
            GameInstance original;
            try { original = await catalog.ImportModpackAsync(version, manager); }
            finally
            {
                report.VerifiedDownloads = catalogHandler.VerifiedPaths.Count;
                report.NetworkRequests = catalogHandler.NetworkRequests;
                report.CachedDownloads = catalogHandler.CachedDownloads;
            }
            report.SourceInstanceId = original.Id;
            Require(catalogHandler.PackRequests > 0, "Catalog import did not request the selected mrpack.");
            Require(manifest.Downloads.All(file => catalogHandler.VerifiedPaths.Contains(file.Path)),
                "Some applicable manifest downloads were not independently verified.");
            RequireInstance(original, manifest);

            await Phase("verify imported files");
            var originalSnapshot = await Snapshot(manager.GetGameDir(original.Id));
            var expected = new SortedDictionary<string, Fingerprint>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in manifest.Downloads) expected.Add(file.Path, file.Hash);
            foreach (var file in manifest.Overrides) expected[file.Key] = file.Value;
            report.InstalledFiles = originalSnapshot.Count;
            await File.WriteAllTextAsync(Path.Combine(root, "source-snapshot.json"), JsonSerializer.Serialize(originalSnapshot, JsonOptions));
            RequireSnapshot(expected, originalSnapshot, "Original import");

            await Phase("export");
            string exported = Path.Combine(root, "roundtrip.mrpack");
            await ModpackInstaller.ExportAsync(original, manager, exported);
            var exportedManifest = await ReadManifest(exported);
            Require(manifest.Dependencies.Count == exportedManifest.Dependencies.Count &&
                manifest.Dependencies.All(d => exportedManifest.Dependencies.GetValueOrDefault(d.Key) == d.Value),
                "Export changed the exact Minecraft or loader dependencies.");

            await Phase("offline re-import");
            using var offlineHandler = new OfflineHandler();
            using var offlineHttp = new HttpClient(offlineHandler);
            GameInstance roundtrip;
            try { roundtrip = await new ModpackInstaller(offlineHttp).ImportAsync(exported, manager); }
            finally { report.OfflineNetworkRequests = offlineHandler.Requests; }
            report.RoundtripInstanceId = roundtrip.Id;
            Require(offlineHandler.Requests == 0, "Roundtrip import attempted a network request.");
            RequireInstance(roundtrip, manifest);
            Require(manager.GetAllInstances().Count == 2, "Import did not leave exactly two independent instances.");

            await Phase("verify roundtrip files");
            var roundtripSnapshot = await Snapshot(manager.GetGameDir(roundtrip.Id));
            report.RoundtripFiles = roundtripSnapshot.Count;
            await File.WriteAllTextAsync(Path.Combine(root, "roundtrip-snapshot.json"), JsonSerializer.Serialize(roundtripSnapshot, JsonOptions));
            RequireSnapshot(originalSnapshot, roundtripSnapshot, "Export/import roundtrip");

            var cases = new[] { (Name: "original", Instance: original), (Name: "roundtrip", Instance: roundtrip) };
            await Phase("compatibility");
            foreach (var test in cases)
                report.Compatibility[test.Name] = await new ModCompatibilityChecker().CheckAsync(test.Instance, manager.GetGameDir(test.Instance.Id), false);
            Require(report.Compatibility.Values.All(check => check.Issues.All(issue => !issue.IsError)),
                "Pre-launch compatibility check has blocking issues; see Compatibility in result.json. No game was launched.");

            await Phase("runtime metadata");
            var versions = new VersionManager(report.SharedCache);
            var vanilla = await versions.GetVersionMetaAsync(manifest.Minecraft);
            int major = vanilla.JavaVersion?.MajorVersion ?? 8;
            string component = vanilla.JavaVersion?.Component ?? "jre-legacy";
            string runtime = Path.Combine(report.SharedCache, "runtime", component, "windows-x64", component);
            string localJava = Path.Combine(runtime, "bin", "javaw.exe");
            string? java = File.Exists(Path.Combine(runtime, ".complete")) && File.Exists(localJava)
                ? localJava : JavaFinder.FindJava(component, major);
            java ??= await JavaFinder.DownloadJavaAsync(component, report.SharedCache, status => Progress(status));
            if (java == null) throw new InvalidOperationException($"Java {major} runtime is unavailable.");
            JavaFinder.ValidateJava(java, major);
            report.Java = java;
            report.JavaMajor = major;

            for (int i = 0; i < cases.Length; i++)
            {
                var test = cases[i];
                string gameDir = manager.GetGameDir(test.Instance.Id);
                await ConfigureWindow(gameDir);
                string cachedClient = Path.Combine(report.SharedCache, "versions", vanilla.Id, vanilla.Id + ".jar");
                string client = Path.Combine(gameDir, "versions", vanilla.Id, vanilla.Id + ".jar");
                if (File.Exists(cachedClient) && !File.Exists(client))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(client)!);
                    File.Copy(cachedClient, client);
                }
                var downloader = new AssetDownloader(report.SharedCache, gameDir);
                downloader.ProgressChanged += (status, percent) => Progress(status, percent);
                await Phase(test.Name + " vanilla installation");
                await downloader.DownloadVersionAsync(vanilla);
                await Phase(test.Name + " loader installation");
                await InstallExactLoader(test.Instance, report.SharedCache, gameDir, java, (status, percent) => Progress(status, percent));
                var meta = test.Instance.Loader == LoaderType.None ? vanilla
                    : await versions.GetMergedMetaAsync(test.Instance.GetEffectiveVersionId(), gameDir);
                if (test.Instance.Loader != LoaderType.None)
                    await downloader.DownloadVersionAsync(new VersionMeta { Id = vanilla.Id, Libraries = meta.Libraries });
                await Phase(test.Name + " launch");
                await SmokeTests.LaunchAsync(meta, java, gameDir, report.SharedCache, vanilla.Id, i + 1);
                report.CompletedLaunches++;
                report.Launches.Add(new(test.Name, test.Instance.Id, Path.Combine(gameDir, "logs", $"smoke-{i + 1}.log")));
            }

            report.Phase = "complete";
            report.Passed = true;
            Console.WriteLine($"PASS {version.VersionNumber}: {report.ClientFiles} verified downloads, {report.InstalledFiles} roundtrip files, two launches and clean exits");
            Console.WriteLine(report.Limitations);
            return 0;
        }
        catch (Exception ex)
        {
            report.Error = ex.ToString();
            Console.Error.WriteLine($"FAIL modpack smoke ({report.Phase}): {ex.Message}");
            return 1;
        }
        finally
        {
            report.FinishedAt = DateTimeOffset.UtcNow;
            await SaveReport();
            Console.WriteLine("REPORT " + reportPath);
        }
    }

    private static async Task<PackManifest> ReadManifest(string pack)
    {
        using var zip = ZipFile.OpenRead(pack);
        var index = zip.GetEntry("modrinth.index.json") ?? throw new InvalidDataException("The fixture has no modrinth.index.json.");
        using var input = index.Open();
        using var json = await JsonDocument.ParseAsync(input);
        var node = json.RootElement;
        Require(node.GetProperty("formatVersion").GetInt32() == 1 && node.GetProperty("game").GetString() == "minecraft", "Unsupported fixture format.");
        var dependencies = node.GetProperty("dependencies").EnumerateObject().ToDictionary(d => d.Name, d => d.Value.GetString()!);
        string minecraft = dependencies["minecraft"];
        var loaders = dependencies.Where(d => d.Key != "minecraft").ToArray();
        Require(!string.IsNullOrWhiteSpace(minecraft) && loaders.Length <= 1, "Invalid fixture dependencies.");
        var loader = loaders.Length == 0 ? LoaderType.None : loaders[0].Key switch
        {
            "fabric-loader" => LoaderType.Fabric,
            "quilt-loader" => LoaderType.Quilt,
            "forge" => LoaderType.Forge,
            "neoforge" => LoaderType.NeoForge,
            _ => throw new InvalidDataException("Unknown fixture loader.")
        };
        string? loaderVersion = loaders.Length == 0 ? null : loaders[0].Value;
        Require(loader == LoaderType.None || !string.IsNullOrWhiteSpace(loaderVersion), "The fixture has no exact loader version.");
        var downloads = new List<PackDownload>();
        var files = node.GetProperty("files");
        foreach (var file in files.EnumerateArray())
        {
            if (file.TryGetProperty("env", out var env) && env.TryGetProperty("client", out var client) && client.GetString() == "unsupported") continue;
            string path = NormalizePath(file.GetProperty("path").GetString()!);
            var hashes = file.GetProperty("hashes");
            downloads.Add(new(path, new(file.GetProperty("fileSize").GetInt64(),
                    hashes.GetProperty("sha1").GetString()!.ToUpperInvariant(), hashes.GetProperty("sha512").GetString()!.ToUpperInvariant()),
                file.GetProperty("downloads").EnumerateArray().Select(url => url.GetString()!).ToArray()));
        }
        var overrides = new SortedDictionary<string, Fingerprint>(StringComparer.OrdinalIgnoreCase);
        foreach (string prefix in new[] { "overrides/", "client-overrides/" })
            foreach (var entry in zip.Entries.Where(entry => entry.FullName.StartsWith(prefix, StringComparison.Ordinal) && entry.Name.Length > 0))
            {
                using var stream = entry.Open();
                overrides[NormalizePath(entry.FullName[prefix.Length..])] = await FingerprintStream(stream);
            }
        return new(minecraft, loader, loaderVersion, dependencies, files.GetArrayLength(), downloads, overrides);
    }

    private static async Task<SortedDictionary<string, Fingerprint>> Snapshot(string gameDir)
    {
        var result = new SortedDictionary<string, Fingerprint>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in Directory.EnumerateFiles(gameDir, "*", SearchOption.AllDirectories))
            result.Add(NormalizePath(Path.GetRelativePath(gameDir, path)), await FingerprintFile(path));
        return result;
    }

    private static void RequireInstance(GameInstance instance, PackManifest manifest) => Require(
        instance.McVersion == manifest.Minecraft && instance.Loader == manifest.Loader && instance.LoaderVersion == manifest.LoaderVersion,
        "Import changed the exact Minecraft or loader dependency.");

    private static void RequireSnapshot(IReadOnlyDictionary<string, Fingerprint> expected, IReadOnlyDictionary<string, Fingerprint> actual, string phase)
    {
        string[] missing = expected.Keys.Where(path => !actual.ContainsKey(path)).ToArray();
        string[] extra = actual.Keys.Where(path => !expected.ContainsKey(path)).ToArray();
        string[] changed = expected.Where(file => actual.TryGetValue(file.Key, out var hash) && hash != file.Value).Select(file => file.Key).ToArray();
        Require(missing.Length == 0 && extra.Length == 0 && changed.Length == 0,
            $"{phase} differs: missing [{string.Join(", ", missing)}]; extra [{string.Join(", ", extra)}]; changed [{string.Join(", ", changed)}].");
    }

    private static string NormalizePath(string path)
    {
        Require(!string.IsNullOrWhiteSpace(path) && !Path.IsPathRooted(path) && !path.Contains(':'), "Invalid fixture relative path.");
        string anchor = Path.Combine(Path.GetTempPath(), "mechanica-modpack-path-check");
        string full = Path.GetFullPath(Path.Combine(anchor, path.Replace('/', Path.DirectorySeparatorChar)));
        Require(full.StartsWith(anchor + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "Fixture path leaves the game directory.");
        return Path.GetRelativePath(anchor, full).Replace('\\', '/');
    }

    private static async Task<Fingerprint> FingerprintFile(string path)
    {
        await using var stream = File.OpenRead(path);
        return await FingerprintStream(stream);
    }

    private static async Task<Fingerprint> FingerprintStream(Stream stream)
    {
        using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        using var sha512 = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
        byte[] buffer = new byte[81920];
        long length = 0;
        int count;
        while ((count = await stream.ReadAsync(buffer)) > 0)
        {
            length += count;
            sha1.AppendData(buffer, 0, count);
            sha512.AppendData(buffer, 0, count);
        }
        return new(length, Convert.ToHexString(sha1.GetHashAndReset()), Convert.ToHexString(sha512.GetHashAndReset()));
    }

    private static async Task ConfigureWindow(string gameDir)
    {
        string path = Path.Combine(gameDir, "options.txt");
        string[] settings = ["fullscreen", "renderDistance", "maxFps", "initialTutorialCompleted"];
        string[] lines = File.Exists(path) ? await File.ReadAllLinesAsync(path) : [];
        await File.WriteAllLinesAsync(path, lines.Where(line => !settings.Any(key => line.StartsWith(key + ":", StringComparison.Ordinal)))
            .Concat(["fullscreen:false", "renderDistance:4", "maxFps:30", "initialTutorialCompleted:true"]));
    }

    private static async Task InstallExactLoader(GameInstance instance, string shared, string gameDir, string java, Action<string, double> progress)
    {
        switch (instance.Loader)
        {
            case LoaderType.None: return;
            case LoaderType.Fabric:
                var fabric = new FabricInstaller(shared, gameDir); fabric.ProgressChanged += progress;
                await fabric.InstallAsync(instance.McVersion, instance.LoaderVersion!); break;
            case LoaderType.Quilt:
                var quilt = new QuiltInstaller(shared, gameDir); quilt.ProgressChanged += progress;
                await quilt.InstallAsync(instance.McVersion, instance.LoaderVersion!); break;
            case LoaderType.Forge:
                var forge = new ForgeInstaller(shared, gameDir); forge.ProgressChanged += progress;
                await forge.InstallAsync(instance.McVersion, instance.LoaderVersion!, java); break;
            case LoaderType.NeoForge:
                var neo = new NeoForgeInstaller(shared, gameDir); neo.ProgressChanged += progress;
                await neo.InstallAsync(instance.McVersion, instance.LoaderVersion!, java); break;
            default: throw new InvalidDataException("Unsupported instance loader.");
        }
    }

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidDataException(message);
    }

    private sealed record Fingerprint(long Length, string Sha1, string Sha512);
    private sealed record PackDownload(string Path, Fingerprint Hash, string[] Urls);
    private sealed record PackManifest(string Minecraft, LoaderType Loader, string? LoaderVersion,
        Dictionary<string, string> Dependencies, int TotalFiles, List<PackDownload> Downloads, SortedDictionary<string, Fingerprint> Overrides);
    private sealed record LaunchResult(string Source, string InstanceId, string Log);

    private sealed class PackHttpHandler(string packPath, string packUrl, IReadOnlyList<PackDownload> downloads, string? cacheDirectory) : DelegatingHandler(new HttpClientHandler())
    {
        public HashSet<string> VerifiedPaths { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int NetworkRequests { get; private set; }
        public int PackRequests { get; private set; }
        public int CachedDownloads { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string url = request.RequestUri?.AbsoluteUri ?? "";
            if (url == new Uri(packUrl).AbsoluteUri)
            {
                PackRequests++;
                return new(HttpStatusCode.OK) { RequestMessage = request, Content = new StreamContent(File.OpenRead(packPath)) };
            }
            var expected = downloads.Where(file => file.Urls.Any(candidate => new Uri(candidate).AbsoluteUri == url)).ToArray();
            if (expected.Length == 0) throw new InvalidOperationException("Import requested a URL outside the fixture manifest: " + url);
            string? cached = cacheDirectory == null ? null : Path.Combine(cacheDirectory, expected[0].Path);
            HttpResponseMessage response;
            if (cached != null && File.Exists(cached) && await FingerprintFile(cached) == expected[0].Hash)
            {
                CachedDownloads++;
                response = new(HttpStatusCode.OK) { RequestMessage = request, Content = new StreamContent(File.OpenRead(cached)) };
            }
            else
            {
                NetworkRequests++;
                response = await base.SendAsync(request, cancellationToken);
            }
            try
            {
                if (response.IsSuccessStatusCode)
                {
                    byte[] bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                    var actual = new Fingerprint(bytes.LongLength, Convert.ToHexString(SHA1.HashData(bytes)), Convert.ToHexString(SHA512.HashData(bytes)));
                    foreach (var file in expected)
                    {
                        Require(actual == file.Hash, "Manifest download hash or size mismatch: " + file.Path);
                        VerifiedPaths.Add(file.Path);
                    }
                }
                return response;
            }
            catch { response.Dispose(); throw; }
        }
    }

    private sealed class OfflineHandler : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            throw new InvalidOperationException("Offline roundtrip attempted a network request: " + request.RequestUri);
        }
    }

    private sealed class Report
    {
        public DateTimeOffset StartedAt { get; set; }
        public DateTimeOffset FinishedAt { get; set; }
        public string Phase { get; set; } = "fixtures";
        public bool Passed { get; set; }
        public string? Error { get; set; }
        public string SourcePack { get; set; } = "";
        public string SourceMetadata { get; set; } = "";
        public bool CatalogArchiveReplayedFromLocalFile { get; } = true;
        public string? ProjectId { get; set; }
        public string VersionId { get; set; } = "";
        public string VersionNumber { get; set; } = "";
        public string PackSha512 { get; set; } = "";
        public string Minecraft { get; set; } = "";
        public string Loader { get; set; } = "";
        public string? LoaderVersion { get; set; }
        public Dictionary<string, string> Dependencies { get; set; } = [];
        public string SharedCache { get; set; } = "";
        public string? ContentCache { get; set; }
        public string? Java { get; set; }
        public int JavaMajor { get; set; }
        public string? SourceInstanceId { get; set; }
        public string? RoundtripInstanceId { get; set; }
        public int ManifestFiles { get; set; }
        public int ClientFiles { get; set; }
        public int VerifiedDownloads { get; set; }
        public int NetworkRequests { get; set; }
        public int CachedDownloads { get; set; }
        public int OverrideFiles { get; set; }
        public int InstalledFiles { get; set; }
        public int RoundtripFiles { get; set; }
        public int OfflineNetworkRequests { get; set; }
        public int CompletedLaunches { get; set; }
        public Dictionary<string, CompatibilityReport> Compatibility { get; set; } = [];
        public List<LaunchResult> Launches { get; set; } = [];
        public string Limitations { get; set; } = "";
    }
}
