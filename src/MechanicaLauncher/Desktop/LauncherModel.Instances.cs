using MechanicaLauncher.Core.Game;
using MechanicaLauncher.Core.Mods;

namespace MechanicaLauncher.Desktop;

public sealed partial class LauncherModel
{
    public ObservableCollection<ItemModel> LibraryItems { get; } = [];
    private string libraryQuery = "";
    public string LibraryQuery { get => libraryQuery; set { if (Set(ref libraryQuery, value)) RefreshLibrary(); } }
    private string libraryLoader = "";
    private bool recentFirst = true;
    private int libraryTotal;
    public string LibrarySort => recentFirst ? T("Сначала недавние", "Recently played") : T("По имени", "Name");
    public string LibraryFilter => libraryLoader.Length == 0 ? T("Все загрузчики", "All loaders") : libraryLoader;
    public bool HasLibraryFilters => !string.IsNullOrWhiteSpace(LibraryQuery) || libraryLoader.Length > 0;
    public string LibraryCount => HasLibraryFilters ? T($"{LibraryItems.Count} из {libraryTotal}", $"{LibraryItems.Count} of {libraryTotal}") : T($"Сборок: {LibraryItems.Count}", $"Instances: {LibraryItems.Count}");
    public string LibraryEmptyTitle => libraryTotal == 0 ? T("Здесь будут твои сборки", "Your instances will be here") : T("Сборки не найдены", "No matching instances");
    public string LibraryEmptyHint => libraryTotal == 0
        ? T("Создай сборку с нужной версией Minecraft или импортируй готовый .mrpack.", "Create an instance with your Minecraft version or import a .mrpack.")
        : T("Попробуй другое название или сбрось фильтр загрузчика.", "Try another name or clear the loader filter.");
    public bool CompactInstances { get => Settings.CompactInstances; set { Settings.CompactInstances = value; Settings.Save(); RefreshLibrary(); } }
    public GameInstance? EditingInstance { get; private set; }
    public string InstanceTab { get; private set; } = "settings";
    public string EditName { get; set; } = "";
    public string EditMaxMemory { get; set; } = "4096";
    public string EditMinMemory { get; set; } = "2048";
    public string EditWidth { get; set; } = "1920";
    public string EditHeight { get; set; } = "1080";
    public string EditJava { get; set; } = "";
    public string EditJvm { get; set; } = "";
    private bool autoJava = true;
    public bool AutoJava { get => autoJava; set { autoJava = value; Changed(); } }
    public bool Advanced { get; set; }
    public string EditAccent { get; set; } = "";
    private string? coverDraft, iconDraft;
    public ImageSource CoverPreview { get; private set; } = ImageSource.Empty;
    public ImageSource IconPreview { get; private set; } = ImageSource.Empty;
    public ObservableCollection<ItemModel> DetailItems { get; } = [];
    public string DetailStatus { get; private set; } = "";
    public string NewName { get; set; } = "";
    public string NewVersion { get; private set; } = "";
    public string NewLoader { get; private set; } = "Vanilla";
    public string NewVersionLabel => NewVersion.Length == 0 ? T("Выбрать версию Minecraft", "Choose Minecraft version") : NewVersion;
    public string CreateStatus { get; private set; } = "";
    private IReadOnlyList<string> minecraftVersions = [];
    private int screenshotPage;
    private IReadOnlyList<GameScreenshot> screenshots = [];

    public void RefreshInstances()
    {
        var all = Instances.GetAllInstances();
        SelectedInstance = all.FirstOrDefault(i => i.Id == Settings.SelectedInstanceId) ?? all.FirstOrDefault();
        HeroImage = MediaCache.LoadLocal(SelectedInstance == null ? null : Instances.GetCoverAbsolutePath(SelectedInstance));
        if (Page == "instances") RefreshLibrary();
        Changed();
    }
    public void RefreshLibrary()
    {
        var instances = Instances.GetAllInstances();
        libraryTotal = instances.Count;
        var all = instances.Where(i => i.Name.Contains(LibraryQuery.Trim(), StringComparison.OrdinalIgnoreCase) &&
            (libraryLoader.Length == 0 || (i.Loader == LoaderType.None ? "Vanilla" : i.Loader.ToString()) == libraryLoader));
        all = recentFirst ? all.OrderByDescending(i => i.LastPlayed).ThenBy(i => i.Name) : all.OrderBy(i => i.Name);
        LibraryItems.Clear();
        foreach (var instance in all)
            LibraryItems.Add(new()
            {
                Id = instance.Id, Title = instance.Name, Meta = InstanceLabel(instance), Compact = CompactInstances,
                Description = Sessions.IsRunning(instance.Id) ? T("Сейчас в игре", "Running") : instance.LastPlayed is { } date ? T("Последний запуск: ", "Last played: ") + date.ToLocalTime().ToString("g") : T("Ещё не запускалась", "Not played yet"),
                Image = MediaCache.LoadLocal(Instances.GetIconAbsolutePath(instance)),
                Primary = T("Играть", "Play"), Secondary = T("Настроить", "Settings"), Tertiary = T("Ещё", "More"),
                Action = () => { SetInstance(instance.Id); Home(); Play(); },
                Action2 = () => EditInstance(instance.Id), Action3 = () => InstanceActions(instance.Id)
            });
        Changed();
    }
    public void SortLibrary() { recentFirst = !recentFirst; RefreshLibrary(); }
    public void ClearLibraryFilters() { libraryQuery = ""; libraryLoader = ""; RefreshLibrary(); }
    public void FilterLibrary() => ShowChoices(T("Загрузчик", "Loader"), new[] { "", "Vanilla", "Fabric", "Quilt", "Forge", "NeoForge" }.Select(loader => new ItemModel
    {
        Title = loader.Length == 0 ? T("Все загрузчики", "All loaders") : loader,
        Action = () => { libraryLoader = loader; CloseDialog(); RefreshLibrary(); }
    }));
    public void NewInstance()
    {
        if (!AllowCreate) return;
        NewName = ""; NewVersion = ""; NewLoader = "Vanilla"; CreateStatus = "";
        Navigate("create"); LoadMinecraftVersions();
    }
    public void LoadMinecraftVersions() => Run(async () =>
    {
        var token = PageToken;
        CreateStatus = T("Загружаю версии…", "Loading versions…"); Changed();
        try
        {
            var manifest = await new VersionManager(Instances.SharedDir).GetManifestAsync(token);
            token.ThrowIfCancellationRequested();
            minecraftVersions = manifest.Versions.Where(v => v.Type == "release" || Settings.ShowSnapshots && v.Type == "snapshot").Select(v => v.Id).ToArray();
            NewVersion = minecraftVersions.FirstOrDefault() ?? "";
            CreateStatus = NewVersion.Length == 0 ? T("Нет доступных версий", "No versions available") : "";
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { CreateStatus = ex.Message; }
        Changed();
    });
    public void ChooseMinecraft() => ShowChoices(T("Версия Minecraft", "Minecraft version"), minecraftVersions.Select(version => new ItemModel
    {
        Id = version, Title = version, Action = () => { NewVersion = version; CloseDialog(); Changed(); }
    }));
    public void ChooseLoader() => ShowChoices(T("Загрузчик модов", "Mod loader"), new[] { "Vanilla", "Fabric", "Quilt", "Forge", "NeoForge" }.Select(loader => new ItemModel
    {
        Title = loader, Action = () => { NewLoader = loader; CloseDialog(); Changed(); }
    }));
    public void CreateInstance() => Run(() => WithBusy(async () =>
    {
        if (!AllowCreate) throw new InvalidOperationException(L("catalog.install_locked"));
        var token = PageToken;
        if (string.IsNullOrWhiteSpace(NewName)) throw new InvalidDataException(T("Введи название сборки.", "Enter an instance name."));
        if (!minecraftVersions.Contains(NewVersion)) throw new InvalidDataException(T("Выбери версию Minecraft.", "Choose a Minecraft version."));
        string name = NewName.Trim(), version = NewVersion;
        var loader = NewLoader == "Vanilla" ? LoaderType.None : Enum.Parse<LoaderType>(NewLoader);
        string? loaderVersion = loader switch
        {
            LoaderType.Fabric => (await new FabricInstaller(Instances.SharedDir, "").GetLoaderVersionsAsync(version).WaitAsync(token)).OrderByDescending(v => v.Stable).FirstOrDefault()?.Version,
            LoaderType.Quilt => (await new QuiltInstaller(Instances.SharedDir, "").GetLoaderVersionsAsync(version).WaitAsync(token)).FirstOrDefault(),
            LoaderType.Forge => (await new ForgeInstaller(Instances.SharedDir, "").GetVersionsAsync(version).WaitAsync(token)).FirstOrDefault(),
            LoaderType.NeoForge => (await new NeoForgeInstaller(Instances.SharedDir, "").GetVersionsAsync(version).WaitAsync(token)).FirstOrDefault(),
            _ => null
        };
        token.ThrowIfCancellationRequested();
        if (loader != LoaderType.None && string.IsNullOrWhiteSpace(loaderVersion)) throw new InvalidOperationException(T($"{loader} недоступен для Minecraft {version}.", $"{loader} is not available for Minecraft {version}."));
        var instance = Instances.CreateInstance(name, version, loader, loaderVersion);
        SetInstance(instance.Id); Home(); Notice(T("Сборка создана. Игра и Java скачаются при первом запуске.", "Instance created. Minecraft and Java will download on first launch."));
    }));
    public void EditInstance(string id)
    {
        EditingInstance = Instances.GetInstance(id) ?? throw new InvalidOperationException(L("catalog.target_changed"));
        var i = EditingInstance;
        EditName = i.Name; EditMaxMemory = i.MaxMemoryMb.ToString(); EditMinMemory = i.MinMemoryMb.ToString();
        EditWidth = i.WindowWidth.ToString(); EditHeight = i.WindowHeight.ToString(); EditJava = i.JavaPath ?? ""; AutoJava = string.IsNullOrEmpty(i.JavaPath); EditJvm = i.JvmArgs;
        EditAccent = i.AccentColor ?? ""; coverDraft = Instances.GetCoverAbsolutePath(i); iconDraft = Instances.GetIconAbsolutePath(i);
        CoverPreview = MediaCache.LoadLocal(coverDraft); IconPreview = MediaCache.LoadLocal(iconDraft);
        Advanced = false; InstanceTab = "settings"; DetailItems.Clear(); Navigate("instance");
    }
    public void SelectInstanceTab(string tab)
    {
        InstanceTab = tab; DetailItems.Clear(); DetailStatus = "";
        if (tab == "screenshots") LoadScreenshots();
        if (tab == "crashes") LoadCrashes();
        if (tab == "compatibility") CheckCompatibility();
        Changed();
    }
    public void ToggleAdvanced() { Advanced = !Advanced; Changed(); }
    private GameInstance RequireEditable()
    {
        if (EditingInstance == null) throw new InvalidOperationException(L("catalog.target_changed"));
        if (Sessions.IsBusy(EditingInstance.Id)) throw new InvalidOperationException(L("feature.busy"));
        return Instances.GetInstance(EditingInstance.Id) ?? throw new InvalidOperationException(L("catalog.target_changed"));
    }
    internal static int PositiveInteger(string text, string label)
    {
        if (!int.TryParse(text, out int value) || value <= 0) throw new InvalidDataException(label);
        return value;
    }
    public void SaveInstanceSettings() => Run(() =>
    {
        var i = RequireEditable();
        if (string.IsNullOrWhiteSpace(EditName)) throw new InvalidDataException(L("instance.name_required"));
        int max = PositiveInteger(EditMaxMemory, L("instance.memory_invalid"));
        int min = PositiveInteger(EditMinMemory, L("instance.memory_invalid"));
        int width = PositiveInteger(EditWidth, L("instance.window_invalid"));
        int height = PositiveInteger(EditHeight, L("instance.window_invalid"));
        if (min > max) throw new InvalidDataException(T("Минимум памяти не может быть больше максимума.", "Minimum memory cannot exceed maximum."));
        if (!AutoJava && (!File.Exists(EditJava.Trim()) || !new[] { "java.exe", "javaw.exe" }.Contains(Path.GetFileName(EditJava.Trim()), StringComparer.OrdinalIgnoreCase))) throw new InvalidDataException(T("Выбери существующий java.exe или javaw.exe.", "Choose an existing java.exe or javaw.exe."));
        i.Name = EditName.Trim(); i.MaxMemoryMb = max; i.MinMemoryMb = min; i.WindowWidth = width; i.WindowHeight = height;
        i.JavaPath = AutoJava ? null : Path.GetFullPath(EditJava.Trim()); i.JvmArgs = EditJvm;
        Instances.SaveInstance(i); EditingInstance = i; Notice(T("Настройки сохранены", "Settings saved")); return Task.CompletedTask;
    });
    public void PickJava() { var file = Platform.PickFile("Java|java.exe;javaw.exe"); if (file != null) { EditJava = file; AutoJava = false; Changed(); } }
    public void InstanceMods() { if (EditingInstance != null) { SetInstance(EditingInstance.Id); Catalog(); } }
    public void InstanceFolder() { if (EditingInstance != null) Platform.OpenPath(Instances.GetGameDir(EditingInstance.Id)); }
    public void PickCover() => Run(() => { var file = Platform.PickFile("PNG, JPEG|*.png;*.jpg;*.jpeg"); if (file != null) { coverDraft = file; CoverPreview = MediaCache.LoadLocal(file); Changed(); } return Task.CompletedTask; });
    public void PickIcon() => Run(() => { var file = Platform.PickFile("PNG, JPEG|*.png;*.jpg;*.jpeg"); if (file != null) { iconDraft = file; IconPreview = MediaCache.LoadLocal(file); Changed(); } return Task.CompletedTask; });
    public void ClearCover() { coverDraft = null; CoverPreview = ImageSource.Empty; Changed(); }
    public void ClearIcon() { iconDraft = null; IconPreview = ImageSource.Empty; Changed(); }
    public void SaveAppearance() => Run(() =>
    {
        var i = RequireEditable();
        if (EditAccent.Length != 0 && !InstanceMedia.IsAccent(EditAccent)) throw new InvalidDataException(T("Цвет должен быть в формате #RRGGBB.", "Use #RRGGBB for the accent color."));
        if (iconDraft == null) i.IconPath = null;
        else if (!string.Equals(Instances.GetIconAbsolutePath(i), iconDraft, StringComparison.OrdinalIgnoreCase)) Instances.SetIconFromFile(i, iconDraft, save: false);
        Instances.SaveAppearance(i, coverDraft, EditAccent.Length == 0 ? null : EditAccent);
        EditingInstance = i; Notice(T("Оформление сохранено", "Appearance saved")); return Task.CompletedTask;
    });
    public void InstanceActions(string id)
    {
        var instance = Instances.GetInstance(id);
        if (instance == null) return;
        ShowChoices(instance.Name, new ItemModel[]
        {
            new() { Title = T("Открыть папку", "Open folder"), Action = () => { CloseDialog(); Platform.OpenPath(Instances.GetGameDir(id)); } },
            new() { Title = T("Дублировать", "Duplicate"), Enabled = AllowCreate, Action = () => { CloseDialog(); Run(() => { RequireEventPermission(AllowCreate); if (Sessions.IsBusy(id)) throw new InvalidOperationException(L("feature.busy")); Sessions.Enqueue(T("Дублирование сборки", "Duplicate instance"), id, token => { RequireEventPermission(AllowCreate); token.ThrowIfCancellationRequested(); Instances.DuplicateInstance(id); return Task.CompletedTask; }); Downloads(); return Task.CompletedTask; }); } },
            new() { Title = T("Экспорт .mrpack", "Export .mrpack"), Action = () => { CloseDialog(); Run(() => { if (Sessions.IsBusy(id)) throw new InvalidOperationException(L("feature.busy")); var path = Platform.SaveFile("Modrinth pack|*.mrpack", instance.Name + ".mrpack"); if (path != null) { Sessions.Enqueue(T("Экспорт сборки", "Export instance"), id, async token => { token.ThrowIfCancellationRequested(); await ModpackInstaller.ExportAsync(instance, Instances, path); }); Downloads(); } return Task.CompletedTask; }); } },
            new() { Title = T("Удалить сборку…", "Delete instance…"), Enabled = AllowDelete, Action = () => { CloseDialog(); Run(async () => { RequireEventPermission(AllowDelete); if (await Confirm(T("Удалить сборку?", "Delete instance?"), instance.Name + "\n" + T("Миры, моды и скриншоты будут удалены безвозвратно.", "Worlds, mods and screenshots will be permanently deleted."), T("Удалить", "Delete"), destructive: true)) { RequireEventPermission(AllowDelete); if (Sessions.IsBusy(id)) throw new InvalidOperationException(L("feature.busy")); Instances.DeleteInstance(id); RefreshInstances(); } }); } }
        });
    }
    public void ImportPack() { if (!AllowCreate) return; var path = Platform.PickFile("Modrinth pack|*.mrpack"); if (path != null) ImportPack(path); }
    public void ImportPack(string path)
    {
        if (!AllowCreate) return;
        Sessions.Enqueue(T("Импорт модпака", "Import modpack"), null, async token =>
        {
            RequireEventPermission(AllowCreate);
            var created = await new ModpackInstaller().ImportAsync(path, Instances, token);
            DownloadQueue.Current!.ResultInstanceId = created.Id;
        });
        Downloads();
    }
    public void CheckCompatibility() => Run(async () =>
    {
        if (EditingInstance is not { } i) return;
        string id = i.Id; var token = PageToken;
        DetailStatus = T("Проверяю моды…", "Checking mods…"); Changed();
        var report = await new ModCompatibilityChecker().CheckAsync(i, Instances.GetGameDir(id), true, token);
        if (Page != "instance" || EditingInstance?.Id != id || InstanceTab != "compatibility") return;
        DetailItems.Clear();
        foreach (var issue in report.Issues) DetailItems.Add(new() { Title = L("compat." + issue.Code), Description = issue.Detail });
        DetailStatus = report.Issues.Count == 0 ? L("compat.clean") : L("compat.local"); Changed();
    });
    public void Diagnose() => Run(async () =>
    {
        if (EditingInstance is not { } i) return;
        var token = PageToken;
        DetailStatus = T("Проверяю файлы и Java…", "Checking files and Java…"); Changed();
        var reports = await InstanceDiagnostics.RunAsync(i, Instances, new VersionManager(Instances.SharedDir), Settings.AccessToken);
        token.ThrowIfCancellationRequested(); DetailItems.Clear();
        foreach (var report in reports) DetailItems.Add(new()
        {
            Title = report.Title, Description = report.Detail, Primary = report.FixLabel ?? "",
            Action = report.Fix == null ? null : () => Run(async () => { if (Sessions.IsBusy(i.Id)) throw new InvalidOperationException(L("feature.busy")); await report.Fix(); Diagnose(); })
        });
        DetailStatus = T("Проверка завершена", "Check complete"); Changed();
    });
    public void LoadScreenshots()
    {
        if (EditingInstance == null) return;
        screenshots = InstanceMedia.GetScreenshots(Instances.GetGameDir(EditingInstance.Id));
        screenshotPage = 0; DetailItems.Clear(); MoreScreenshots();
    }
    public void MoreScreenshots()
    {
        foreach (var shot in screenshots.Skip(screenshotPage * 30).Take(30))
            DetailItems.Add(new() { Id = shot.Path, Title = shot.Name, Meta = shot.Time.ToString("g"), Image = MediaCache.LoadLocal(shot.Path), Primary = T("Открыть", "Open"), Secondary = T("Показать файл", "Show file"), Action = () => ShowImage(shot.Path, shot.Name), Action2 = () => Platform.RevealFile(shot.Path) });
        screenshotPage++; DetailStatus = T($"{DetailItems.Count} из {screenshots.Count} · F2 в игре", $"{DetailItems.Count} of {screenshots.Count} · F2 in game"); Changed();
    }
    public void LoadCrashes()
    {
        if (EditingInstance == null) return;
        DetailItems.Clear();
        foreach (var report in CrashAnalyzer.GetReports(Instances.GetGameDir(EditingInstance.Id)))
            DetailItems.Add(new()
            {
                Title = L("crash." + report.Reason), Meta = report.Time.ToLocalTime().ToString("g") + " · " + report.ExitCode,
                Description = L("crash." + report.Reason + ".help") + "\n" + report.Evidence, Primary = T("Показать лог", "Show log"), Secondary = T("Экспорт лога", "Export log"),
                Action = () => ShowText(L("crash." + report.Reason), report.LogTail),
                Action2 = () => { var path = Platform.SaveFile("Text|*.txt", "minecraft-crash.txt"); if (path != null) File.WriteAllText(path, report.LogTail); }
            });
        DetailStatus = DetailItems.Count == 0 ? T("Вылетов пока нет", "No crashes recorded") : T("Причины определены по журналу игры.", "Causes detected from the game log."); Changed();
    }
    public ImageSource DialogImage { get; private set; } = ImageSource.Empty;
    public string DialogText { get; private set; } = "";
    private string? dialogImagePath;
    public bool HasDialogImage => dialogImagePath != null;
    private void ShowImage(string path, string title) { BeginDialog(title, "", "", null); dialogImagePath = path; DialogImage = MediaCache.LoadLocal(path); Changed(); }
    public void OpenOriginalImage() { if (dialogImagePath != null) Process.Start(new ProcessStartInfo(dialogImagePath) { UseShellExecute = true }); }
    public void SaveImageCopy() => Run(() =>
    {
        if (dialogImagePath == null) return Task.CompletedTask;
        var extension = Path.GetExtension(dialogImagePath);
        var path = Platform.SaveFile($"Image|*{extension}", Path.GetFileName(dialogImagePath));
        if (path != null) File.Copy(dialogImagePath, path, overwrite: true);
        return Task.CompletedTask;
    });
    private void ShowText(string title, string text) { BeginDialog(title, "", "", null); DialogText = text; Changed(); }
}
