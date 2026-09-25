using MechanicaLauncher.Core.Models;
using MechanicaLauncher.Core.Mods;

namespace MechanicaLauncher.Desktop;

public sealed partial class LauncherModel
{
    public ObservableCollection<ItemModel> CatalogItems { get; } = [];
    public ObservableCollection<ItemModel> DescriptionItems { get; } = [];
    private string query = "";
    private CancellationTokenSource? searchDelay;
    public string Query
    {
        get => query;
        set
        {
            if (!Set(ref query, value) || Page != "catalog") return;
            searchDelay?.Cancel(); searchDelay?.Dispose();
            searchDelay = CancellationTokenSource.CreateLinkedTokenSource(PageToken);
            var token = searchDelay.Token;
            Run(async () => { await Task.Delay(350, token); token.ThrowIfCancellationRequested(); await SearchAsync(false); });
        }
    }
    public string ContentType { get; private set; } = "mod";
    private bool compatibleOnly = true;
    public bool CompatibleOnly { get => compatibleOnly; set { if (Set(ref compatibleOnly, value)) Search(); } }
    public string CompatibilityLabel => SelectedInstance == null ? T("Выбери сборку для фильтра по версии", "Select an instance to filter by version") : T("Только Minecraft ", "Only Minecraft ") + SelectedInstance.McVersion;
    public string CatalogStatus { get; private set; } = "";
    public bool CatalogLoading { get; private set; }
    public bool CatalogFailed { get; private set; }
    public bool InstalledOnly { get; private set; }
    public bool MoreCatalog { get; private set; }
    public bool HasCatalogFilters => !string.IsNullOrWhiteSpace(Query) || !InstalledOnly && category != null;
    public string CatalogEmptyTitle => CatalogLoading ? T("Ищу дополнения", "Searching for extras")
        : CatalogFailed ? T("Каталог недоступен", "Catalog unavailable")
        : InstalledOnly && SelectedInstance == null ? T("Нужна сборка для установки", "Choose an instance first")
        : InstalledOnly && NeedsWorld && World == null ? T("В какой мир добавить датапаки?", "Which world needs datapacks?")
        : InstalledOnly && !HasCatalogFilters ? T("Здесь пока пусто", "Nothing installed yet")
        : T("Ничего не нашлось", "No results found");
    public string CatalogEmptyHint => CatalogLoading ? T("Загружаю результаты с Modrinth…", "Loading results from Modrinth…")
        : CatalogFailed ? T("Проверь подключение или повтори чуть позже. Фильтры поиска сохранены.", "Check your connection or try again later. Your search filters are saved.")
        : InstalledOnly && SelectedInstance == null ? T("Создай сборку или выбери существующую — дополнения устанавливаются отдельно для каждой.", "Create or select an instance. Each instance has its own extras.")
        : InstalledOnly && NeedsWorld && World == null ? T("Датапаки хранятся внутри мира. Если миров ещё нет, сначала создай мир в Minecraft.", "Datapacks belong to a world. Create a world in Minecraft first if you don't have one.")
        : InstalledOnly && !HasCatalogFilters ? T("Выбери дополнение в каталоге. После установки оно появится здесь.", "Choose an extra in Discover. It will appear here after installation.")
        : InstalledOnly ? T("Попробуй другое название файла или сбрось поиск.", "Try another file name or clear the search.")
        : HasCatalogFilters ? T("Попробуй другое название или сбрось поиск и категорию. Версия Minecraft останется прежней.", "Try another name or clear the search and category. Your Minecraft version filter will stay as selected.")
        : T("Для выбранной версии и типа дополнений результатов нет. Можно выбрать другую сборку или изменить фильтры.", "No results for this version and content type. Choose another instance or change the filters.");
    public string CatalogEmptyAction => CatalogLoading ? "" : CatalogFailed ? T("Повторить", "Retry")
        : InstalledOnly && SelectedInstance == null ? ShowInstancesPage ? T("К сборкам", "Your instances") : ""
        : InstalledOnly && NeedsWorld && World == null ? T("Выбрать мир", "Choose world")
        : HasCatalogFilters ? T("Сбросить фильтры", "Clear filters")
        : InstalledOnly ? T("Найти дополнения", "Find extras")
        : CompatibleOnly && SelectedInstance != null ? T("Выбрать сборку", "Select instance") : "";
    public string SortLabel => sortBy switch { "downloads" => T("По загрузкам", "Downloads"), "newest" => T("Новые", "Newest"), "updated" => T("Обновлённые", "Updated"), _ => T("По релевантности", "Relevance") };
    private string sortBy = "relevance";
    private string? category;
    public string CategoryLabel => category ?? T("Все категории", "All categories");
    private int catalogOffset, catalogGeneration;
    private string catalogResultStatus = "";
    private CancellationTokenSource? catalogCancellation;
    private (string Query, string Type, bool Compatible, string? Instance, string? Minecraft, LoaderType? Loader, string Sort, string? Category, string Language)? catalogSelection;
    private (string, string, bool, string?, string?, LoaderType?, string, string?, string) CurrentCatalogSelection =>
        (Query.Trim(), ContentType, CompatibleOnly, SelectedInstance?.Id, SelectedInstance?.McVersion, SelectedInstance?.Loader, sortBy, category, Locale.CurrentLanguage);
    private readonly Dictionary<string, string?> catalogIcons = [];
    private ModrinthProject? project;
    private ModrinthProjectInfo? projectInfo;
    private string? projectMinecraft;
    private string projectType = "mod";
    private readonly List<ModrinthVersion> projectVersions = [];
    private ModrinthVersion? selectedVersion;
    public string ProjectTitle => project?.Title ?? "";
    public string ProjectDescription => projectInfo?.Description ?? project?.Description ?? "";
    public string ProjectMeta => project == null ? "" : (string.IsNullOrWhiteSpace(project.Author) ? "" : project.Author + "  ·  ") + project.DownloadsFormatted + T(" загрузок", " downloads");
    public ImageSource ProjectIcon { get; private set; } = ImageSource.Empty;
    public bool ProjectLoading { get; private set; }
    public bool ProjectFailed { get; private set; }
    public string ProjectStatus { get; private set; } = "";
    public bool HasProjectVersions => !ProjectLoading && projectVersions.Count > 0;
    public string ProjectVersionLabel => ProjectLoading ? T("Загружаю файлы…", "Loading files…") : selectedVersion == null ? T("Нет подходящего файла", "No compatible file") : selectedVersion.VersionNumber + " · " + string.Join(", ", selectedVersion.Loaders);
    public string ProjectTarget => projectType == "modpack" ? T("Будет создана отдельная сборка", "Creates a new instance") + (projectMinecraft == null ? "" : " · Minecraft " + projectMinecraft)
        : SelectedInstance == null ? T("Сборка для установки не выбрана", "No installation target selected") : T("Установить в: ", "Install to: ") + InstanceName + " · " + InstanceLabel(SelectedInstance);
    public string ProjectGalleryLabel => T("Скриншоты", "Screenshots") + (projectInfo?.Gallery.Count > 0 ? $" ({projectInfo.Gallery.Count})" : "");
    public string ProjectTab { get; private set; } = "description";
    public string? World { get; private set; }
    public string WorldLabel => World ?? T("Выбрать мир", "Choose world");
    public bool NeedsWorld => (Page == "project" ? projectType : ContentType) == "datapack";
    private DownloadJob? installJob;
    private enum InstallRequirement { Ready, Loading, Retry, Queued, Permission, Instance, Loader, Busy, Version, World }
    private InstallRequirement RequiredForInstall => ProjectLoading ? InstallRequirement.Loading : ProjectFailed ? InstallRequirement.Retry
        : installJob?.State is DownloadState.Queued or DownloadState.Running ? InstallRequirement.Queued
        : Sessions.Events.Active?.Ui?.AllowModInstall == false || projectType == "modpack" && !AllowCreate ? InstallRequirement.Permission
        : projectType != "modpack" && SelectedInstance == null ? InstallRequirement.Instance
        : projectType == "mod" && SelectedInstance?.Loader == LoaderType.None ? InstallRequirement.Loader
        : projectType != "modpack" && SelectedInstance != null && Sessions.IsBusy(SelectedInstance.Id) ? InstallRequirement.Busy
        : selectedVersion == null ? InstallRequirement.Version
        : NeedsWorld && World == null ? InstallRequirement.World : InstallRequirement.Ready;
    public bool CanInstall => RequiredForInstall == InstallRequirement.Ready;
    public string InstallHint => RequiredForInstall switch
    {
        InstallRequirement.Loading => T("Проверяю доступные файлы для установки…", "Checking available files…"),
        InstallRequirement.Retry => T("Не удалось загрузить файлы проекта. Проверь подключение и повтори попытку.", "Could not load the project's files. Check your connection and try again."),
        InstallRequirement.Queued => T("Установка добавлена в очередь. Прогресс и отмена — в загрузках.", "Installation is queued. View progress or cancel it in Downloads."),
        InstallRequirement.Permission => T("Установка отключена настройками события.", "Installation is disabled by the event configuration."),
        InstallRequirement.Instance => T("Выбери сборку, чтобы проверить версию Minecraft и установить дополнение.", "Select an instance to check its Minecraft version and install this extra."),
        InstallRequirement.Loader => T("Для модов нужен Fabric, Quilt, Forge или NeoForge. В выбранной сборке — Vanilla.", "Mods need Fabric, Quilt, Forge or NeoForge. The selected instance uses Vanilla."),
        InstallRequirement.Busy => T("Сборка занята. Заверши игру или дождись окончания её загрузок.", "This instance is busy. Close the game or wait for its downloads to finish."),
        InstallRequirement.Version => T("У проекта нет подходящего файла для выбранной версии Minecraft и загрузчика.", "This project has no compatible file for the selected Minecraft version and loader."),
        InstallRequirement.World => T("Выбери мир, в который нужно установить датапак.", "Choose the world where this datapack should be installed."),
        _ => ""
    };
    public string InstallHelpLabel => RequiredForInstall switch
    {
        InstallRequirement.Retry => T("Повторить", "Retry"),
        InstallRequirement.Queued => T("К загрузкам", "Downloads"),
        InstallRequirement.Instance or InstallRequirement.Loader => T("Выбрать сборку", "Select instance"),
        InstallRequirement.Version => projectType == "modpack" ? T("В каталог", "Discover") : T("Сменить сборку", "Change instance"),
        InstallRequirement.Busy => T("На главную", "Home"),
        InstallRequirement.World => T("Выбрать мир", "Choose world"),
        _ => ""
    };
    public string InstallLabel => installJob?.State is DownloadState.Queued or DownloadState.Running ? T("В очереди загрузок", "In download queue") : T("Установить", "Install");
    public void ResolveInstallRequirement()
    {
        switch (RequiredForInstall)
        {
            case InstallRequirement.Retry: LoadProject(); break;
            case InstallRequirement.Queued: Downloads(); break;
            case InstallRequirement.Instance or InstallRequirement.Loader: SelectInstance(); break;
            case InstallRequirement.Version: if (projectType == "modpack") Catalog(); else SelectInstance(); break;
            case InstallRequirement.Busy: Home(); break;
            case InstallRequirement.World: ChooseWorld(); break;
        }
    }

    public void SetContentType(string type)
    {
        ContentType = type; category = null; World = null;
        if (type == "modpack") InstalledOnly = false;
        Search(); Changed();
    }
    public void ShowCatalog() { InstalledOnly = false; Search(); }
    public void ShowInstalled() { InstalledOnly = true; Search(); }
    public void Search() { searchDelay?.Cancel(); Run(() => SearchAsync(false)); }
    public void LoadMoreCatalog() => Run(() => SearchAsync(true));
    public void RetryCatalog() => Run(() => SearchAsync(CatalogItems.Count > 0));
    public void ClearCatalogFilters() { query = ""; category = null; Search(); Changed(); }
    public void ResolveCatalogEmpty()
    {
        if (CatalogLoading) return;
        if (CatalogFailed) RetryCatalog();
        else if (InstalledOnly && SelectedInstance == null) Library();
        else if (InstalledOnly && NeedsWorld && World == null) ChooseWorld();
        else if (HasCatalogFilters) ClearCatalogFilters();
        else if (InstalledOnly) ShowCatalog();
        else SelectInstance();
    }
    private void ResumeCatalog()
    {
        if (InstalledOnly || catalogSelection != CurrentCatalogSelection || CatalogLoading) { Search(); return; }
        foreach (var row in CatalogItems.Where(row => !row.HasImage))
            if (catalogIcons.TryGetValue(row.Id, out var url)) LoadRemoteImage(row, url, PageToken);
    }
    private void LeaveCatalog()
    {
        ++catalogGeneration; catalogCancellation?.Cancel(); CatalogLoading = false;
        if (catalogSelection != null) CatalogStatus = catalogResultStatus;
    }
    public async Task SearchAsync(bool append = false)
    {
        if (Page != "catalog") return;
        searchDelay?.Cancel();
        if (append && catalogSelection != CurrentCatalogSelection) append = false;
        if (InstalledOnly) { LoadInstalled(); return; }
        if (append && (CatalogLoading || !MoreCatalog && !CatalogFailed)) return;
        catalogCancellation?.Cancel(); catalogCancellation?.Dispose();
        catalogCancellation = CancellationTokenSource.CreateLinkedTokenSource(PageToken);
        var token = catalogCancellation.Token; int generation = ++catalogGeneration;
        var selection = CurrentCatalogSelection;
        string previousStatus = CatalogStatus;
        if (!append) { catalogSelection = null; catalogOffset = 0; CatalogItems.Clear(); catalogIcons.Clear(); MoreCatalog = false; }
        CatalogLoading = true; CatalogFailed = false;
        CatalogStatus = append ? T("Загружаю ещё…", "Loading more…") : T("Ищу дополнения…", "Searching…"); Changed();
        string type = ContentType;
        string? minecraft = CompatibleOnly ? SelectedInstance?.McVersion : null;
        string? loader = type == "mod" && minecraft != null && SelectedInstance?.Loader != LoaderType.None ? SelectedInstance?.Loader.ToString().ToLowerInvariant() : null;
        try
        {
            var result = await CatalogClient.SearchAsync(Query.Trim(), minecraft, loader, type, category, catalogOffset, 20, sortBy, token);
            token.ThrowIfCancellationRequested();
            if (generation != catalogGeneration) return;
            foreach (var hit in result.Hits)
            {
                if (CatalogItems.Any(item => item.Id == hit.ProjectId)) continue;
                var row = new ItemModel
                {
                    Id = hit.ProjectId, Title = hit.Title, Description = hit.Description,
                    Meta = hit.Author + " · " + hit.DownloadsFormatted + T(" загрузок", " downloads"),
                    Primary = T("Подробнее", "View project"), Action = () => OpenProject(hit, type, minecraft)
                };
                CatalogItems.Add(row); catalogIcons[row.Id] = hit.IconUrl; LoadRemoteImage(row, hit.IconUrl, token);
            }
            catalogOffset += result.Hits.Count; MoreCatalog = catalogOffset < result.TotalHits;
            catalogSelection = selection;
            CatalogStatus = T($"{result.TotalHits:N0} результатов", $"{result.TotalHits:N0} results") + " · " + (minecraft == null ? T("Все версии Minecraft", "All Minecraft versions") : "Minecraft " + minecraft);
            catalogResultStatus = CatalogStatus;
        }
        catch (Exception) when (token.IsCancellationRequested) { }
        catch (Exception)
        {
            if (generation == catalogGeneration) { CatalogFailed = true; CatalogStatus = append ? previousStatus : ""; }
        }
        finally
        {
            if (generation == catalogGeneration)
            {
                if (token.IsCancellationRequested && append) CatalogStatus = previousStatus;
                CatalogLoading = false; Changed();
            }
        }
    }
    private void LoadRemoteImage(ItemModel row, string? url, CancellationToken token) => Run(async () =>
    {
        var image = await MediaCache.LoadRemote(url, token);
        if (token.IsCancellationRequested || disposed) return;
        row.Image = image; row.Changed();
    });
    public void ChooseSort() => ShowChoices(T("Сортировка", "Sort"), new[] { ("relevance", T("Релевантность", "Relevance")), ("downloads", T("Загрузки", "Downloads")), ("newest", T("Новые", "Newest")), ("updated", T("Обновлённые", "Updated")) }.Select(entry => new ItemModel
    {
        Title = entry.Item2, Action = () => { sortBy = entry.Item1; CloseDialog(); Search(); }
    }));
    public void ChooseCategory()
    {
        string[] categories = ContentType switch
        {
            "mod" => ["adventure", "decoration", "equipment", "food", "library", "magic", "management", "optimization", "storage", "technology", "transportation", "utility", "worldgen"],
            "modpack" => ["adventure", "challenging", "combat", "kitchen-sink", "lightweight", "magic", "multiplayer", "optimization", "quests", "technology"],
            "shader" => ["cartoon", "fantasy", "realistic", "semi-realistic", "vanilla-like"],
            _ => ["16x", "32x", "64x", "128x", "256x", "realistic", "simplistic", "themed"]
        };
        ShowChoices(T("Категория", "Category"), new[] { "" }.Concat(categories).Select(item => new ItemModel { Title = item.Length == 0 ? T("Все категории", "All categories") : item, Action = () => { category = item.Length == 0 ? null : item; CloseDialog(); Search(); } }));
    }
    public void ChooseWorld()
    {
        if (SelectedInstance == null) { SelectInstance(); return; }
        string saves = Path.Combine(Instances.GetGameDir(SelectedInstance.Id), "saves");
        var worlds = Directory.Exists(saves) ? Directory.GetDirectories(saves).Where(path => File.Exists(Path.Combine(path, "level.dat"))) : [];
        ShowChoices(T("Мир для датапака", "Datapack world"), worlds.Select(path => new ItemModel { Id = path, Title = Path.GetFileName(path), Action = () => { World = Path.GetFileName(path); CloseDialog(); if (InstalledOnly) LoadInstalled(); Changed(); } }));
        DialogBody = T("Если список пуст, сначала создай мир в Minecraft.", "If the list is empty, create a world in Minecraft first."); Changed();
    }
    private void LoadInstalled()
    {
        catalogCancellation?.Cancel(); ++catalogGeneration; catalogSelection = null;
        CatalogLoading = false; CatalogFailed = false; MoreCatalog = false; CatalogItems.Clear(); catalogIcons.Clear();
        if (SelectedInstance is not { } instance) { CatalogStatus = T("Сначала выбери сборку", "Select an instance first"); Changed(); return; }
        if (ContentType == "modpack") { InstalledOnly = false; Search(); return; }
        if (NeedsWorld && World == null) { CatalogStatus = T("Выбери мир для датапаков", "Choose a world for datapacks"); Changed(); return; }
        var directory = ModInstaller.GetContentDirectory(Instances.GetGameDir(instance.Id), ContentType, World);
        foreach (var mod in ModInstaller.GetInstalledMods(directory, ContentType == "mod" ? ".jar" : ".zip").Where(m => m.FileName.Contains(Query, StringComparison.OrdinalIgnoreCase)))
        {
            CatalogItems.Add(new()
            {
                Id = mod.FilePath, Title = mod.FileName, Meta = mod.SizeFormatted, Description = mod.Enabled ? T("Включено", "Enabled") : T("Отключено", "Disabled"),
                Primary = mod.Enabled ? T("Отключить", "Disable") : T("Включить", "Enable"), Secondary = T("Удалить", "Remove"), Enabled = AllowModToggle,
                Action = () => Run(() => { RequireEventPermission(AllowModToggle); if (Sessions.IsBusy(instance.Id)) throw new InvalidOperationException(L("feature.busy")); ModInstaller.ToggleMod(mod.FilePath); LoadInstalled(); return Task.CompletedTask; }),
                Action2 = () => Run(async () => { RequireEventPermission(AllowModToggle); if (await Confirm(T("Удалить файл?", "Delete file?"), mod.FileName, T("Удалить", "Delete"), destructive: true)) { RequireEventPermission(AllowModToggle); if (Sessions.IsBusy(instance.Id)) throw new InvalidOperationException(L("feature.busy")); ModInstaller.RemoveMod(mod.FilePath); LoadInstalled(); } })
            });
        }
        CatalogStatus = T($"Установлено: {CatalogItems.Count}", $"Installed: {CatalogItems.Count}"); Changed();
    }
    public void CatalogFolder() { if (SelectedInstance != null && (!NeedsWorld || World != null)) Platform.OpenPath(ModInstaller.GetContentDirectory(Instances.GetGameDir(SelectedInstance.Id), ContentType, World)); }
    public void OpenProject(ModrinthProject hit, string type, string? minecraft)
    {
        project = hit; projectInfo = null; projectType = type; projectMinecraft = minecraft; selectedVersion = null;
        ProjectTab = "description"; ProjectIcon = ImageSource.Empty; installJob = null; DescriptionItems.Clear();
        Navigate("project"); LoadProject();
    }
    public void LoadProject() => Run(async () =>
    {
        if (project == null) return;
        catalogCancellation?.Cancel(); catalogCancellation?.Dispose();
        catalogCancellation = CancellationTokenSource.CreateLinkedTokenSource(PageToken);
        var token = catalogCancellation.Token; string id = project.ProjectId;
        selectedVersion = null; projectVersions.Clear();
        ProjectLoading = true; ProjectFailed = false; ProjectStatus = ""; Changed();
        try
        {
            string? mc = projectType == "modpack" ? projectMinecraft : SelectedInstance?.McVersion;
            string? loader = projectType == "mod" && SelectedInstance?.Loader != LoaderType.None ? SelectedInstance?.Loader.ToString().ToLowerInvariant() : projectType == "datapack" ? "datapack" : null;
            var infoTask = CatalogClient.GetProjectAsync(id, token);
            var versionsTask = CatalogClient.GetProjectVersionsAsync(id, mc, loader, token);
            await Task.WhenAll(infoTask, versionsTask); token.ThrowIfCancellationRequested();
            projectInfo = await infoTask;
            string extension = projectType == "modpack" ? ".mrpack" : projectType == "mod" ? ".jar" : ".zip";
            projectVersions.Clear();
            projectVersions.AddRange((await versionsTask).Where(v => (mc == null || v.GameVersions.Contains(mc)) && (loader == null || v.Loaders.Contains(loader, StringComparer.OrdinalIgnoreCase)) && v.Files.Any(f => f.Filename.EndsWith(extension, StringComparison.OrdinalIgnoreCase))));
            selectedVersion = projectVersions.FirstOrDefault(v => v.VersionType == "release") ?? projectVersions.FirstOrDefault();
            ProjectStatus = selectedVersion == null ? "" : T($"Доступно файлов: {projectVersions.Count}", $"Available releases: {projectVersions.Count}");
            if (projectType == "shader") ProjectStatus += T(" · Для шейдеров нужен Iris или OptiFine.", " · Shaders require Iris or OptiFine.");
            ShowProjectDescription();
            var icon = await MediaCache.LoadRemote(project.IconUrl, token); token.ThrowIfCancellationRequested(); ProjectIcon = icon;
        }
        catch (Exception) when (token.IsCancellationRequested) { }
        catch (Exception) { ProjectFailed = true; }
        finally { if (!token.IsCancellationRequested) { ProjectLoading = false; Changed(); } }
    });
    public void ChooseProjectVersion() => ShowChoices(T("Файл для установки", "Release to install"), projectVersions.Select(version => new ItemModel
    {
        Id = version.Id, Title = version.Name, Meta = string.Join(", ", version.GameVersions) + " · " + string.Join(", ", version.Loaders) + " · " + version.VersionType,
        Action = () => { selectedVersion = version; CloseDialog(); Changed(); }
    }));
    public void ShowProjectDescription()
    {
        ProjectTab = "description"; DescriptionItems.Clear();
        foreach (var block in MarkdownReader.Read(projectInfo?.Body ?? ProjectDescription, Platform.OpenUrl))
        {
            DescriptionItems.Add(block.Item);
            if (block.ImageUrl != null) LoadRemoteImage(block.Item, block.ImageUrl, PageToken);
        }
        if (DescriptionItems.Count == 0) DescriptionItems.Add(new() { Description = ProjectDescription });
        Changed();
    }
    public void ShowProjectGallery()
    {
        ProjectTab = "gallery"; DescriptionItems.Clear();
        foreach (var entry in (projectInfo?.Gallery ?? []).OrderByDescending(g => g.Featured).ThenBy(g => g.Ordering))
        {
            var row = new ItemModel { Title = entry.Title ?? "", Description = entry.Description ?? "", Primary = T("Открыть оригинал", "Open original"), Action = () => Platform.OpenUrl(entry.Url) };
            DescriptionItems.Add(row); LoadRemoteImage(row, entry.Url, PageToken);
        }
        if (DescriptionItems.Count == 0) DescriptionItems.Add(new() { Description = T("Автор не добавил скриншоты.", "No screenshots provided by the author.") });
        Changed();
    }
    public void ProjectWebsite() { if (project != null) Platform.OpenUrl("https://modrinth.com/" + projectType + "/" + Uri.EscapeDataString(project.Slug)); }
    public void InstallProject()
    {
        if (!CanInstall || selectedVersion is not { } version) return;
        var target = SelectedInstance; string type = projectType; string? world = World; bool pack = type == "modpack";
        var alternatives = projectVersions.SelectMany(v => v.Files).Select(f => f.Filename).Where(n => Path.GetFileName(n) == n).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        installJob = Sessions.Enqueue(ProjectTitle + " · " + version.VersionNumber, pack ? null : target!.Id, async token =>
        {
            if (Sessions.Events.Active?.Ui?.AllowModInstall == false) throw new InvalidOperationException(L("catalog.install_locked"));
            var installer = new ModInstaller();
            if (pack) { RequireEventPermission(AllowCreate); var created = await installer.ImportModpackAsync(version, Instances, token); DownloadQueue.Current!.ResultInstanceId = created.Id; }
            else
            {
                var current = Instances.GetInstance(target!.Id);
                if (current == null || current.McVersion != target.McVersion || current.Loader != target.Loader) throw new InvalidOperationException(L("catalog.target_changed"));
                var directory = ModInstaller.GetContentDirectory(Instances.GetGameDir(current.Id), type, world);
                var filename = ModInstaller.SelectFile(version, type == "mod" ? ".jar" : ".zip").Filename;
                if (alternatives.Any(n => !n.Equals(filename, StringComparison.OrdinalIgnoreCase) && (File.Exists(Path.Combine(directory, n)) || File.Exists(Path.Combine(directory, n + ".disabled")))) || File.Exists(Path.Combine(directory, filename + ".disabled"))) throw new InvalidOperationException(L("catalog.existing_version"));
                await installer.InstallContentAsync(version, Instances.GetGameDir(current.Id), type, current.McVersion,
                    current.Loader == LoaderType.None ? null : current.Loader.ToString().ToLowerInvariant(), world, token);
            }
        });
        Notice(T("Добавлено в центр загрузок", "Added to downloads")); Changed();
    }
}
