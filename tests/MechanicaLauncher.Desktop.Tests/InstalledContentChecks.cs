using System;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MechanicaLauncher.Core.Instances;
using MechanicaLauncher.Core.IO;
using MechanicaLauncher.Core.Localization;
using MechanicaLauncher.Core.Models;
using MechanicaLauncher.Core.Mods;
using MechanicaLauncher.Core.Profiles;
using MechanicaLauncher.Desktop;
using Nitidus;
using Nitidus.Native;

internal static partial class Program
{
    private static void InstalledContentChecks(Pump context)
    {
        string? previousData = Environment.GetEnvironmentVariable("MECHANICA_DATA_DIR");
        string previousLanguage = Locale.CurrentLanguage;
        string data = Path.Combine(output, "installed-content-data");
        Environment.SetEnvironmentVariable("MECHANICA_DATA_DIR", data);
        try
        {
            var instances = new InstanceManager(data);
            var main = instances.CreateInstance("Установленные моды", "1.21.1", LoaderType.Fabric, "0.16.10");
            var empty = instances.CreateInstance("Другая сборка", "1.21.1", LoaderType.Fabric, "0.16.10");
            var delayed = instances.CreateInstance("Предыдущая сборка", "1.21.1", LoaderType.Fabric, "0.16.10");
            var offline = instances.CreateInstance("Проверка подключения", "1.21.1", LoaderType.Fabric, "0.16.10");
            string mainMods = Path.Combine(instances.GetGameDir(main.Id), "mods");
            string renamed = Path.Combine(mainMods, "renamed.jar");
            string knownHash = WriteInstalledJar(renamed, "Внутреннее имя JAR", "dev", "known_fixture");
            string disabledPath = Path.Combine(mainMods, "quiet-steps.jar.disabled");
            string disabledHash = WriteInstalledJar(disabledPath, "Локальное имя шагов", "local", "disabled_fixture");
            WriteInstalledJar(Path.Combine(mainMods, "local-helper.jar"), "Местный помощник", "1.2.3-local", "local_fixture");
            WriteInstalledJar(Path.Combine(mainMods, "opaque-name.jar"), null, "", "opaque_fixture");
            string offlinePath = Path.Combine(instances.GetGameDir(offline.Id), "mods", "offline-helper.jar");
            WriteInstalledJar(offlinePath, "Мод без подключения", "0.8.0", "offline_fixture");

            using var handler = new InstalledContentHandler(knownHash, disabledHash);
            using var http = new HttpClient(handler) { BaseAddress = new("http://installed-content.invalid") };
            using var model = new LauncherModel(new LauncherSettings
            {
                Username = "InstalledContent", Language = "ru", DiscordRpc = false, Animations = false,
                SelectedInstanceId = main.Id
            }, instances, new ModrinthClient(http));
            model.Dispatch = action => context.Post(_ => action(), null);
            using var presentation = new LauncherView(model);
            using var host = new HeadlessHost(presentation.View, 1180, 800);
            var view = presentation.View;
            double time = 0;
            void Render() { context.Drain(); host.Render(time += 1); host.Render(time += 1); }
            void Until(Func<bool> condition, string operation)
            {
                var wait = System.Diagnostics.Stopwatch.StartNew();
                while (!condition())
                {
                    if (wait.Elapsed.TotalSeconds > 10)
                        throw new TimeoutException($"Installed content: {operation}; page={model.Page}, catalogLoading={model.CatalogLoading}, projectLoading={model.ProjectLoading}, install={model.InstallLabel}, canInstall={model.CanInstall}, rows={model.CatalogItems.Count}, message={model.Message}, unexpected={string.Join(", ", handler.Unexpected)}");
                    context.Drain(); Thread.Sleep(1);
                }
                Render();
            }
            void Await(Task task, string operation)
            {
                Until(() => task.IsCompleted, operation);
                task.GetAwaiter().GetResult(); Render();
            }
            void Installed(int count)
            {
                Until(() => !model.CatalogLoading && model.CatalogItems.Count == count, "installed list");
                Check(model.InstalledOnly && !model.CatalogFailed, "installed list remains usable after indexing");
            }
            void Open(string id, string installedLabel = "Установлено", bool canInstall = false)
            {
                model.OpenProject(handler.Project(id), "mod", "1.21.1");
                Until(() => !model.ProjectLoading && model.InstallLabel == installedLabel && model.CanInstall == canInstall, "project " + id);
                Check(!model.ProjectFailed && model.HasProjectVersions, "project metadata and versions load from the fixture: " + id);
            }
            ItemModel FileRow(string fileName) => model.CatalogItems.Single(item => Path.GetFileName(item.Id) == fileName);
            bool Fits(Element node, Rect bounds)
            {
                var box = UiTransform.VisualBounds(node);
                return box.Width > 0 && box.Height > 0 && box.X >= bounds.X - 1 && box.Y >= bounds.Y - 1 &&
                    box.X + box.Width <= bounds.X + bounds.Width + 1 && box.Y + box.Height <= bounds.Y + bounds.Height + 1;
            }

            model.ShowInstalled(); model.Catalog(); Installed(4);
            Check(FileRow("renamed.jar").Title == "Красивые биомы" && FileRow("renamed.jar").Meta.Contains("1.0.0") &&
                FileRow("renamed.jar").Meta.Contains("Modrinth"), "Modrinth title and version replace misleading JAR metadata and the renamed filename");
            Check(FileRow("renamed.jar").Description == "renamed.jar", "installed row retains the actual filename below its readable name");
            Check(FileRow("local-helper.jar").Title == "Местный помощник" && FileRow("local-helper.jar").Meta.Contains("1.2.3-local") &&
                FileRow("local-helper.jar").Meta.Contains("Не найдено на Modrinth"), "unknown local mod keeps its Fabric name and version with an explicit unmatched status");
            Check(FileRow("opaque-name.jar").Title == "opaque-name.jar", "a JAR without metadata falls back to its filename");
            Check(FileRow("quiet-steps.jar.disabled").Title == "Тихие шаги" && FileRow("quiet-steps.jar.disabled").Meta.Contains("Отключено") &&
                FileRow("quiet-steps.jar.disabled").Primary == "Включить", "disabled content keeps its recognized name and a distinct enable action");
            Check(handler.HashRequests > 0 && handler.ProjectsRequests > 0, "installed recognition uses bulk hash and project endpoints");

            int queryRequests = handler.Requests;
            string sentinel = Path.Combine(mainMods, "query-sentinel.jar");
            WriteInstalledJar(sentinel, "Новый файл после сканирования", "1.0", "sentinel_fixture");
            try
            {
                model.Query = "КРАСИВЫЕ"; Installed(1);
                Check(model.CatalogItems[0].Title == "Красивые биомы", "installed search matches a normalized display name without case sensitivity");
                model.Query = "renamed.jar"; Installed(1);
                Check(model.CatalogItems[0].Title == "Красивые биомы", "installed search also matches the original filename");
                model.Query = "Местный"; Installed(1);
                Check(model.CatalogItems[0].Title == "Местный помощник", "installed search includes names read from local metadata");
                model.Query = "query-sentinel"; Installed(0);
                model.Query = ""; Installed(4);
                Check(handler.Requests == queryRequests, "typing installed filters issues no HTTP and does not rescan new files");
            }
            finally { File.Delete(sentinel); }

            var list = view.Find("catalogList");
            foreach (bool light in new[] { false, true })
            {
                model.LightTheme = light;
                foreach (var size in new[] { (1180u, 800u), (840u, 620u) })
                {
                    host.Resize(size.Item1, size.Item2); Render();
                    Check(Fits(view.Find("catalogSearch"), new(0, 0, size.Item1, size.Item2)), "installed search remains inside the window");
                    for (int i = 0; i < model.CatalogItems.Count; i++)
                    {
                        UiVirtualList.ScrollIntoView(list, i, ScrollAlignment.Start); Render();
                        var row = UiVirtualList.GetRealizedItem(list, i)!;
                        var item = model.CatalogItems[i];
                        Check(row != null && row.DescendantsAndSelf().Any(node => node.Get(Ui.Text) == item.Title) &&
                            row.DescendantsAndSelf().Any(node => node.Get(Ui.Text) == item.Description), "native installed row renders its readable title and filename");
                        foreach (var button in row!.DescendantsAndSelf().Where(node => node.Type.Is(UiTypes.Button) && Visible(node)))
                            Check(Fits(button, list.Bounds) && button.Get(Ui.Enabled), "installed content actions remain visible and reachable after scrolling");
                    }
                    UiVirtualList.ScrollIntoView(list, 0, ScrollAlignment.Start); Render();
                    Capture(host, $"installed-content-{(light ? "light" : "dark")}-{size.Item1}x{size.Item2}");
                }
            }

            model.ShowCatalog();
            Until(() => !model.CatalogLoading && model.CatalogItems.Count == 2 && model.CatalogItems.All(item => item.Meta.Contains("Установлено")), "catalog installed markers");
            Check(model.CatalogItems.Single(item => item.Id == InstalledContentHandler.DisabledProject).Meta.Contains("Отключено"),
                "catalog marks disabled installed content separately from active content");
            Open(InstalledContentHandler.KnownProject);
            Check(!model.CanInstall && model.InstallLabel == "Установлено" && !view.Find("install").Get(Ui.Enabled),
                "an exact installed version is disabled in project UI even when the JAR was renamed");
            int jobs = model.Sessions.Downloads.Jobs.Count;
            model.InstallProject(); Render();
            Check(model.Sessions.Downloads.Jobs.Count == jobs, "exact installed content cannot queue a duplicate download");
            model.ChooseProjectVersion(); model.DialogChoices.Single(item => item.Id == "known-v2").Invoke(); Render();
            Check(!model.CanInstall && model.InstallHelpLabel == "К установленным" && model.InstallHint.Contains("1.0.0"),
                "choosing another project version blocks duplicates and identifies the existing version");
            model.InstallProject(); Render();
            Check(model.Sessions.Downloads.Jobs.Count == jobs, "another version cannot bypass the installed project guard");
            Capture(host, "installed-content-other-version-840x620");
            model.ResolveInstallRequirement(); Installed(1);
            Check(model.Page == "catalog" && model.CatalogItems[0].Description == "renamed.jar", "installed-version guidance opens the matching installed file");

            Open(InstalledContentHandler.DisabledProject, "Отключено");
            Check(!model.CanInstall && model.InstallLabel == "Отключено" && model.InstallHelpLabel == "К установленным",
                "a disabled exact version offers its installed file instead of reinstalling it");
            model.ResolveInstallRequirement(); Installed(1);
            Check(model.CatalogItems[0].Primary == "Включить", "disabled project guidance exposes the enable action");
            model.CatalogItems[0].Invoke();
            Until(() => !model.CatalogLoading && model.CatalogItems.Count == 1 && model.CatalogItems[0].Primary == "Отключить", "enable installed mod");
            Check(File.Exists(disabledPath[..^".disabled".Length]) && !File.Exists(disabledPath), "enabling a recognized mod renames the actual disabled file");
            model.CatalogItems[0].Invoke(); model.CatalogItems[0].Invoke();
            Until(() => !model.CatalogLoading && model.CatalogItems.Count == 1 && model.CatalogItems[0].Primary == "Отключить", "rapid consecutive toggles");
            Check(model.Message.Length == 0 && File.Exists(disabledPath[..^".disabled".Length]) && !File.Exists(disabledPath),
                "rapid toggles use the updated file path before the background scan finishes");
            Open(InstalledContentHandler.DisabledProject);
            Check(!model.CanInstall && model.InstallLabel == "Установлено", "project status refreshes after the installed file is enabled");

            model.Query = ""; model.Catalog(); model.SetInstance(delayed.Id); Installed(0);
            string delayedPath = Path.Combine(instances.GetGameDir(delayed.Id), "mods", "late-response.jar");
            string delayedHash = WriteInstalledJar(delayedPath, "Старый ответ", "1.0.0", "late_fixture");
            handler.KnownHashes[delayedHash] = InstalledContentHandler.Version("known-v1", InstalledContentHandler.KnownProject, "1.0.0", delayedHash);
            handler.HoldNextHashes();
            var obsolete = model.SearchAsync(refreshInstalled: true);
            Until(() => handler.HeldStarted.Task.IsCompleted, "held hash lookup");
            Check(model.CatalogLoading, "hash lookup keeps installed loading state active while its HTTP result is pending");
            model.OpenProject(handler.Project(InstalledContentHandler.KnownProject), "mod", "1.21.1");
            Until(() => !model.ProjectLoading && model.HasProjectVersions && model.DescriptionItems.Count > 0, "description before hash lookup");
            Check(!model.CanInstall && Visible(view.Find("article")), "project description renders while the pending installed-file lookup still blocks installation");
            Capture(host, "installed-content-pending-index-840x620");
            model.SetInstance(empty.Id); model.Catalog(); Render();
            Check(model.CatalogItems.Count == 0, "selecting an empty instance immediately removes the previous instance's rows");
            handler.ReleaseHashes(); Await(obsolete, "cancelled previous-instance scan"); Installed(0);
            Check(model.SelectedInstance?.Id == empty.Id && model.Message.Length == 0, "late cancelled hash lookup cannot restore stale content or show an error");
            Open(InstalledContentHandler.KnownProject, "Установить", true);
            Check(model.CanInstall && model.InstallLabel == "Установить", "installed status belongs to the selected instance rather than the previously scanned one");

            model.Query = ""; model.Catalog(); model.ShowInstalled(); Installed(0);
            string copied = Path.Combine(instances.GetGameDir(empty.Id), "mods", "queued-copy.jar");
            Directory.CreateDirectory(Path.GetDirectoryName(copied)!);
            var downloadReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var queued = model.Sessions.Enqueue("Installed content fixture", empty.Id, async token =>
            {
                await downloadReady.Task.WaitAsync(token);
                File.Copy(renamed, copied);
            });
            Until(() => queued.State == DownloadState.Running, "running content job");
            Check(model.CatalogItems.Count == 0, "pending queue work cannot advertise content before its file is written");
            downloadReady.SetResult();
            Await(queued.Completion, "completed content job");
            Until(() => !model.CatalogLoading && model.CatalogItems.Any(item => item.Title == "Красивые биомы"), "queue-triggered installed refresh");
            Check(queued.State == DownloadState.Completed && model.CatalogItems.Count == 1 && model.CatalogItems[0].Description == "queued-copy.jar",
                "completed download queue work refreshes the open installed list without a manual search");
            Open(InstalledContentHandler.KnownProject);
            Check(!model.CanInstall && model.InstallLabel == "Установлено", "completed content job also updates project install state");

            model.Query = ""; model.Catalog(); model.ShowInstalled(); Installed(1);
            handler.HashUnavailable = true; model.SetInstance(offline.Id); Installed(1);
            Check(model.CatalogItems[0].Title == "Мод без подключения" && model.CatalogItems[0].Meta.Contains("Modrinth недоступен") &&
                !model.CatalogItems[0].Meta.Contains("Не найдено"), "network failure retains local metadata and is distinct from a successful unmatched lookup");
            int unavailableRequests = handler.HashRequests;
            handler.HashUnavailable = false;
            Await(model.SearchAsync(refreshInstalled: true), "retry unavailable hash lookup"); Installed(1);
            Check(handler.HashRequests > unavailableRequests && model.CatalogItems[0].Meta.Contains("Не найдено на Modrinth"),
                "an unavailable lookup can be retried and is not cached as a negative match");
            Check(handler.Unexpected.IsEmpty, "installed content checks use only mocked catalog endpoints");
        }
        finally
        {
            Environment.SetEnvironmentVariable("MECHANICA_DATA_DIR", previousData);
            Locale.Init(previousLanguage);
        }
    }

    private static string WriteInstalledJar(string path, string? name, string version, string id)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            using var writer = new StreamWriter(zip.CreateEntry(name == null ? "fixture.txt" : "fabric.mod.json").Open(), new UTF8Encoding(false));
            writer.Write(name == null ? "No mod metadata" : JsonSerializer.Serialize(new { schemaVersion = 1, id, version, name, environment = "client" }));
        }
        using var input = File.OpenRead(path);
        return Convert.ToHexString(SHA1.HashData(input)).ToLowerInvariant();
    }

    private sealed class InstalledContentHandler : HttpMessageHandler
    {
        public const string KnownProject = "installed-known", DisabledProject = "installed-disabled";
        public readonly ConcurrentDictionary<string, ModrinthVersion> KnownHashes = new(StringComparer.OrdinalIgnoreCase);
        public readonly ConcurrentQueue<string> Unexpected = new();
        public bool HashUnavailable;
        private int requests, hashRequests, projectsRequests;
        public int Requests => Volatile.Read(ref requests);
        public int HashRequests => Volatile.Read(ref hashRequests);
        public int ProjectsRequests => Volatile.Read(ref projectsRequests);
        private TaskCompletionSource? held;
        public TaskCompletionSource HeldStarted { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ModrinthVersion known, disabled;
        public InstalledContentHandler(string knownHash, string disabledHash)
        {
            known = Version("known-v1", KnownProject, "1.0.0", knownHash);
            disabled = Version("disabled-v1", DisabledProject, "2.4.0", disabledHash);
            KnownHashes[knownHash] = known; KnownHashes[disabledHash] = disabled;
        }
        public static ModrinthVersion Version(string id, string project, string version, string hash) => new()
        {
            Id = id, ProjectId = project, Name = "Release " + version, VersionNumber = version, VersionType = "release",
            GameVersions = ["1.21.1"], Loaders = ["fabric"],
            Files = [new() { Filename = project + "-" + version + ".jar", Primary = true, Hashes = new() { ["sha1"] = hash } }]
        };
        public ModrinthProject Project(string id) => new()
        {
            ProjectId = id, Slug = id, Title = id == KnownProject ? "Красивые биомы" : "Тихие шаги",
            Description = "Описание тестового дополнения.", ProjectType = "mod", Author = "Fixture", Downloads = 1234
        };
        private ModrinthProjectInfo Info(string id) => new()
        {
            Id = id, Slug = id, Title = Project(id).Title, Description = Project(id).Description,
            Body = "## Возможности\n\nТестовое дополнение для выбранной сборки.", ProjectType = "mod"
        };
        public void HoldNextHashes()
        {
            HeldStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            held = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        public void ReleaseHashes() => held?.TrySetResult();
        private static HttpResponseMessage Reply(object value) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json")
        };
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref requests);
            string path = request.RequestUri!.AbsolutePath;
            if (path == "/v2/version_files")
            {
                Interlocked.Increment(ref hashRequests);
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                var hashes = body.RootElement.GetProperty("hashes").EnumerateArray().Select(value => value.GetString()!).ToArray();
                if (held is { } pending)
                {
                    HeldStarted.TrySetResult();
                    // Finish an obsolete HTTP response after the user has selected another instance.
                    await pending.Task.ConfigureAwait(false);
                    held = null;
                }
                if (HashUnavailable) return new(HttpStatusCode.ServiceUnavailable);
                return Reply(hashes.Where(KnownHashes.ContainsKey).ToDictionary(hash => hash, hash => KnownHashes[hash]));
            }
            if (path == "/v2/projects")
            {
                Interlocked.Increment(ref projectsRequests);
                string json = Uri.UnescapeDataString(request.RequestUri.Query.Split('=', 2)[1]);
                return Reply(JsonSerializer.Deserialize<string[]>(json)!.Select(Info).ToArray());
            }
            if (path == "/v2/search") return Reply(new ModrinthSearchResult
            {
                TotalHits = 2, Hits = [Project(KnownProject), Project(DisabledProject)]
            });
            if (path == "/v2/project/" + KnownProject + "/version")
                return Reply(new[] { known, Version("known-v2", KnownProject, "2.0.0", new string('a', 40)) });
            if (path == "/v2/project/" + DisabledProject + "/version") return Reply(new[] { disabled });
            if (path == "/v2/project/" + KnownProject) return Reply(Info(KnownProject));
            if (path == "/v2/project/" + DisabledProject) return Reply(Info(DisabledProject));
            Unexpected.Enqueue(request.Method + " " + path);
            throw new InvalidOperationException("Unexpected installed-content fixture request: " + request.RequestUri);
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) held?.TrySetCanceled();
            base.Dispose(disposing);
        }
    }
}
