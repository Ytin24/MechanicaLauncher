using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using MechanicaLauncher.Core.Instances;
using MechanicaLauncher.Core.Localization;
using MechanicaLauncher.Core.Models;
using MechanicaLauncher.Core.Profiles;
using MechanicaLauncher.Desktop;
using Nitidus;
using Nitidus.Native;

internal static partial class Program
{
    private static double UxChecks(LauncherModel model, LauncherView presentation, HeadlessHost host, Pump context,
        GameInstance instance, GameInstance second, double time)
    {
        var view = presentation.View;
        void Render() { context.Drain(); host.Render(time += 1); host.Render(time += 1); }
        void Until(Func<bool> condition)
        {
            var wait = System.Diagnostics.Stopwatch.StartNew();
            while (!condition())
            {
                if (wait.Elapsed.TotalSeconds > 5) throw new TimeoutException("UX response did not settle.");
                context.Drain(); System.Threading.Thread.Sleep(1);
            }
            Render();
        }
        void Click(string name)
        {
            Render();
            var node = view.Find(name);
            Check(Visible(node) && node.Get(Ui.Enabled) && node.Bounds.Width > 0 && node.Bounds.Y + node.Bounds.Height <= 800, "UX action is visible and enabled: " + name);
            host.Click(node.Bounds.X + node.Bounds.Width / 2, node.Bounds.Y + node.Bounds.Height / 2); Render();
        }
        HttpResponseMessage Reply(object value) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json")
        };
        HttpResponseMessage Page(int start, int count, int total) => Reply(new ModrinthSearchResult
        {
            TotalHits = total,
            Hits = Enumerable.Range(start, count).Select(i => new ModrinthProject
            {
                ProjectId = "ux-" + i, Slug = "ux-" + i, Title = "Приключения " + i,
                Description = "Новые миры и знакомые приключения.", Author = "Fixture", Downloads = 12000
            }).ToList()
        });
        void Fits()
        {
            foreach (var node in view.Interactive().Where(n => Visible(n) && n.Bounds.Width > 0 && n.Bounds.Y < 800))
                Check(node.Bounds.X >= 0 && node.Bounds.X + node.Bounds.Width <= 840, "UX control fits narrow window: " + node.Get(Ui.Text));
        }

        string language = Locale.CurrentLanguage;
        Locale.Init("ru"); model.Settings.Language = "ru"; model.LightTheme = false;
        host.Resize(840, 800, 1); model.SetInstance(instance.Id);
        model.Library(); model.LibraryQuery = "несуществующая сборка";
        model.FilterLibrary(); model.DialogChoices.Single(c => c.Title == "Quilt").Invoke(); Render();
        Check(model.LibraryItems.Count == 0 && model.LibraryEmptyTitle == "Сборки не найдены", "filtered library does not pretend this is first run");
        Check(Visible(view.Find("libraryEmpty")) && !Visible(view.Find("instanceList")), "empty library has a compact explanation instead of a blank list");
        Fits(); Capture(host, "ux-library-filter-dark-840");
        model.LightTheme = true; Render(); Capture(host, "ux-library-filter-light-840");
        Click("resetLibrary");
        Check(!model.HasLibraryFilters && model.LibraryItems.Count == model.Instances.GetAllInstances().Count, "one action clears both library query and loader");

        model.Query = ""; model.Catalog(); model.SetContentType("modpack");
        CatalogHandler.NextSearch = Page(0, 20, 60); model.Search(); Render();
        var list = view.Find("catalogList");
        UiVirtualList.ScrollIntoView(list, 14, ScrollAlignment.Start); Render();
        var scroll = UiVirtualList.Inspect(list);
        var anchor = UiVirtualList.GetItemKey(list, scroll.FirstVisible);
        float anchorY = UiVirtualList.GetRealizedItem(list, scroll.FirstVisible)!.Bounds.Y - list.Bounds.Y;
        var row = model.CatalogItems[14];
        var item = UiVirtualList.GetRealizedItem(list, 14)!;
        var open = item.DescendantsAndSelf().Single(n => n.Type.Name == "Button" && n.Get(Ui.Text) == "Подробнее");
        int requests = CatalogHandler.SearchRequests;
        string status = model.CatalogStatus;
        host.Click(open.Bounds.X + 20, open.Bounds.Y + 15); Render();
        Check(model.Page == "project" && model.CanInstall, "project opens from scrolled catalog row");
        Fits(); Capture(host, "ux-project-light-840");
        Click("backToCatalog");
        Check(CatalogHandler.SearchRequests == requests && ReferenceEquals(row, model.CatalogItems[14]), "back reuses loaded catalog without a new request");
        var restoredScroll = UiVirtualList.Inspect(list);
        Check(scroll.Offset > 0 && restoredScroll.Offset > 0 && Equals(anchor, UiVirtualList.GetItemKey(list, restoredScroll.FirstVisible)) &&
            Math.Abs(UiVirtualList.GetRealizedItem(list, restoredScroll.FirstVisible)!.Bounds.Y - list.Bounds.Y - anchorY) < 1,
            "back preserves the visible catalog item and its exact reading position");
        Check(model.CatalogStatus == status && model.MoreCatalog, "project metadata does not replace catalog result count or pagination");

        CatalogHandler.NextSearch = new(HttpStatusCode.ServiceUnavailable);
        model.LoadMoreCatalog(); Render();
        Check(model.CatalogFailed && model.CatalogItems.Count == 20 && ReferenceEquals(row, model.CatalogItems[14]), "failed next page retains loaded rows");
        Check(Visible(view.Find("catalogRetry")) && !Visible(view.Find("moreCatalog")), "pagination failure offers a single retry action");
        Fits(); Capture(host, "ux-pagination-error-light-840");
        CatalogHandler.NextSearch = Page(20, 20, 60); Click("retryCatalog");
        Check(!model.CatalogFailed && model.CatalogItems.Count == 40 && CatalogHandler.LastSearch.Contains("offset=20"), "retry requests the failed page and appends it once");

        var pending = new TaskCompletionSource<HttpResponseMessage>(); CatalogHandler.DelayNextSearch = pending;
        requests = CatalogHandler.SearchRequests;
        model.LoadMoreCatalog(); model.LoadMoreCatalog(); Render();
        Check(model.CatalogLoading && CatalogHandler.SearchRequests == requests + 1, "repeated load-more clicks do not duplicate requests");
        model.CatalogItems[14].Invoke(); Render();
        pending.SetResult(Page(40, 20, 60)); Render();
        Click("backToCatalog");
        Check(model.CatalogItems.Count == 40 && model.MoreCatalog && !model.CatalogLoading, "cancelled next page leaves loaded rows and a working continuation");
        Check(model.Message.Length == 0, "cancelled catalog request does not show a false error");

        model.CatalogItems[0].Invoke(); Render();
        model.SetInstance(second.Id); Render();
        Check(!model.CanInstall && model.InstallHint.Length > 0 && Visible(view.Find("installHelp")), "unavailable project file explains how to recover");
        requests = CatalogHandler.SearchRequests; Click("backToCatalog");
        Check(CatalogHandler.SearchRequests == requests + 1 && CatalogHandler.LastSearch.Contains("versions:1.20.1"), "changed project target invalidates cached catalog compatibility");
        model.SetInstance(instance.Id); Render();
        CatalogHandler.NextSearch = Page(0, 20, 60); model.Search(); Render();
        model.Query = "новый запрос"; model.LoadMoreCatalog(); Render();
        Check(CatalogHandler.LastSearch.Contains("offset=0") && model.CatalogItems.Count == 1, "load-more after typing starts a new search instead of mixing result sets");

        model.Query = "неизвестный модпак";
        model.ChooseCategory(); CatalogHandler.NextSearch = Page(0, 0, 0);
        model.DialogChoices.Single(c => c.Title == "adventure").Invoke(); Render();
        Check(model.CatalogEmptyTitle == "Ничего не нашлось" && model.CatalogEmptyAction == "Сбросить фильтры", "zero search results have an actionable empty state");
        Fits(); Capture(host, "ux-no-results-light-840");
        Click("catalogEmptyAction");
        Check(!model.HasCatalogFilters && model.CompatibleOnly && model.ContentType == "modpack" && CatalogHandler.LastSearch.Contains("versions:1.21.1"), "clearing search and category preserves type and Minecraft compatibility");

        CatalogHandler.NextSearch = new(HttpStatusCode.ServiceUnavailable);
        model.Query = "сохранить запрос"; model.Search(); Render();
        Check(model.CatalogFailed && model.CatalogEmptyAction == "Повторить", "network error is not presented as zero results");
        model.LightTheme = false; Render(); Fits(); Capture(host, "ux-network-error-dark-840");
        Click("catalogEmptyAction");
        Check(!model.CatalogFailed && model.Query == "сохранить запрос" && model.CatalogItems.Count == 1, "network retry retains the exact search");

        pending = new(); CatalogHandler.DelayNextSearch = pending; model.Search(); Render();
        Check(model.CatalogLoading && model.CatalogEmptyAction.Length == 0 && !Visible(view.Find("catalogEmptyAction")), "loading state cannot be mistaken for an empty result");
        Capture(host, "ux-loading-dark-840");
        pending.SetException(new TaskCanceledException("fixture request timeout")); Until(() => !model.CatalogLoading);
        Check(model.CatalogFailed && !model.CatalogLoading, "HTTP timeout offers retry instead of an empty result");
        Click("catalogEmptyAction");

        CatalogHandler.NextVersions = new(HttpStatusCode.ServiceUnavailable);
        model.CatalogItems[0].Invoke(); Render();
        Check(model.ProjectFailed && !model.CanInstall && !view.Find("projectVersion").Get(Ui.Enabled), "project load failure disables empty release chooser");
        Click("installHelp");
        Check(!model.ProjectFailed && model.CanInstall && model.InstallHint.Length == 0, "project retry restores a valid installation");
        model.Sessions.Events.SetActive(new() { Ui = new() { AllowModInstall = false } }); Render();
        Check(!model.CanInstall && model.InstallHint.Contains("события") && !Visible(view.Find("installHelp")), "event restriction explains why installation is disabled");
        model.Sessions.Events.Clear();

        var vanilla = model.Instances.CreateInstance("Vanilla UX", "1.21.1"); context.Drain();
        model.SetInstance(vanilla.Id); model.OpenProject(new() { ProjectId = "fixture", Title = "Мод для Fabric" }, "mod", "1.21.1"); Render();
        Check(!model.CanInstall && model.InstallHint.Contains("Vanilla"), "vanilla mod target explains the loader requirement before file availability");
        Click("installHelp"); Check(model.DialogOpen, "loader guidance opens instance selection");
        model.DialogChoices.Single(c => c.Id == instance.Id).Invoke(); Render();

        string world = Path.Combine(model.Instances.GetGameDir(instance.Id), "saves", "Тестовый мир");
        Directory.CreateDirectory(world); File.WriteAllBytes(Path.Combine(world, "level.dat"), []);
        CatalogHandler.NextVersions = Reply(new ModrinthVersion[]
        {
            new() { Id = "world", VersionNumber = "1.0", GameVersions = ["1.21.1"], Loaders = ["datapack"], Files = [new() { Filename = "world.zip" }] }
        });
        model.OpenProject(new() { ProjectId = "fixture", Title = "Новые приключения в твоём мире" }, "datapack", "1.21.1"); Render();
        Check(!model.CanInstall && model.InstallHelpLabel == "Выбрать мир", "datapack installation explains the missing world");
        Check(view.Interactive().Count(n => Visible(n) && n.Get(Ui.Text) == "Выбрать мир") == 1, "datapack has one world-selection action");
        Fits(); Capture(host, "ux-world-required-dark-840");
        Click("installHelp"); model.DialogChoices.Single(c => c.Title == "Тестовый мир").Invoke(); Render();
        Check(model.CanInstall && model.InstallHint.Length == 0 && model.World == "Тестовый мир", "choosing a world enables the compatible datapack");
        model.Catalog(); model.SetContentType("shader"); model.Query = ""; model.ShowInstalled(); Render();
        Check(model.CatalogEmptyTitle == "Здесь пока пусто" && model.CatalogEmptyAction == "Найти дополнения", "installed empty state points to discovery");
        Click("catalogEmptyAction"); Check(!model.InstalledOnly && model.CatalogItems.Count > 0, "installed empty action returns to matching content type");

        using (var empty = new LauncherModel(new LauncherSettings { Username = "Player", Language = "ru", DiscordRpc = false }, new InstanceManager(Path.Combine(output, "first-run")), model.CatalogClient))
        using (var emptyView = new LauncherView(empty))
        using (var emptyHost = new HeadlessHost(emptyView.View, 840, 800))
        {
            emptyHost.Render(1); emptyHost.Render(2);
            var create = emptyView.View.Find("createFirst");
            Check(Visible(create) && create.Get(Ui.Text) == "Создать сборку" && !Visible(emptyView.View.Find("play")), "first run offers creation without a nonfunctional play action");
            Capture(emptyHost, "ux-first-run-dark-840");
            Check(!Visible(emptyView.View.Find("instancePicker")) && Visible(emptyView.View.Find("importFirst")) && Visible(emptyView.View.Find("browseModpacks")), "empty home exposes creation, import and modpacks without an empty picker");
            emptyHost.Click(create.Bounds.X + 10, create.Bounds.Y + 10);
            emptyHost.Render(3); emptyHost.Render(4);
            Check(empty.Page == "create" && !empty.DialogOpen, "first-run primary action opens instance creation");
            Capture(emptyHost, "ux-first-create-dark-840");
        }
        model.Settings.Language = language; Locale.Init(language); model.Changed();
        model.SetInstance(instance.Id); model.Query = ""; model.Library(); Render();
        return time;
    }
}
