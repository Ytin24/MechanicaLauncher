using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
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
    private static void ModUpdateChecks(Pump context)
    {
        string? previousData = Environment.GetEnvironmentVariable("MECHANICA_DATA_DIR");
        string previousLanguage = Locale.CurrentLanguage;
        string data = Path.Combine(output, "mod-update-ui-data");
        Environment.SetEnvironmentVariable("MECHANICA_DATA_DIR", data);
        try
        {
            var instances = new InstanceManager(data);
            var main = instances.CreateInstance("Обновления модов", "1.21.1", LoaderType.Fabric, "0.19.3");
            var other = instances.CreateInstance("Пустая сборка", "1.21.1", LoaderType.Fabric, "0.19.3");
            var stale = instances.CreateInstance("Предыдущая сборка", "1.21.1", LoaderType.Fabric, "0.19.3");
            string mods = Path.Combine(instances.GetGameDir(main.Id), "mods");
            string enabled = Path.Combine(mods, "renamed.jar"), disabled = Path.Combine(mods, "quiet.jar.disabled");
            using var handler = new ModUpdatesUiHandler();
            File.WriteAllBytes(enabled, handler.GardenOld);
            File.WriteAllBytes(disabled, handler.QuietOld);
            File.WriteAllBytes(Path.Combine(instances.GetGameDir(stale.Id), "mods", "stale-garden.jar"), handler.GardenOld);
            string personal = Path.Combine(mods, "personal.txt");
            File.WriteAllText(personal, "keep this personal file");
            using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.modrinth.com") };
            var client = new ModrinthClient(http);
            using var model = new LauncherModel(new LauncherSettings
            {
                Username = "ModUpdates", Language = "ru", DiscordRpc = false, Animations = false, SelectedInstanceId = main.Id
            }, instances, client, new ModUpdateService(client, http));
            model.Dispatch = action => context.Post(_ => action(), null);
            using var presentation = new LauncherView(model);
            using var host = new HeadlessHost(presentation.View, 1180, 800);
            var view = presentation.View;
            double time = 0;
            void Render() { context.Drain(); host.Render(time += 1); host.Render(time += 1); }
            void Until(Func<bool> done, string operation)
            {
                var timer = Stopwatch.StartNew();
                while (!done())
                {
                    if (timer.Elapsed.TotalSeconds > 15)
                        throw new TimeoutException($"Mod update UI: {operation}; checking={model.CheckingModUpdates}, rows={model.CatalogItems.Count}, updates={model.ModUpdateCount}, status={model.ModUpdateStatus}, dialog={model.DialogTitle}, error={model.DialogError}, message={model.Message}, unexpected={string.Join(",", handler.Unexpected)}");
                    context.Drain(); Thread.Sleep(1);
                }
                Render();
            }
            void Await(Task task, string operation) { Until(() => task.IsCompleted, operation); task.GetAwaiter().GetResult(); }
            ItemModel Row(string fileName) => model.CatalogItems.Single(item => Path.GetFileName(item.Id) == fileName);
            void CheckUpdates(int count)
            {
                Until(() => model.CanCheckModUpdates, "ready to check updates");
                int requests = handler.UpdateRequests;
                model.CheckModUpdates();
                Until(() => !model.CheckingModUpdates && handler.UpdateRequests > requests && model.ModUpdateCount == count, "checking updates");
            }
            bool Fits(Element node, Rect area)
            {
                var box = UiTransform.VisualBounds(node);
                return box.Width > 0 && box.Height > 0 && box.X >= area.X - 1 && box.Y >= area.Y - 1 &&
                    box.X + box.Width <= area.X + area.Width + 1 && box.Y + box.Height <= area.Y + area.Height + 1;
            }
            void SelectOnly(string path)
            {
                model.SelectModUpdates(); Render();
                Check(model.DialogOpen && model.DialogFields.All(field => field.IsToggle), "available updates use explicit selection checkboxes");
                foreach (var field in model.DialogFields) field.IsChecked = field.Id.Equals(path, StringComparison.OrdinalIgnoreCase);
                model.AcceptDialog();
                Until(() => !model.DialogBusy && (model.DialogTitle == "Обновить моды?" || model.DialogError.Length > 0), "building the chosen update plan");
                Check(model.DialogTitle == "Обновить моды?" && model.DialogError.Length == 0, "selection opens a validated update plan: " + model.DialogError);
            }
            DownloadJob StartPlan()
            {
                int before = model.Sessions.Downloads.Jobs.Count;
                model.AcceptDialog();
                Until(() => !model.DialogOpen && model.Sessions.Downloads.Jobs.Count == before + 1, "queueing the confirmed update");
                return model.Sessions.Downloads.Jobs.Last();
            }

            model.ShowInstalled(); model.Catalog();
            Until(() => !model.CatalogLoading && model.CatalogItems.Count == 2 && model.CanCheckModUpdates, "installed index");
            Check(Row("quiet.jar.disabled").Primary == "Включить", "disabled mod starts with a separate enable action");
            CheckUpdates(2);
            Check(model.ModUpdateStatus.Contains("2") && Row("renamed.jar").Primary == "Обновить" && Row("renamed.jar").Meta.Contains("2.0.0"),
                "checking replaces the primary action with the newer compatible version");
            Check(Row("quiet.jar.disabled").Primary == "Обновить" && Row("quiet.jar.disabled").Secondary == "Включить" &&
                Row("quiet.jar.disabled").Meta.Contains("Отключено"), "updating and enabling a disabled mod remain distinct actions");
            Check(handler.LastMinecraft == "1.21.1" && handler.LastLoader == "fabric" && handler.ReleaseOnly,
                "update lookup uses the selected Minecraft, loader and stable release filter");

            foreach (bool light in new[] { false, true })
            foreach (var size in new[] { (1180u, 800u), (840u, 620u) })
            {
                model.LightTheme = light; host.Resize(size.Item1, size.Item2); Render();
                var area = new Rect(0, 0, size.Item1, size.Item2);
                foreach (string name in new[] { "catalogSearch", "checkModUpdates", "selectModUpdates" })
                    Check(Visible(view.Find(name)) && Fits(view.Find(name), area), "mod update toolbar action fits: " + name);
                var list = view.Find("catalogList");
                Check(list.Bounds.Height > 0 && Fits(list, area), "update toolbar leaves a visible bounded installed list");
                for (int index = 0; index < model.CatalogItems.Count; index++)
                {
                    UiVirtualList.ScrollIntoView(list, index, ScrollAlignment.Start); Render();
                    var row = UiVirtualList.GetRealizedItem(list, index)!;
                    var buttons = row.DescendantsAndSelf().Where(node => node.Type.Is(UiTypes.Button) && Visible(node)).ToArray();
                    Check(buttons.Any(node => node.Get(Ui.Text) == "Обновить"), "native installed row exposes its update action");
                    foreach (var button in buttons) Check(Fits(button, list.Bounds) && button.Get(Ui.Enabled), "all update row actions remain reachable below the toolbar");
                }
                UiVirtualList.ScrollIntoView(list, 0, ScrollAlignment.Start); Render();
                Capture(host, $"mod-updates-{(light ? "light" : "dark")}-{size.Item1}x{size.Item2}");
                model.SelectModUpdates(); Render();
                Check(Fits(view.Find("dialogAccept"), view.Find("dialog").Bounds) && Fits(view.Find("dialogFields"), view.Find("dialog").Bounds),
                    "update selection and confirm action fit the modal");
                var selectedField = model.DialogFields[0];
                var checkbox = view.Interactive().Single(node => node.Get(Ui.Text) == selectedField.Label);
                var bounds = UiTransform.VisualBounds(checkbox);
                host.Click(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2); Render();
                Check(!selectedField.IsChecked && model.DialogFields[1].IsChecked, "native checkbox changes only the chosen update selection");
                host.Click(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2); Render();
                Check(selectedField.IsChecked, "update can be selected again through its native checkbox");
                Capture(host, $"mod-update-selection-{(light ? "light" : "dark")}-{size.Item1}x{size.Item2}");
                model.CloseDialog(); Render();
            }

            model.SelectModUpdates();
            foreach (var field in model.DialogFields) field.IsChecked = false;
            model.AcceptDialog(); Render();
            Check(model.DialogOpen && model.DialogError.Length > 0 && model.Sessions.Downloads.Jobs.Count == 0, "empty selection reports validation and queues no update");
            model.CloseDialog();
            SelectOnly(disabled);
            Check(model.DialogBody.Contains("Тихие шаги") && !model.DialogBody.Contains("Красивые биомы") && handler.DownloadRequests == 0,
                "update review contains only the selected disabled mod and downloads nothing before consent");
            var disabledJob = StartPlan(); Await(disabledJob.Completion, "updating disabled content");
            Check(disabledJob.State == DownloadState.Completed, "confirmed selected update completes in the download queue: " + disabledJob.Snapshot().Error);
            Until(() => !model.CheckingModUpdates && !model.CatalogLoading && model.ModUpdateCount == 1 && Row("quiet.jar.disabled").Meta.Contains("2.0.0"),
                "refreshing versions after queue completion");
            Check(File.ReadAllBytes(disabled).SequenceEqual(handler.QuietNew) && !File.Exists(Path.Combine(mods, "quiet.jar")), "updating a disabled mod replaces its bytes without enabling or renaming it");
            Check(File.ReadAllBytes(enabled).SequenceEqual(handler.GardenOld), "an unchecked mod keeps its original file");

            Row("renamed.jar").Invoke();
            Until(() => model.DialogOpen && model.DialogTitle == "Обновить моды?", "opening single-mod update plan");
            Check(model.DialogBody.Contains("Красивые биомы") && model.DialogBody.Contains("Поддержка биомов") && model.DialogBody.Contains("зависимость"),
                "single-mod update review includes its required dependency");
            Check(!model.DialogBody.Contains("optional", StringComparison.OrdinalIgnoreCase), "optional dependencies are excluded from the review");
            int jobsBeforeCancel = model.Sessions.Downloads.Jobs.Count, downloadsBeforeCancel = handler.DownloadRequests;
            model.CloseDialog(); Render();
            Check(model.Sessions.Downloads.Jobs.Count == jobsBeforeCancel && handler.DownloadRequests == downloadsBeforeCancel, "cancelling the plan has no queue or file side effects");

            Row("renamed.jar").Invoke(); Until(() => model.DialogOpen && !model.DialogBusy, "reopening the required-dependency plan");
            handler.HoldDownload();
            var cancelledJob = StartPlan(); Until(() => handler.DownloadStarted.Task.IsCompleted, "staged download started");
            Check(cancelledJob.State == DownloadState.Running && File.ReadAllBytes(enabled).SequenceEqual(handler.GardenOld), "a queued download preserves the existing JAR until commit");
            cancelledJob.Cancel(); Await(cancelledJob.Completion, "cancelling an update download");
            Check(cancelledJob.State == DownloadState.Cancelled && File.ReadAllBytes(enabled).SequenceEqual(handler.GardenOld) &&
                !File.Exists(Path.Combine(mods, "support-1.0.0.jar")), "cancelled download keeps the previous mod and adds no dependency");
            Until(() => model.CanCheckModUpdates, "ready after cancelled update"); CheckUpdates(1);

            Row("renamed.jar").Invoke(); Until(() => model.DialogOpen && !model.DialogBusy, "final update plan");
            foreach (bool light in new[] { false, true })
            foreach (var size in new[] { (1180u, 800u), (840u, 620u) })
            {
                model.LightTheme = light; host.Resize(size.Item1, size.Item2); Render();
                Check(Fits(view.Find("dialogAccept"), view.Find("dialog").Bounds), "required dependency plan keeps its update action visible");
                Capture(host, $"mod-update-plan-{(light ? "light" : "dark")}-{size.Item1}x{size.Item2}");
            }
            var updatedJob = StartPlan(); Await(updatedJob.Completion, "applying mod and required dependency");
            Check(updatedJob.State == DownloadState.Completed, "real JAR replacement and dependency installation finish successfully: " + updatedJob.Snapshot().Error);
            Until(() => !model.CheckingModUpdates && !model.CatalogLoading && model.CatalogItems.Count == 3 &&
                model.ModUpdateStatus.Contains("Новых совместимых версий не найдено"), "refreshing installed versions after completed update");
            Check(File.ReadAllBytes(enabled).SequenceEqual(handler.GardenNew) && !File.Exists(Path.Combine(mods, "garden-2.0.0.jar")), "updated renamed mod is replaced in place with the exact downloaded ZIP");
            Check(File.ReadAllBytes(Path.Combine(mods, "support-1.0.0.jar")).SequenceEqual(handler.Support), "required dependency installs as an actual verified JAR");
            Check(File.ReadAllBytes(disabled).SequenceEqual(handler.QuietNew) && File.ReadAllText(personal) == "keep this personal file", "other installed files and personal data survive the update");
            Check(model.ModUpdateCount == 0 && Row("quiet.jar.disabled").Primary == "Включить", "automatic post-queue refresh removes obsolete update actions without enabling disabled content");

            handler.FailUpdates = true; CheckUpdates(0);
            Check(model.ModUpdateStatus.Contains("Не удалось проверить") && !model.ModUpdateStatus.Contains("Новых совместимых"), "HTTP failure is shown as an unavailable check, not as no updates");
            Check(model.CanCheckModUpdates && model.CatalogItems.Count == 3, "failed update check keeps the installed list and retry action usable");
            handler.FailUpdates = false; CheckUpdates(0);
            Check(model.ModUpdateStatus.Contains("Новых совместимых версий не найдено"), "retry recovers from the failed update lookup");

            model.SetInstance(stale.Id); Until(() => !model.CatalogLoading && model.CatalogItems.Count == 1 && model.CanCheckModUpdates, "opening an instance with a pending available update");
            handler.HoldUpdates(ignoreCancellation: true); model.CheckModUpdates();
            Until(() => handler.UpdatesStarted.Task.IsCompleted && model.CheckingModUpdates, "holding an old-instance update response");
            model.SetInstance(other.Id);
            Until(() => !model.CatalogLoading && model.CatalogItems.Count == 0, "showing another instance");
            handler.ReleaseUpdates(); Until(() => !model.CheckingModUpdates, "finishing the obsolete response");
            Check(model.SelectedInstance?.Id == other.Id && model.ModUpdateCount == 0 && model.ModUpdateStatus.Length == 0 && model.CatalogItems.Count == 0,
                "late update response cannot populate another instance or retain its status");

            model.SetInstance(main.Id); Until(() => !model.CatalogLoading && model.CatalogItems.Count == 3 && model.CanCheckModUpdates, "returning to the updated instance");
            handler.HoldUpdates(ignoreCancellation: false); model.CheckModUpdates();
            Until(() => handler.UpdatesStarted.Task.IsCompleted, "holding a cancellable update check");
            model.Home(); Until(() => !model.CheckingModUpdates, "cancelling a check on page change");
            Check(handler.CancelledChecks > 0 && model.Page == "home" && !model.Error, "leaving the page cancels the update request without an error notification");
            Check(handler.Unexpected.IsEmpty, "all update and dependency traffic stays inside the strict HTTP fixture");
        }
        finally { Environment.SetEnvironmentVariable("MECHANICA_DATA_DIR", previousData); Locale.Init(previousLanguage); }
    }

    private sealed class ModUpdatesUiHandler : HttpMessageHandler
    {
        public readonly byte[] GardenOld = ModUpdateJar("garden_fixture", "Красивые биомы", "1.0.0");
        public readonly byte[] GardenNew = ModUpdateJar("garden_fixture", "Красивые биомы", "2.0.0", "support_fixture");
        public readonly byte[] QuietOld = ModUpdateJar("quiet_fixture", "Тихие шаги", "1.0.0");
        public readonly byte[] QuietNew = ModUpdateJar("quiet_fixture", "Тихие шаги", "2.0.0");
        public readonly byte[] Support = ModUpdateJar("support_fixture", "Поддержка биомов", "1.0.0");
        public readonly ConcurrentQueue<string> Unexpected = new();
        private readonly Dictionary<string, ModrinthVersion> versions = new(), hashes = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, byte[]> downloads = new();
        private TaskCompletionSource? heldUpdates, heldDownload;
        private bool ignoreUpdateCancellation;
        public TaskCompletionSource UpdatesStarted { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource DownloadStarted { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool FailUpdates;
        public string LastMinecraft = "", LastLoader = "";
        public bool ReleaseOnly;
        public int UpdateRequests, DownloadRequests, CancelledChecks;
        public ModUpdatesUiHandler()
        {
            Add("garden-v1", "garden", "Красивые биомы", "1.0.0", GardenOld, 1);
            Add("garden-v2", "garden", "Красивые биомы", "2.0.0", GardenNew, 2);
            Add("quiet-v1", "quiet", "Тихие шаги", "1.0.0", QuietOld, 1);
            Add("quiet-v2", "quiet", "Тихие шаги", "2.0.0", QuietNew, 2);
            Add("support-v1", "support", "Поддержка биомов", "1.0.0", Support, 1);
            versions["garden-v2"].Dependencies =
            [
                new() { ProjectId = "support", VersionId = "support-v1", DependencyType = "required" },
                new() { ProjectId = "optional", VersionId = "optional-v1", DependencyType = "optional" }
            ];
        }
        private void Add(string id, string project, string name, string number, byte[] bytes, int day)
        {
            string sha1 = Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant();
            string file = project + "-" + number + ".jar";
            var version = new ModrinthVersion
            {
                Id = id, ProjectId = project, Name = name, VersionNumber = number, VersionType = "release",
                DatePublished = new DateTimeOffset(2026, 1, day, 0, 0, 0, TimeSpan.Zero), GameVersions = ["1.21.1"], Loaders = ["fabric"],
                Files = [new() { Filename = file, Size = bytes.Length, Primary = true, Url = "https://cdn.modrinth.com/" + file,
                    Hashes = new() { ["sha1"] = sha1, ["sha512"] = Convert.ToHexString(SHA512.HashData(bytes)).ToLowerInvariant() } }]
            };
            versions.Add(id, version); hashes.Add(sha1, version); downloads.Add("/" + file, bytes);
        }
        public void HoldUpdates(bool ignoreCancellation)
        {
            ignoreUpdateCancellation = ignoreCancellation;
            UpdatesStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            heldUpdates = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        public void ReleaseUpdates() => heldUpdates?.TrySetResult();
        public void HoldDownload()
        {
            DownloadStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            heldDownload = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        private static HttpResponseMessage Reply(object value) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json")
        };
        private ModrinthProjectInfo Info(string id) => new()
        {
            Id = id, Slug = id, Title = versions.Values.First(version => version.ProjectId == id).Name,
            ProjectType = "mod", Body = "Описание локального тестового мода."
        };
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string path = request.RequestUri!.AbsolutePath;
            if (request.RequestUri.Host == "cdn.modrinth.com" && downloads.TryGetValue(path, out var bytes))
            {
                Interlocked.Increment(ref DownloadRequests);
                if (heldDownload is { } pending)
                {
                    DownloadStarted.TrySetResult();
                    try { await pending.Task.WaitAsync(cancellationToken).ConfigureAwait(false); }
                    finally { if (ReferenceEquals(heldDownload, pending)) heldDownload = null; }
                }
                return new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
            }
            if (path is "/v2/version_files" or "/v2/version_files/update")
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                string[] requested = body.RootElement.GetProperty("hashes").EnumerateArray().Select(value => value.GetString()!).ToArray();
                if (path.EndsWith("/update", StringComparison.Ordinal))
                {
                    Interlocked.Increment(ref UpdateRequests);
                    LastMinecraft = body.RootElement.GetProperty("game_versions")[0].GetString()!;
                    LastLoader = body.RootElement.GetProperty("loaders")[0].GetString()!;
                    ReleaseOnly = body.RootElement.GetProperty("version_types").GetArrayLength() == 1 && body.RootElement.GetProperty("version_types")[0].GetString() == "release";
                    if (heldUpdates is { } pending)
                    {
                        UpdatesStarted.TrySetResult();
                        try
                        {
                            if (ignoreUpdateCancellation) await pending.Task.ConfigureAwait(false);
                            else await pending.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) { Interlocked.Increment(ref CancelledChecks); throw; }
                        finally { if (ReferenceEquals(heldUpdates, pending)) heldUpdates = null; }
                    }
                    if (FailUpdates) return new(HttpStatusCode.ServiceUnavailable);
                    return Reply(requested.Where(hashes.ContainsKey).ToDictionary(hash => hash, hash =>
                        hashes[hash].ProjectId switch { "garden" => versions["garden-v2"], "quiet" => versions["quiet-v2"], _ => hashes[hash] }));
                }
                return Reply(requested.Where(hashes.ContainsKey).ToDictionary(hash => hash, hash => hashes[hash]));
            }
            if (path == "/v2/projects")
            {
                string json = Uri.UnescapeDataString(request.RequestUri.Query.Split('=', 2)[1]);
                return Reply(JsonSerializer.Deserialize<string[]>(json)!.Select(Info).ToArray());
            }
            if (path.StartsWith("/v2/version/", StringComparison.Ordinal) && versions.TryGetValue(path[12..], out var version)) return Reply(version);
            if (path.StartsWith("/v2/project/", StringComparison.Ordinal))
            {
                string id = path[12..];
                if (id.EndsWith("/version", StringComparison.Ordinal) && versions.Values.Any(item => item.ProjectId == id[..^8]))
                    return Reply(versions.Values.Where(item => item.ProjectId == id[..^8]).ToArray());
                if (versions.Values.Any(item => item.ProjectId == id)) return Reply(Info(id));
            }
            if (path == "/v2/search") return Reply(new ModrinthSearchResult());
            Unexpected.Enqueue(request.Method + " " + request.RequestUri);
            throw new InvalidOperationException("Unexpected mod-update HTTP request: " + request.RequestUri);
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { heldDownload?.TrySetCanceled(); heldUpdates?.TrySetCanceled(); }
            base.Dispose(disposing);
        }
    }

    private static byte[] ModUpdateJar(string id, string name, string version, string? dependency = null)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var writer = new StreamWriter(archive.CreateEntry("fabric.mod.json").Open(), new UTF8Encoding(false));
            var dependencies = new Dictionary<string, string> { ["fabricloader"] = ">=0.19.3", ["minecraft"] = "1.21.1", ["java"] = ">=21" };
            if (dependency != null) dependencies.Add(dependency, ">=1.0.0");
            writer.Write(JsonSerializer.Serialize(new { schemaVersion = 1, id, name, version, environment = "*", depends = dependencies }));
        }
        return stream.ToArray();
    }
}
