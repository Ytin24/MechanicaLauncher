using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using MechanicaLauncher.Core.Instances;
using MechanicaLauncher.Core.IO;
using MechanicaLauncher.Core.Models;
using MechanicaLauncher.Core.Mods;

internal static class ModUpdateSmokeTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true, PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
    private const long MaximumDownloadBytes = 64L * 1024 * 1024;

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length != 2)
        {
            Console.Error.WriteLine("--mod-updates-smoke <new isolated directory> <matrix.json>");
            return 2;
        }
        string root = Path.GetFullPath(args[0]);
        if (File.Exists(root) || Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
            throw new InvalidOperationException("Mod update smoke requires a new, empty directory.");
        var matrix = JsonSerializer.Deserialize<SmokeMatrix>(await File.ReadAllTextAsync(args[1]), JsonOptions)
            ?? throw new InvalidDataException("The mod update matrix is empty.");
        Require(matrix.SchemaVersion == 1 && matrix.Targets.Length is > 0 and <= 16, "Expected matrix schema 1 and 1–16 targets.");
        Require(matrix.Targets.Select(target => target.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() == matrix.Targets.Length,
            "Matrix target IDs must be unique.");
        foreach (var target in matrix.Targets)
        {
            Require(Regex.IsMatch(target.Id, "^[a-z0-9][a-z0-9._-]{0,63}$") && target.Id is not "." and not "..", "Invalid target ID.");
            Require(Enum.IsDefined(target.Loader) && target.Loader != LoaderType.None && !string.IsNullOrWhiteSpace(target.Minecraft) &&
                !string.IsNullOrWhiteSpace(target.LoaderVersion), "Each target must specify Minecraft and an exact mod loader version.");
            Require(target.InitialDependencyVersionIds.Length <= 8, "Too many initial fixture dependencies.");
        }
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, ".mechanica-mod-update-smoke"), "Isolated live Modrinth update fixtures. No game is launched.");
        await File.WriteAllTextAsync(Path.Combine(root, "matrix.json"), JsonSerializer.Serialize(matrix, JsonOptions));
        var report = new SmokeReport { StartedAt = DateTimeOffset.UtcNow, Targets = matrix.Targets.Select(target => new TargetReport { Target = target }).ToList() };
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += cancel;
        using var handler = new LiveHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.modrinth.com"), Timeout = TimeSpan.FromSeconds(60) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("MechanicaLauncher-ModUpdateSmoke/1.0 (+https://github.com/Ytin24/MechanicaLauncher)");
        var client = new ModrinthClient(http);
        var service = new ModUpdateService(client, http);
        var sources = new Dictionary<string, PreparedSource>(StringComparer.Ordinal);
        long reservedBytes = 0;
        Task SaveReport() => File.WriteAllTextAsync(Path.Combine(root, "result.json"), JsonSerializer.Serialize(report, JsonOptions));
        void Reserve(long bytes)
        {
            Require(bytes >= 0 && bytes <= 20L * 1024 * 1024 && reservedBytes + bytes <= MaximumDownloadBytes,
                "Live mod smoke download budget exceeded; no further file request was started.");
            reservedBytes += bytes;
        }
        async Task<PreparedSource> PrepareSource(string versionId, TargetInput target)
        {
            Require(Regex.IsMatch(versionId, "^[A-Za-z0-9]{1,64}$"), "Invalid Modrinth version ID.");
            if (sources.TryGetValue(versionId, out var cached)) { ValidateVersion(cached.Version, target); return cached; }
            var version = await client.GetVersionAsync(versionId, cancellation.Token) ?? throw new InvalidDataException("Modrinth version was not found: " + versionId);
            Require(version.Id == versionId, "Modrinth returned another source version.");
            ValidateVersion(version, target);
            var file = ModInstaller.SelectFile(version, ".jar");
            ValidateFile(file);
            Reserve(file.Size);
            string directory = Path.Combine(root, "sources");
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(Path.Combine(directory, versionId + ".json"), JsonSerializer.Serialize(version, JsonOptions));
            string path = Path.Combine(directory, versionId + ".jar");
            await FileDownloader.EnsureAsync(http, file.Url, path, file.Hashes["sha1"], file.Size, cancellation.Token, file.Hashes["sha512"]);
            Require(await Matches(path, file), "Downloaded source does not match the catalog size and both hashes.");
            var prepared = new PreparedSource(version, file, path);
            sources.Add(versionId, prepared);
            return prepared;
        }
        try
        {
            await SaveReport();
            foreach (var targetReport in report.Targets)
            {
                var target = targetReport.Target;
                if (cancellation.IsCancellationRequested) break;
                if (!string.IsNullOrWhiteSpace(target.UnavailableReason))
                {
                    targetReport.Status = "unavailable"; targetReport.Error = target.UnavailableReason;
                    Console.WriteLine("UNAVAILABLE " + target.Id + ": " + target.UnavailableReason);
                    await SaveReport(); continue;
                }
                try
                {
                    var source = await PrepareSource(target.SourceVersionId, target);
                    Require(source.Version.ProjectId == target.ProjectId, "The source version belongs to another project.");
                    var dependencies = new List<PreparedSource>();
                    foreach (string dependency in target.InitialDependencyVersionIds.Distinct(StringComparer.Ordinal))
                        dependencies.Add(await PrepareSource(dependency, target));
                    Require(dependencies.All(item => item.Version.ProjectId != target.ProjectId), "An initial dependency duplicates the mod under test.");
                    targetReport.SourceVersion = source.Version;
                    foreach (bool enabled in new[] { true, false })
                    {
                        cancellation.Token.ThrowIfCancellationRequested();
                        var result = new CaseReport { Name = enabled ? "enabled" : "disabled", Enabled = enabled };
                        targetReport.Cases.Add(result);
                        string game = Path.Combine(root, target.Id, result.Name);
                        Directory.CreateDirectory(Path.Combine(game, "mods"));
                        Directory.CreateDirectory(Path.Combine(game, "config"));
                        string installed = Path.Combine(game, "mods", "renamed-smoke.jar" + (enabled ? "" : ".disabled"));
                        string personal = Path.Combine(game, "config", "personal-sentinel.txt");
                        string personalModNote = Path.Combine(game, "mods", "personal-note.txt");
                        string sentinel = "Personal fixture for " + target.Id + ": " + Guid.NewGuid().ToString("N");
                        File.Copy(source.Path, installed);
                        await File.WriteAllTextAsync(personal, sentinel);
                        await File.WriteAllTextAsync(personalModNote, sentinel);
                        foreach (var dependency in dependencies)
                        {
                            string name = dependency.File.Filename;
                            Require(name == Path.GetFileName(name) && name.IndexOfAny(['/', '\\', ':']) < 0, "Invalid dependency filename.");
                            File.Copy(dependency.Path, Path.Combine(game, "mods", name));
                        }
                        var instance = new GameInstance
                        {
                            Id = target.Id + "-" + result.Name, Name = "Live update " + target.Id, McVersion = target.Minecraft,
                            Loader = target.Loader, LoaderVersion = target.LoaderVersion
                        };
                        async Task Phase(string phase)
                        {
                            result.Phase = phase;
                            Console.WriteLine("PHASE " + target.Id + "/" + result.Name + " " + phase);
                            await SaveReport();
                        }
                        try
                        {
                            await Phase("baseline compatibility");
                            result.BeforeSha512 = await Hash(installed, HashAlgorithmName.SHA512);
                            result.BeforeCompatibility = await new ModCompatibilityChecker(client).CheckAsync(instance, game, false, cancellation.Token);
                            Require(result.BeforeCompatibility.Issues.All(issue => !issue.IsError && issue.Code != "dependency"),
                                "The real source fixture is incompatible: " + string.Join("; ", result.BeforeCompatibility.Issues.Select(issue => issue.Detail)));

                            await Phase("check available");
                            var scan = await service.CheckAsync(instance, game, cancellation.Token);
                            result.Scan = scan.Items; result.ScanErrors = scan.Errors;
                            Require(scan.Errors.Count == 0, "Modrinth check failed: " + string.Join("; ", scan.Errors));
                            var update = scan.Items.Single(item => item.FilePath == installed);
                            Require(update.State == ModUpdateState.Available && update.CurrentVersion == source.Version.VersionNumber && update.Enabled == enabled,
                                "Expected a newer stable version recognized by hash for the renamed fixture: " + update.State + " " + update.Reason);

                            await Phase("plan");
                            var plan = await service.PlanAsync(scan, [installed], cancellation.Token);
                            result.Changes = plan.Changes; result.Conflicts = plan.Conflicts; result.DownloadBytes = plan.DownloadBytes;
                            result.Artifacts = plan.Files.Select(file => new ArtifactReport
                            {
                                ProjectId = file.Version.ProjectId!, VersionId = file.Version.Id, VersionNumber = file.Version.VersionNumber,
                                FileName = file.FileName, Sha1 = file.File.Hashes["sha1"], Sha512 = file.File.Hashes["sha512"],
                                Size = file.File.Size, Dependency = file.Dependency, Enabled = file.Enabled
                            }).ToArray();
                            await SaveReport();
                            Require(plan.Conflicts.Count == 0, "Plan conflicts: " + string.Join("; ", plan.Conflicts.Select(item => item.FileName + ": " + item.Reason)));
                            Require(plan.HasChanges && plan.Changes.Count <= 12, "The live update plan is empty or exceeds the fixture limit.");
                            var rootChange = plan.Files.Single(change => change.Before?.FilePath == installed);
                            Require(rootChange.Version.ProjectId == source.Version.ProjectId && rootChange.Version.Id != source.Version.Id &&
                                rootChange.Version.DatePublished > source.Version.DatePublished && rootChange.Enabled == enabled,
                                "Plan must update the same project to a strictly newer release without toggling it.");
                            foreach (var change in plan.Files) { ValidateVersion(change.Version, target); ValidateFile(change.File); }
                            Reserve(plan.DownloadBytes);

                            await Phase("apply");
                            var applied = await service.ApplyAsync(plan, () => false, cancellation.Token);
                            result.UpdatedCount = applied.UpdatedCount; result.BackupDirectory = applied.BackupDirectory;
                            Require(applied.UpdatedCount == plan.Changes.Count, "Apply count does not match the approved plan.");
                            Require(Path.GetFullPath(applied.BackupDirectory).StartsWith(Path.GetFullPath(game) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
                                "Backup directory escaped the isolated game directory.");
                            string backup = Path.Combine(applied.BackupDirectory, Path.GetFileName(installed));
                            result.AfterSha512 = await Hash(installed, HashAlgorithmName.SHA512);
                            result.BackupSha512 = await Hash(backup, HashAlgorithmName.SHA512);
                            result.BackupVerified = result.BackupSha512 == result.BeforeSha512 && await Matches(backup, source.File);
                            Require(result.BackupVerified && result.AfterSha512 != result.BeforeSha512, "Original bytes must be backed up before replacement.");
                            foreach (var change in plan.Files)
                                Require(await Matches(Path.Combine(game, "mods", change.FileName), change.File), "Installed update does not match both catalog hashes: " + change.FileName);
                            result.RenamedFilePreserved = File.Exists(installed) && rootChange.FileName == Path.GetFileName(installed);
                            result.EnabledStatePreserved = !File.Exists(enabled ? installed + ".disabled" : installed[..^9]);
                            Require(result.RenamedFilePreserved && result.EnabledStatePreserved, "Apply changed the custom filename or enabled state.");
                            result.PersonalFilesPreserved = await File.ReadAllTextAsync(personal) == sentinel && await File.ReadAllTextAsync(personalModNote) == sentinel;
                            Require(result.PersonalFilesPreserved, "Apply changed unrelated personal files.");

                            await Phase("repeat check");
                            int downloads = handler.FileRequests;
                            DateTime installedStamp = File.GetLastWriteTimeUtc(installed);
                            var again = await service.CheckAsync(instance, game, cancellation.Token);
                            result.RepeatScan = again.Items;
                            Require(again.Errors.Count == 0, "Repeat Modrinth check failed: " + string.Join("; ", again.Errors));
                            var repeated = again.Items.Single(item => item.FilePath == installed);
                            result.RepeatCurrent = repeated.State == ModUpdateState.Current && repeated.CurrentVersion == rootChange.Version.VersionNumber && repeated.Enabled == enabled;
                            result.RepeatDidNotDownloadOrWrite = handler.FileRequests == downloads && File.GetLastWriteTimeUtc(installed) == installedStamp &&
                                await Hash(installed, HashAlgorithmName.SHA512) == result.AfterSha512;
                            Require(result.RepeatCurrent && result.RepeatDidNotDownloadOrWrite, "An already updated mod must be Current without a second file download or rewrite.");
                            result.Status = "passed"; result.Phase = "complete";
                            Console.WriteLine("PASS " + target.Id + "/" + result.Name + " " + source.Version.VersionNumber + " -> " + rootChange.Version.VersionNumber);
                        }
                        catch (Exception ex)
                        {
                            result.Status = "failed"; result.Error = ex.ToString();
                            Console.Error.WriteLine("FAIL " + target.Id + "/" + result.Name + ": " + ex.Message);
                        }
                        await File.WriteAllTextAsync(Path.Combine(game, "result.json"), JsonSerializer.Serialize(result, JsonOptions));
                        await SaveReport();
                    }
                    targetReport.Status = targetReport.Cases.Count == 2 && targetReport.Cases.All(result => result.Status == "passed") ? "passed" : "failed";
                }
                catch (Exception ex)
                {
                    targetReport.Status = "failed"; targetReport.Error = ex.ToString();
                    Console.Error.WriteLine("FAIL " + target.Id + ": " + ex.Message);
                }
                await SaveReport();
            }
            report.Status = report.Targets.Any(target => target.Status == "failed") ? "failed" :
                report.Targets.All(target => target.Status == "passed") ? "passed" : "incomplete";
            Console.WriteLine($"{report.Status.ToUpperInvariant()} mod update smoke: {report.Targets.Count(target => target.Status == "passed")}/{report.Targets.Count} targets; report {Path.Combine(root, "result.json")}");
            return report.Status == "passed" ? 0 : report.Status == "failed" ? 1 : 2;
        }
        finally
        {
            Console.CancelKeyPress -= cancel;
            report.FinishedAt = DateTimeOffset.UtcNow; report.ApiRequests = handler.ApiRequests; report.FileRequests = handler.FileRequests;
            report.ApprovedDownloadBytes = reservedBytes;
            await SaveReport();
        }
    }

    private static void ValidateVersion(ModrinthVersion version, TargetInput target) => Require(version.VersionType == "release" &&
        version.DatePublished != null && !string.IsNullOrWhiteSpace(version.ProjectId) && version.GameVersions.Contains(target.Minecraft) &&
        version.Loaders.Contains(target.Loader.ToString().ToLowerInvariant()), "Fixture version is not a stable release for " + target.Id + ": " + version.Id);

    private static void ValidateFile(ModrinthFile file) => Require(file.Size is > 0 and <= 20L * 1024 * 1024 &&
        Uri.TryCreate(file.Url, UriKind.Absolute, out var url) && url.Scheme == "https" && url.Host == "cdn.modrinth.com" && string.IsNullOrEmpty(url.UserInfo) &&
        file.Hashes.TryGetValue("sha1", out var sha1) && sha1.Length == 40 && sha1.All(Uri.IsHexDigit) &&
        file.Hashes.TryGetValue("sha512", out var sha512) && sha512.Length == 128 && sha512.All(Uri.IsHexDigit), "Fixture has an unexpected source, size or hashes.");

    private static async Task<string> Hash(string path, HashAlgorithmName algorithm)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(algorithm == HashAlgorithmName.SHA1 ? await SHA1.HashDataAsync(stream) : await SHA512.HashDataAsync(stream)).ToLowerInvariant();
    }
    private static async Task<bool> Matches(string path, ModrinthFile file) => File.Exists(path) && new FileInfo(path).Length == file.Size &&
        (await Hash(path, HashAlgorithmName.SHA1)).Equals(file.Hashes["sha1"], StringComparison.OrdinalIgnoreCase) &&
        (await Hash(path, HashAlgorithmName.SHA512)).Equals(file.Hashes["sha512"], StringComparison.OrdinalIgnoreCase);
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed record PreparedSource(ModrinthVersion Version, ModrinthFile File, string Path);
    private sealed class SmokeMatrix
    {
        public int SchemaVersion { get; set; } = 1;
        public TargetInput[] Targets { get; set; } = [];
    }
    private sealed class TargetInput
    {
        public string Id { get; set; } = "";
        public string Minecraft { get; set; } = "";
        public LoaderType Loader { get; set; }
        public string LoaderVersion { get; set; } = "";
        public string ProjectId { get; set; } = "";
        public string SourceVersionId { get; set; } = "";
        public string? ObservedTargetVersionId { get; set; }
        public string[] InitialDependencyVersionIds { get; set; } = [];
        public string? UnavailableReason { get; set; }
    }
    private sealed class SmokeReport
    {
        public string Status { get; set; } = "running";
        public DateTimeOffset StartedAt { get; set; }
        public DateTimeOffset FinishedAt { get; set; }
        public string Scope { get; } = "Real Modrinth stable updates and file transactions; no Minecraft process or server is started.";
        public List<TargetReport> Targets { get; set; } = [];
        public int ApiRequests { get; set; }
        public int FileRequests { get; set; }
        public long ApprovedDownloadBytes { get; set; }
    }
    private sealed class TargetReport
    {
        public TargetInput Target { get; set; } = new();
        public string Status { get; set; } = "not_run";
        public ModrinthVersion? SourceVersion { get; set; }
        public List<CaseReport> Cases { get; } = [];
        public string? Error { get; set; }
    }
    private sealed class CaseReport
    {
        public string Name { get; set; } = "";
        public bool Enabled { get; set; }
        public string Status { get; set; } = "running";
        public string Phase { get; set; } = "prepare";
        public CompatibilityReport? BeforeCompatibility { get; set; }
        public IReadOnlyList<ModUpdateItem> Scan { get; set; } = [];
        public IReadOnlyList<string> ScanErrors { get; set; } = [];
        public IReadOnlyList<ModUpdateChange> Changes { get; set; } = [];
        public IReadOnlyList<ModUpdateConflict> Conflicts { get; set; } = [];
        public ArtifactReport[] Artifacts { get; set; } = [];
        public long DownloadBytes { get; set; }
        public int UpdatedCount { get; set; }
        public string? BeforeSha512 { get; set; }
        public string? AfterSha512 { get; set; }
        public string? BackupSha512 { get; set; }
        public string? BackupDirectory { get; set; }
        public bool BackupVerified { get; set; }
        public bool RenamedFilePreserved { get; set; }
        public bool EnabledStatePreserved { get; set; }
        public bool PersonalFilesPreserved { get; set; }
        public bool RepeatCurrent { get; set; }
        public bool RepeatDidNotDownloadOrWrite { get; set; }
        public IReadOnlyList<ModUpdateItem> RepeatScan { get; set; } = [];
        public string? Error { get; set; }
    }
    private sealed class ArtifactReport
    {
        public string ProjectId { get; set; } = "";
        public string VersionId { get; set; } = "";
        public string VersionNumber { get; set; } = "";
        public string FileName { get; set; } = "";
        public string Sha1 { get; set; } = "";
        public string Sha512 { get; set; } = "";
        public long Size { get; set; }
        public bool Dependency { get; set; }
        public bool Enabled { get; set; }
    }
    private sealed class LiveHandler() : DelegatingHandler(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
    {
        private int requests;
        public int ApiRequests;
        public int FileRequests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri ?? throw new InvalidOperationException("Missing live smoke URI.");
            Require(url.Scheme == "https" && url.Host is "api.modrinth.com" or "cdn.modrinth.com", "Unexpected live smoke origin.");
            Require(Interlocked.Increment(ref requests) <= 256, "Live smoke HTTP request limit exceeded.");
            if (url.Host == "cdn.modrinth.com") Interlocked.Increment(ref FileRequests);
            else Interlocked.Increment(ref ApiRequests);
            return base.SendAsync(request, cancellationToken);
        }
    }
}
