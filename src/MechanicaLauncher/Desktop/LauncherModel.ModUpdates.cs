using MechanicaLauncher.Core.Mods;

namespace MechanicaLauncher.Desktop;

public sealed partial class LauncherModel
{
    private readonly ModUpdateService modUpdates;
    private ModUpdateScan? modUpdateScan;
    private string? modUpdateContext;
    private CancellationTokenSource? modUpdateCancellation;
    private string modUpdateFailure = "";
    private bool modUpdatePlanning;
    public bool CheckingModUpdates { get; private set; }
    public bool ShowModUpdates => InstalledOnly && ContentType == "mod" && SelectedInstance != null;
    public bool CanCheckModUpdates => ShowModUpdates && !CheckingModUpdates && !modUpdatePlanning && !contentScanning &&
        SelectedInstance is { } instance && !Sessions.IsBusy(instance.Id);
    private string ModUpdateContext => SelectedInstance is not { } instance ? "" :
        $"{instance.Id}|{instance.McVersion}|{instance.Loader}|{instance.LoaderVersion}|{contentGeneration}";
    private ModUpdateScan? CurrentModUpdates => modUpdateContext == ModUpdateContext ? modUpdateScan : null;
    public int ModUpdateCount => CurrentModUpdates?.Items.Count(item => item.State == ModUpdateState.Available) ?? 0;
    public string ModUpdateButton => CheckingModUpdates ? T("Проверяю обновления…", "Checking updates…") : T("Проверить обновления", "Check for updates");
    public string ModUpdateSelectionLabel => T($"Выбрать обновления ({ModUpdateCount})", $"Select updates ({ModUpdateCount})");
    public string ModUpdateStatus
    {
        get
        {
            if (!ShowModUpdates || CheckingModUpdates || modUpdateContext != ModUpdateContext) return "";
            if (modUpdateFailure.Length > 0) return modUpdateFailure;
            if (CurrentModUpdates is not { } scan) return "";
            if (scan.Errors.Count > 0) return string.Join(" ", scan.Errors);
            int unavailable = scan.Items.Count(item => item.State == ModUpdateState.Unavailable);
            if (unavailable > 0) return T($"Не удалось проверить: {unavailable}", $"Could not check: {unavailable}");
            if (ModUpdateCount > 0) return T($"Доступны обновления: {ModUpdateCount}", $"Updates available: {ModUpdateCount}");
            return scan.Items.Any(item => item.State == ModUpdateState.Current)
                ? T("Новых совместимых версий не найдено", "No newer compatible versions found")
                : T("Нет модов, доступных для обновления через Modrinth", "No mods available for updates through Modrinth");
        }
    }

    public void CheckModUpdates() => Run(CheckModUpdatesAsync);

    private async Task CheckModUpdatesAsync()
    {
        if (!CanCheckModUpdates || SelectedInstance is not { } instance) return;
        modUpdateCancellation?.Cancel(); modUpdateCancellation?.Dispose();
        modUpdateCancellation = CancellationTokenSource.CreateLinkedTokenSource(PageToken);
        var token = modUpdateCancellation.Token;
        string context = ModUpdateContext;
        CheckingModUpdates = true; modUpdateFailure = ""; Changed();
        try
        {
            await RefreshContentIndexAsync(true);
            token.ThrowIfCancellationRequested();
            if (SelectedInstance?.Id != instance.Id || !ShowModUpdates) return;
            context = ModUpdateContext;
            var scan = await modUpdates.CheckAsync(instance, Instances.GetGameDir(instance.Id), token);
            token.ThrowIfCancellationRequested();
            if (context != ModUpdateContext) return;
            modUpdateScan = scan; modUpdateContext = context;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (context == ModUpdateContext)
            {
                modUpdateScan = null; modUpdateContext = context;
                modUpdateFailure = T("Не удалось проверить обновления. ", "Could not check updates. ") + ex.Message;
            }
        }
        finally
        {
            CheckingModUpdates = false;
            if (!disposed) { if (ShowModUpdates && Page == "catalog") RenderInstalled(); Changed(); }
        }
    }

    public void SelectModUpdates()
    {
        if (!CanCheckModUpdates || !AllowModInstall || DialogOpen || CurrentModUpdates is not { } scan) return;
        var updates = scan.Items.Where(item => item.State == ModUpdateState.Available).ToArray();
        if (updates.Length == 0) return;
        var fields = updates.Select(item => new FieldModel(item.FilePath,
            $"{CurrentContent.FirstOrDefault(mod => mod.FilePath == item.FilePath)?.DisplayName ?? item.Name} · {item.CurrentVersion} → {item.TargetVersion}") { IsToggle = true, IsChecked = true }).ToArray();
        BeginDialog(T("Выбрать обновления", "Select updates"), "", T("Продолжить", "Continue"), async () =>
        {
            var selected = fields.Where(field => field.IsChecked).Select(field => field.Id).ToArray();
            if (selected.Length == 0) throw new InvalidOperationException(T("Выбери хотя бы один мод.", "Select at least one mod."));
            var plan = await PrepareModUpdatesAsync(scan, selected);
            DialogBusy = false;
            ShowModUpdatePlan(scan, plan);
        }, fields);
    }

    private Task<ModUpdatePlan> PrepareModUpdatesAsync(ModUpdateScan scan, string[] selected)
    {
        RequireEventPermission(AllowModInstall);
        if (SelectedInstance is not { } instance || instance.Id != scan.InstanceId || CurrentModUpdates != scan)
            throw new InvalidOperationException(L("catalog.target_changed"));
        if (Sessions.IsBusy(instance.Id)) throw new InvalidOperationException(L("feature.busy"));
        return modUpdates.PlanAsync(scan, selected, PageToken);
    }

    private void UpdateInstalledMod(string path) => Run(async () =>
    {
        if (!CanCheckModUpdates || DialogOpen || CurrentModUpdates is not { } scan) return;
        modUpdatePlanning = true; Changed();
        try
        {
            var plan = await PrepareModUpdatesAsync(scan, [path]);
            if (!DialogOpen && CurrentModUpdates == scan) ShowModUpdatePlan(scan, plan);
        }
        finally { modUpdatePlanning = false; Changed(); }
    });

    private void ShowModUpdatePlan(ModUpdateScan scan, ModUpdatePlan plan)
    {
        if (plan.Conflicts.Count > 0)
            throw new InvalidOperationException(string.Join("\n", plan.Conflicts.Select(item => item.FileName + ": " + item.Reason)));
        if (!plan.HasChanges) { Notice(T("Обновления не требуются", "No updates needed")); return; }
        string body = string.Join("\n", plan.Changes.Select(item => item.Name + " · " +
            (item.CurrentVersion.Length > 0 ? item.CurrentVersion + " → " : "") + item.TargetVersion +
            (item.Dependency ? " · " + T("зависимость", "dependency") : "")));
        body += $"\n\n{plan.DownloadBytes / 1048576d:F1} MB";
        BeginDialog(T("Обновить моды?", "Update mods?"), body, T("Обновить", "Update"), () =>
        {
            RequireEventPermission(AllowModInstall);
            if (SelectedInstance is not { } instance || CurrentModUpdates != scan || instance.Id != scan.InstanceId)
                throw new InvalidOperationException(L("catalog.target_changed"));
            if (Sessions.IsBusy(instance.Id)) throw new InvalidOperationException(L("feature.busy"));
            string minecraft = instance.McVersion;
            var loader = instance.Loader;
            string? loaderVersion = instance.LoaderVersion;
            var job = Sessions.Enqueue(T("Обновление модов · ", "Mod updates · ") + instance.Name, instance.Id, async token =>
            {
                RequireEventPermission(AllowModInstall);
                var current = Instances.GetInstance(instance.Id);
                if (current == null || current.McVersion != minecraft || current.Loader != loader || current.LoaderVersion != loaderVersion)
                    throw new InvalidOperationException(L("catalog.target_changed"));
                await modUpdates.ApplyAsync(plan, () => Sessions.IsRunning(instance.Id), token);
            });
            modUpdateScan = null; modUpdateContext = null;
            Notice(T("Добавлено в центр загрузок", "Added to downloads"));
            Run(async () =>
            {
                await job.Completion;
                if (disposed || Page != "catalog" || !ShowModUpdates || SelectedInstance?.Id != instance.Id) return;
                await RefreshContentIndexAsync(true);
                if (job.State == DownloadState.Completed) await CheckModUpdatesAsync();
            });
            return Task.CompletedTask;
        });
    }

    private void ConfigureModUpdateRow(ItemModel row, IndexedContent mod)
    {
        if (ContentType != "mod" || CurrentModUpdates?.Items.FirstOrDefault(item => item.FilePath.Equals(mod.FilePath, StringComparison.OrdinalIgnoreCase)) is not { } update) return;
        if (update.State == ModUpdateState.Managed)
            row.Meta += " · " + T("По списку сервера", "Managed by server");
        if (update.State != ModUpdateState.Available) return;
        row.Meta += " · " + T($"Доступна {update.TargetVersion}", $"{update.TargetVersion} available");
        if (!AllowModInstall) return;
        var toggle = row.Action;
        var remove = mod.ProjectId == null ? row.Action2 : row.Action3;
        row.Secondary = row.Primary;
        row.Primary = T("Обновить", "Update");
        row.Action = () => UpdateInstalledMod(mod.FilePath);
        row.Action2 = toggle;
        row.Tertiary = T("Ещё", "More");
        row.Action3 = () =>
        {
            var choices = new List<ItemModel>();
            if (mod.ProjectId != null) choices.Add(new() { Title = "Modrinth", Action = () =>
            { CloseDialog(); Platform.OpenUrl("https://modrinth.com/project/" + Uri.EscapeDataString(mod.ProjectId)); } });
            choices.Add(new() { Title = T("Удалить", "Remove"), Action = () => { CloseDialog(); remove?.Invoke(); } });
            ShowChoices(mod.DisplayName, choices);
        };
    }
}
