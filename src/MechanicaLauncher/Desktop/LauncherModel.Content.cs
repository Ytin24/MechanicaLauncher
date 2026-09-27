using MechanicaLauncher.Core.Mods;

namespace MechanicaLauncher.Desktop;

public sealed partial class LauncherModel
{
    private readonly InstalledContentIndex contentIndex;
    private IReadOnlyList<IndexedContent> indexedContent = [];
    private (string Instance, string Type, string? World)? indexedContext;
    private (string Instance, string Type, string? World)? ContentContext
    {
        get
        {
            string type = Page == "project" ? projectType : ContentType;
            return SelectedInstance == null || type == "modpack" || type == "datapack" && World == null
                ? null : (SelectedInstance.Id, type, type == "datapack" ? World : null);
        }
    }
    private IEnumerable<IndexedContent> CurrentContent => indexedContext == ContentContext ? indexedContent : [];
    private CancellationTokenSource? contentCancellation;
    private Task? contentScan;
    private int contentGeneration;
    private bool contentScanning, contentIndexReady, contentIndexFailed;
    private readonly Dictionary<string, string> catalogDetails = [];
    private readonly HashSet<Guid> indexedJobs = [];

    private Task RefreshContentIndexAsync(bool force = false)
    {
        var context = ContentContext;
        if (!force && context == indexedContext && contentCancellation?.IsCancellationRequested != true)
        {
            if (contentScan is { IsCompleted: false }) return contentScan;
            if (contentIndexReady) return Task.CompletedTask;
        }
        contentCancellation?.Cancel(); contentCancellation?.Dispose();
        contentCancellation = CancellationTokenSource.CreateLinkedTokenSource(PageToken);
        var token = contentCancellation.Token;
        int generation = ++contentGeneration;
        if (context != indexedContext) indexedContent = [];
        indexedContext = context;
        contentIndexReady = false; contentIndexFailed = false; contentScanning = context != null;
        UpdateContentView();
        return contentScan = Scan();

        async Task Scan()
        {
            try
            {
                IReadOnlyList<IndexedContent> result = [];
                if (context is { } target)
                {
                    string gameDir = Instances.GetGameDir(target.Instance);
                    string directory = ModInstaller.GetContentDirectory(gameDir, target.Type, target.World);
                    string extension = target.Type == "mod" ? ".jar" : ".zip";
                    if (Directory.Exists(directory) && Directory.EnumerateFiles(directory).Any(path =>
                        path.EndsWith(extension, StringComparison.OrdinalIgnoreCase) || path.EndsWith(extension + ".disabled", StringComparison.OrdinalIgnoreCase)))
                    {
                        var progress = new Progress<IReadOnlyList<IndexedContent>>(items =>
                        {
                            if (disposed || generation != contentGeneration || token.IsCancellationRequested || !contentScanning) return;
                            indexedContent = items; UpdateContentView();
                        });
                        result = await contentIndex.ScanAsync(gameDir, target.Type, target.World, token, progress);
                    }
                }
                token.ThrowIfCancellationRequested();
                if (generation != contentGeneration) return;
                indexedContent = result; contentIndexReady = true;
            }
            catch (Exception) when (token.IsCancellationRequested) { }
            catch (Exception)
            {
                if (generation == contentGeneration) contentIndexFailed = true;
            }
            finally
            {
                if (!disposed && generation == contentGeneration)
                {
                    contentScanning = false; UpdateContentView();
                }
            }
        }
    }

    private void UpdateContentView()
    {
        if (Page == "catalog")
        {
            if (InstalledOnly) RenderInstalled();
            else foreach (var row in CatalogItems)
                if (catalogDetails.TryGetValue(row.Id, out var details))
                {
                    row.Meta = CatalogContentMeta(row.Id, details); row.Changed();
                }
        }
        Changed();
    }

    private string CatalogContentMeta(string projectId, string details)
    {
        var matches = CurrentContent.Where(item => item.ProjectId == projectId).ToArray();
        return matches.Length == 0 ? details : details + " · " + (matches.Any(item => item.Enabled)
            ? T("Установлено", "Installed") : T("Установлено · Отключено", "Installed · Disabled"));
    }

    private IndexedContent? InstalledProject => CurrentContent
        .Where(item => item.ProjectId == project?.ProjectId || IsSelectedContent(item))
        .OrderByDescending(IsSelectedContent).ThenByDescending(item => item.Enabled).FirstOrDefault();

    private bool IsSelectedContent(IndexedContent item) => selectedVersion != null &&
        (item.VersionId == selectedVersion.Id || item.Sha1.Length > 0 && selectedVersion.Files.Any(file =>
            file.Hashes.TryGetValue("sha1", out var hash) && hash.Equals(item.Sha1, StringComparison.OrdinalIgnoreCase)));

    private InstallRequirement InstalledRequirement => InstalledProject is not { } item ? InstallRequirement.Ready
        : !IsSelectedContent(item) ? InstallRequirement.OtherVersion
        : item.Enabled ? InstallRequirement.Installed : InstallRequirement.Disabled;

    private void OpenInstalledProject()
    {
        if (project == null) return;
        query = InstalledProject?.FileName ?? project.Title;
        ContentType = projectType; InstalledOnly = true;
        Navigate("catalog");
    }

    private async Task LoadInstalledAsync(bool refresh)
    {
        catalogCancellation?.Cancel(); ++catalogGeneration; catalogSelection = null;
        CatalogFailed = false; MoreCatalog = false; CatalogItems.Clear(); catalogIcons.Clear(); catalogDetails.Clear();
        await RefreshContentIndexAsync(refresh);
        if (!disposed && Page == "catalog" && InstalledOnly) RenderInstalled();
    }

    private void RenderInstalled()
    {
        CatalogItems.Clear();
        CatalogLoading = contentScanning; CatalogFailed = contentIndexFailed;
        if (SelectedInstance is not { } instance) { CatalogStatus = T("Сначала выбери сборку", "Select an instance first"); Changed(); return; }
        if (NeedsWorld && World == null) { CatalogStatus = T("Выбери мир для датапаков", "Choose a world for datapacks"); Changed(); return; }
        string filter = Query.Trim();
        foreach (var mod in CurrentContent.Where(item => (item.DisplayName + " " + item.FileName + " " + item.DisplayVersion)
            .Contains(filter, StringComparison.OrdinalIgnoreCase)).OrderBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase))
        {
            string source = mod.Match switch
            {
                CatalogMatch.Matched => "Modrinth",
                CatalogMatch.NotFound => T("Не найдено на Modrinth", "Not found on Modrinth"),
                _ when mod.Sha1.Length == 0 => T("Не удалось прочитать файл", "Could not read file"),
                _ => contentScanning ? T("Проверяю Modrinth…", "Checking Modrinth…") : T("Modrinth недоступен", "Modrinth unavailable")
            };
            string size = mod.SizeBytes >= 1024 * 1024 ? $"{mod.SizeBytes / 1048576d:F1} MB" : $"{mod.SizeBytes / 1024d:F0} KB";
            Action remove = () => Run(async () =>
            {
                RequireEventPermission(AllowModToggle);
                if (!await Confirm(T("Удалить файл?", "Delete file?"), mod.FileName, T("Удалить", "Delete"), destructive: true)) return;
                RequireEventPermission(AllowModToggle);
                if (Sessions.IsBusy(instance.Id)) throw new InvalidOperationException(L("feature.busy"));
                ModInstaller.RemoveMod(mod.FilePath);
                indexedContent = indexedContent.Where(item => item.FilePath != mod.FilePath).ToArray();
                await RefreshContentIndexAsync(true);
            });
            var row = new ItemModel
            {
                Id = mod.FilePath, Title = mod.DisplayName, Description = mod.DisplayName == mod.FileName ? "" : mod.FileName,
                Meta = string.Join(" · ", new[] { mod.DisplayVersion, mod.Enabled ? "" : T("Отключено", "Disabled"), source, size }.Where(value => value.Length > 0)),
                Primary = mod.Enabled ? T("Отключить", "Disable") : T("Включить", "Enable"),
                Secondary = mod.ProjectId == null ? T("Удалить", "Remove") : "Modrinth",
                Tertiary = mod.ProjectId == null ? "" : T("Удалить", "Remove"), Enabled = AllowModToggle,
                Action = () => Run(async () =>
                {
                    RequireEventPermission(AllowModToggle);
                    if (Sessions.IsBusy(instance.Id)) throw new InvalidOperationException(L("feature.busy"));
                    ModInstaller.ToggleMod(mod.FilePath);
                    indexedContent = indexedContent.Select(item => item.FilePath == mod.FilePath ? item with
                    {
                        Enabled = !mod.Enabled,
                        FilePath = mod.Enabled ? mod.FilePath + ".disabled" : mod.FilePath[..^".disabled".Length]
                    } : item).ToArray();
                    await RefreshContentIndexAsync(true);
                }),
                Action2 = mod.ProjectId == null ? remove : () => Platform.OpenUrl("https://modrinth.com/project/" + Uri.EscapeDataString(mod.ProjectId)),
                Action3 = mod.ProjectId == null ? null : remove
            };
            ConfigureModUpdateRow(row, mod);
            CatalogItems.Add(row); LoadRemoteImage(row, mod.IconUrl, PageToken);
        }
        CatalogStatus = contentScanning ? T("Читаю установленные файлы…", "Reading installed files…")
            : T($"Установлено: {CatalogItems.Count}", $"Installed: {CatalogItems.Count}");
        Changed();
    }

    private void RefreshCompletedContent()
    {
        bool refresh = false;
        foreach (var job in Sessions.Downloads.Jobs.Where(job => job.State is not (DownloadState.Queued or DownloadState.Running)))
            if (indexedJobs.Add(job.Id) && indexedContext is { } context && (job.InstanceId == context.Instance || job.ResultInstanceId == context.Instance)) refresh = true;
        if (!refresh) return;
        contentIndexReady = false;
        if (Page is "catalog" or "project") Run(() => RefreshContentIndexAsync(true));
    }
}
