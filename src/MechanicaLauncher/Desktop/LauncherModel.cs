using MechanicaLauncher.Core.Mods;
using MechanicaLauncher.Core.Game;

namespace MechanicaLauncher.Desktop;

public sealed partial class LauncherModel : ObservableModel, IDisposable
{
    public LauncherSettings Settings { get; }
    public InstanceManager Instances { get; }
    public GameSessions Sessions { get; }
    internal ModrinthClient CatalogClient { get; }
    internal Action<Action> Dispatch { get; set; } = action => action();
    public Action? CloseRequested { get; set; }
    public Action? ExitRequested { get; set; }
    public Action? MinimizeRequested { get; set; }
    public Action? MaximizeRequested { get; set; }
    public event Action? Navigated;
    internal event Action? Navigating;
    internal event Action? DialogClosing;
    private CancellationTokenSource pageLifetime = new();
    private bool disposed;
    private string page = "home";
    public string Page => page;
    public string Title => page switch
    {
        "home" => T("Играть", "Play"),
        "instances" => T("Мои сборки", "My instances"),
        "catalog" => T("Каталог", "Discover"), "project" => ProjectTitle,
        "downloads" => T("Загрузки", "Downloads"), "servers" => T("Любимые серверы", "Favorite servers"),
        "account" => T("Аккаунт", "Account"), "settings" => T("Настройки", "Settings"),
        "instance" => EditingInstance?.Name ?? T("Настройки сборки", "Instance settings"),
        "create" => T("Новая сборка", "New instance"), "skin" => T("Редактор скина", "Skin editor"), _ => "Mechanica"
    };
    public string Subtitle => page switch
    {
        "home" => "",
        "instances" => T("Твои миры, моды и настройки — по отдельности.", "Your worlds, mods and settings, kept together."),
        "catalog" => T("Моды и дополнения с Modrinth.", "Mods and extras from Modrinth."),
        "downloads" => T("Прогресс, отмена и повтор в одном месте.", "Progress, cancellation and retries in one place."),
        "servers" => T("Выбери сервер и запускай нужную сборку.", "Pick a server and launch its instance."),
        "instance" => EditingInstance == null ? "" : InstanceLabel(EditingInstance),
        "settings" => T("Оформление, запуск игры и Discord.", "Appearance, game startup and Discord."),
        "account" => T("Профиль Minecraft и внешний вид персонажа.", "Your Minecraft profile and appearance."),
        _ => ""
    };
    public string InstanceName => SelectedInstance?.Name ?? T("Выбрать сборку", "Select instance");
    public string InstanceMeta => SelectedInstance == null ? T("Создай сборку или установи модпак", "Create an instance or install a modpack") : InstanceLabel(SelectedInstance);
    public GameInstance? SelectedInstance { get; private set; }
    public string Username => Settings.Username;
    public string AccountKind => Settings.AuthMode == "microsoft" ? "Microsoft" : T("Локальный профиль", "Local profile");
    public LauncherPalette Palette => Settings.Theme == "Light" ? LauncherPalette.Light : LauncherPalette.Dark;
    public string Message { get; private set; } = "";
    public bool Error { get; private set; }
    public bool Busy { get; private set; }
    public bool ShowInstancesPage => Sessions.Events.Active?.Ui?.ShowInstances != false;
    public bool ShowCatalogPage => Sessions.Events.Active?.Ui?.ShowMods != false;
    public bool ShowAccountPage => Sessions.Events.Active?.Ui?.ShowAccount != false;
    public bool ShowSettingsPage => Sessions.Events.Active?.Ui?.ShowSettings != false;
    public bool AllowCreate => Sessions.Events.Active?.Ui?.AllowInstanceCreate != false;
    public bool AllowDelete => Sessions.Events.Active?.Ui?.AllowInstanceDelete != false;
    public bool AllowModToggle => Sessions.Events.Active?.Ui?.AllowModToggle != false;
    public bool AllowLog => Sessions.Events.Active?.Ui?.ShowLogPanel != false;
    public bool DialogOpen { get; private set; }
    public string DialogTitle { get; private set; } = "";
    public string DialogBody { get; private set; } = "";
    public string DialogPrimary { get; private set; } = "";
    public bool DialogBusy { get; private set; }
    public bool DialogDanger { get; private set; }
    public string DialogError { get; private set; } = "";
    private string dialogQuery = "";
    private ItemModel[] dialogAllChoices = [];
    public bool DialogSearch { get; private set; }
    public string DialogQuery
    {
        get => dialogQuery;
        set
        {
            if (!Set(ref dialogQuery, value)) return;
            DialogChoices.Clear();
            foreach (var item in dialogAllChoices.Where(i => (i.Title + " " + i.Meta).Contains(value, StringComparison.OrdinalIgnoreCase))) DialogChoices.Add(item);
            Changed();
        }
    }
    public ObservableCollection<FieldModel> DialogFields { get; } = [];
    public ObservableCollection<ItemModel> DialogChoices { get; } = [];
    private Func<Task>? dialogAction;
    private TaskCompletionSource<bool>? dialogCompletion;
    private int dialogGeneration;
    internal int DialogGeneration => dialogGeneration;
    public ImageSource BrandImage { get; } = MediaCache.LoadLocal(Path.Combine(AppContext.BaseDirectory, "Assets", "Mechanica.png"));
    public ImageSource HeroImage { get; private set; } = ImageSource.Empty;
    public bool HasHeroImage => !ReferenceEquals(HeroImage, ImageSource.Empty);
    public Color HeroColor
    {
        get
        {
            if (!InstanceMedia.IsAccent(SelectedInstance?.AccentColor)) return Palette.Hero;
            var accent = Color.Hex(Convert.ToUInt32(SelectedInstance!.AccentColor![1..], 16));
            var background = Palette.Background;
            return new(background.R * .85f + accent.R * .15f, background.G * .85f + accent.G * .15f, background.B * .85f + accent.B * .15f);
        }
    }
    public string PlayLabel => Sessions.Preparing ? T("Отменить подготовку", "Cancel preparation") : SelectedInstance == null ? T("Создать сборку", "Create instance") : Sessions.IsRunning(SelectedInstance.Id) ? T("Завершить игру", "Stop game") : T("Играть", "Play");
    public string SessionStatus => Sessions.Status;
    public double LaunchProgress => Sessions.Progress;
    public bool LaunchPreparing => Sessions.Preparing;
    public string DownloadBadge => Sessions.Downloads.Jobs.Count(j => j.State is DownloadState.Queued or DownloadState.Running).ToString();
    public string GameLog => Sessions.Log;
    public bool ShowLog { get; set; }
    public string RuntimeStatus => TLauncherBlocked ? T("Проверка TLauncher", "TLauncher check") : Sessions.RunningCount > 0 ? T("Minecraft запущен", "Minecraft is running") : "";
    public CancellationToken PageToken => pageLifetime.Token;

    public LauncherModel(LauncherSettings settings, InstanceManager? instances = null, ModrinthClient? catalog = null)
    {
        Settings = settings;
        Instances = instances ?? new();
        CatalogClient = catalog ?? new();
        Locale.Init(settings.Language);
        Sessions = new(settings, Instances);
        Sessions.Changed += OnSessionChanged;
        InstancesChangedHandler = () => Dispatch(() => { if (!disposed) RefreshInstances(); });
        InstanceManager.InstancesChanged += InstancesChangedHandler;
        RefreshInstances();
    }
    private Action InstancesChangedHandler { get; }
    public string T(string ru, string en) => Locale.CurrentLanguage == "ru" ? ru : en;
    public string L(string key) => Locale.Get(key);
    public static string InstanceLabel(GameInstance instance) => $"Minecraft {instance.McVersion}  ·  {(instance.Loader == LoaderType.None ? "Vanilla" : instance.Loader + " " + instance.LoaderVersion)}";
    private int sessionRefreshPending;
    private void OnSessionChanged()
    {
        if (Interlocked.Exchange(ref sessionRefreshPending, 1) != 0) return;
        Dispatch(() =>
        {
            Interlocked.Exchange(ref sessionRefreshPending, 0);
            if (!disposed) { if (Page == "downloads") RefreshDownloads(); Changed(); }
        });
    }
    public void Notice(string message, bool error = false) { Message = CrashAnalyzer.Redact(message, [Settings.AccessToken, Settings.MsRefreshToken]); Error = error; Changed(); }
    public void DismissMessage() { Message = ""; Changed(); }
    public void Run(Func<Task> action) => _ = RunSafe(action);
    private async Task RunSafe(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!disposed) Dispatch(() => Notice(ex.Message, true)); }
    }
    public async Task WithBusy(Func<Task> action)
    {
        if (Busy) return;
        Busy = true; Changed();
        try { await action(); }
        finally { Busy = false; if (!disposed) Changed(); }
    }
    public void Navigate(string target)
    {
        if (DialogOpen || TLauncherBlocked) return;
        if ((target is "instances" or "instance" or "create" && !ShowInstancesPage)
            || (target is "catalog" or "project" && !ShowCatalogPage)
            || (target is "account" or "skin" && !ShowAccountPage)
            || (target == "settings" && !ShowSettingsPage)) return;
        if (page != target) Navigating?.Invoke();
        if (page == "catalog" && target != "catalog") LeaveCatalog();
        pageLifetime.Cancel(); pageLifetime.Dispose(); pageLifetime = new();
        page = target; Message = "";
        if (target == "instances") RefreshLibrary();
        if (target == "catalog") ResumeCatalog();
        if (target == "downloads") RefreshDownloads();
        if (target == "servers") RefreshServers();
        if (target == "account") LoadAccount();
        Changed(); Navigated?.Invoke();
    }
    public void Home() => Navigate("home");
    public void Library() => Navigate("instances");
    public void Catalog() => Navigate("catalog");
    public void BrowseModpacks() { SetContentType("modpack"); Catalog(); }
    public void Downloads() => Navigate("downloads");
    public void Servers() => Navigate("servers");
    public void Account() => Navigate("account");
    public void Preferences() => Navigate("settings");
    public void Minimize() => MinimizeRequested?.Invoke();
    public void Maximize() => MaximizeRequested?.Invoke();
    public void Close() => CloseRequested?.Invoke();
    public void Exit() => ExitRequested?.Invoke();
    public void ToggleLog() { ShowLog = AllowLog && !ShowLog; Changed(); }
    private void RequireEventPermission(bool allowed)
    {
        if (!allowed) throw new InvalidOperationException(T("Это действие отключено настройками события.", "This action is disabled by the event configuration."));
    }
    public void Play() => Run(async () =>
    {
        if (TLauncherBlocked) return;
        if (Sessions.Preparing) { Sessions.Cancel(); return; }
        if (SelectedInstance is not { } instance) { NewInstance(); return; }
        if (Sessions.IsRunning(instance.Id))
        {
            if (await Confirm(T("Завершить Minecraft?", "Stop Minecraft?"), T("Несохранённые изменения мира могут потеряться.", "Unsaved world changes may be lost."), T("Завершить", "Stop"))) Sessions.Stop(instance.Id);
            return;
        }
        await Launch(instance);
    });
    private Task Launch(GameInstance instance, string? server = null, int? port = null) => TLauncherBlocked ? Task.CompletedTask : Sessions.LaunchAsync(instance, server, port,
        (text) => Confirm(L("compat.launch"), text, L("compat.continue")));
    public void OpenSelectedFolder() { if (SelectedInstance != null) Platform.OpenPath(Instances.GetGameDir(SelectedInstance.Id)); }
    public void SelectedDetails() { if (SelectedInstance != null) EditInstance(SelectedInstance.Id); else NewInstance(); }
    public void SelectInstance()
    {
        var instances = Instances.GetAllInstances();
        if (instances.Count == 0) { Library(); return; }
        ShowChoices(T("Выбрать сборку", "Select instance"), instances.Select(instance => new ItemModel
        {
            Id = instance.Id, Title = instance.Name, Meta = InstanceLabel(instance),
            Action = () => { CloseDialog(); SetInstance(instance.Id); }
        }));
    }
    public void SetInstance(string id)
    {
        SelectedInstance = Instances.GetInstance(id);
        Settings.SelectedInstanceId = SelectedInstance?.Id; Settings.Save();
        HeroImage = MediaCache.LoadLocal(SelectedInstance == null ? null : Instances.GetCoverAbsolutePath(SelectedInstance));
        Changed();
        if (Page == "catalog") { World = null; Search(); }
        if (Page == "project") { World = null; if (projectType == "modpack" && CompatibleOnly) projectMinecraft = SelectedInstance?.McVersion; LoadProject(); }
    }
    public void ShowChoices(string title, IEnumerable<ItemModel> choices)
    {
        BeginDialog(title, "", "", null);
        dialogAllChoices = choices.ToArray(); DialogSearch = true;
        foreach (var choice in dialogAllChoices) DialogChoices.Add(choice);
        Changed();
    }
    public void BeginDialog(string title, string body, string primary, Func<Task>? action, params FieldModel[] fields)
    {
        if (DialogBusy) return;
        dialogCompletion?.TrySetResult(false); dialogCompletion = null;
        dialogGeneration++; DialogFields.Clear(); DialogChoices.Clear(); DialogError = ""; DialogDanger = false;
        DialogImage = ImageSource.Empty; DialogText = "";
        dialogImagePath = null;
        dialogQuery = ""; dialogAllChoices = []; DialogSearch = false;
        foreach (var field in fields) DialogFields.Add(field);
        DialogTitle = title; DialogBody = body; DialogPrimary = primary; dialogAction = action;
        DialogOpen = true; Changed();
    }
    public string Field(string id) => DialogFields.First(f => f.Id == id).Value.Trim();
    public void AcceptDialog() => Run(async () =>
    {
        if (DialogBusy) return;
        int generation = dialogGeneration;
        DialogBusy = true; DialogError = ""; Changed();
        try
        {
            if (dialogAction != null) await dialogAction();
            if (generation == dialogGeneration) { dialogCompletion?.TrySetResult(true); DialogBusy = false; CloseDialog(); }
        }
        catch (Exception ex) { if (generation == dialogGeneration) DialogError = ex.Message; }
        finally { DialogBusy = false; Changed(); }
    });
    public void CloseDialog()
    {
        if (DialogBusy) return;
        if (DialogOpen) DialogClosing?.Invoke();
        dialogGeneration++; dialogCompletion?.TrySetResult(false); dialogCompletion = null;
        DialogOpen = false; dialogAction = null; DialogFields.Clear(); DialogChoices.Clear(); Changed();
    }
    public Task<bool> Confirm(string title, string body, string action, bool destructive = false)
    {
        BeginDialog(title, body, action, null);
        DialogDanger = destructive; Changed();
        dialogCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        return dialogCompletion.Task;
    }
    public void Tick()
    {
        if (disposed) return;
        if (Page == "downloads") RefreshDownloads();
        if (Page == "settings") Changed();
        if (Sessions.Preparing || Sessions.RunningCount > 0) Changed();
    }
    public void Dispose()
    {
        if (disposed) return; disposed = true;
        tlauncherLifetime.Cancel(); tlauncherLifetime.Dispose();
        pageLifetime.Cancel(); pageLifetime.Dispose(); catalogCancellation?.Cancel(); catalogCancellation?.Dispose(); skinBitmap?.Dispose();
        searchDelay?.Cancel(); searchDelay?.Dispose();
        foreach (var snapshot in skinUndo) snapshot.Dispose();
        dialogCompletion?.TrySetResult(false);
        InstanceManager.InstancesChanged -= InstancesChangedHandler;
        Sessions.Changed -= OnSessionChanged; Sessions.Dispose();
    }
}
