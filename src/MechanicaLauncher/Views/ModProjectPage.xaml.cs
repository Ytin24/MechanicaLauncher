using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.Web.WebView2.Core;
using MechanicaLauncher.Core.Instances;
using MechanicaLauncher.Core.IO;
using MechanicaLauncher.Core.Mods;
using MechanicaLauncher.Core.Models;
using MechanicaLauncher.Helpers;

namespace MechanicaLauncher.Views;

internal sealed record ModProjectRequest(ModrinthProject Project, string ContentType, string? InstanceId, string? MinecraftVersion = null);

public sealed partial class ModProjectPage : Page
{
    private readonly InstanceManager _instances = new();
    private readonly ModrinthClient _client = new();
    private ModProjectRequest? _request;
    private ModrinthProjectInfo? _project;
    private GameInstance? _target;
    private List<ModrinthGalleryImage> _gallery = [];
    private int _imageIndex;
    private bool _galleryBuilt;
    private bool _active;
    private bool _updating;
    private bool _busy;
    private bool _loadingVersions;
    private bool _versionsError;
    private bool _creatingWebView;
    private WebView2? _descriptionView;
    private CancellationTokenSource? _descriptionCancellation;
    private string? _pendingDescriptionUri;
    private ulong? _descriptionNavigationId;
    private TaskCompletionSource<bool>? _descriptionLoaded;
    private CancellationTokenSource? _projectCancellation;
    private CancellationTokenSource? _versionsCancellation;
    private DownloadJob? _installJob;
    private string? _createdInstanceId;

    private string ContentType => _request?.ContentType ?? "mod";
    private ModrinthVersion? SelectedVersion => (VersionPicker.SelectedItem as ComboBoxItem)?.Tag as ModrinthVersion;
    private string? SelectedWorld => (WorldPicker.SelectedItem as ComboBoxItem)?.Tag as string;
    private static Brush Brush(string key) => (Brush)Application.Current.Resources[key];

    public ModProjectPage()
    {
        InitializeComponent();
        AnimationHelper.AddButtonFeedback(InstallButton);
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _request = e.Parameter as ModProjectRequest;
        _active = true;
        ApplyLocale();
        if (_request == null) return;
        ProjectTitle.Text = _request.Project.Title;
        ProjectSummary.Text = _request.Project.Description;
        ProjectMeta.Text = $"{_request.Project.Author} · {_request.Project.DownloadsFormatted} {App.L("mods.downloads")}";
        SetIcon(_request.Project.IconUrl);
        _updating = true;
        foreach (var instance in _instances.GetAllInstances())
        {
            var item = new ComboBoxItem { Content = instance.Name, Tag = instance };
            TargetPicker.Items.Add(item);
            if (instance.Id == _request.InstanceId) TargetPicker.SelectedItem = item;
        }
        _target = (TargetPicker.SelectedItem as ComboBoxItem)?.Tag as GameInstance;
        _updating = false;
        TargetPicker.Visibility = ContentType == "modpack" ? Visibility.Collapsed : Visibility.Visible;
        PackTarget.Visibility = ContentType == "modpack" ? Visibility.Visible : Visibility.Collapsed;
        RefreshWorlds();
        ShowDescription();
        App.RunningInstancesChanged += RunningChanged;
        _ = LoadProjectAsync();
        _ = LoadVersionsAsync();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _active = false;
        _projectCancellation?.Cancel();
        _versionsCancellation?.Cancel();
        _descriptionCancellation?.Cancel();
        App.RunningInstancesChanged -= RunningChanged;
        base.OnNavigatedFrom(e);
    }

    private void ApplyLocale()
    {
        BackLabel.Text = App.L("catalog.back");
        DownloadsButton.Content = App.L("downloads.open");
        DescriptionTab.Content = App.L("catalog.description");
        GalleryTab.Content = App.L("catalog.gallery");
        InstallTitle.Text = App.L("mods.install");
        PackTarget.Text = App.L("catalog.pack_notice");
        TargetPicker.Header = App.L("catalog.target");
        TargetPicker.PlaceholderText = App.L("catalog.select_instance");
        VersionPicker.Header = App.L("catalog.version");
        WorldPicker.Header = App.L("catalog.world");
        WorldPicker.PlaceholderText = App.L("catalog.select_world");
        CancelButton.Content = App.L("inst.cancel");
        RetryButton.Content = App.L("catalog.retry");
        OpenInstanceButton.Content = App.L("catalog.open_instance");
        DescriptionFallback.Text = App.L("catalog.loading");
        AutomationProperties.SetName(PreviousImage, App.L("catalog.previous_image"));
        AutomationProperties.SetName(NextImage, App.L("catalog.next_image"));
        AutomationProperties.SetName(WebsiteButton, App.L("catalog.website"));
        ToolTipService.SetToolTip(WebsiteButton, App.L("catalog.website"));
    }

    private void SetIcon(string? url)
    {
        if (TryHttps(url, out var uri)) ProjectIcon.Source = new BitmapImage(uri) { DecodePixelWidth = 128 };
    }

    private async Task LoadProjectAsync()
    {
        if (_request == null || !_active) return;
        _projectCancellation?.Cancel();
        using var cancellation = new CancellationTokenSource();
        _projectCancellation = cancellation;
        LoadingRing.IsActive = true;
        LoadError.IsOpen = false;
        RetryButton.Visibility = Visibility.Collapsed;
        try
        {
            var project = await _client.GetProjectAsync(_request.Project.ProjectId, cancellation.Token)
                ?? throw new InvalidDataException("Project not found.");
            cancellation.Token.ThrowIfCancellationRequested();
            if (!_active) return;
            _project = project;
            ProjectTitle.Text = project.Title;
            ProjectSummary.Text = project.Description ?? "";
            SetIcon(project.IconUrl);
            ProjectMeta.Text = $"{_request.Project.Author} · {_request.Project.DownloadsFormatted} {App.L("mods.downloads")}" +
                (project.Updated is { } updated ? $" · {App.L("catalog.updated", updated.ToLocalTime().ToString("d"))}" : "");
            _gallery = project.Gallery.Where(g => TryHttps(g.Url, out _))
                .OrderByDescending(g => g.Featured).ThenBy(g => g.Ordering ?? int.MaxValue).ToList();
            _galleryBuilt = false;
            GalleryTab.Content = App.L("catalog.gallery") + $" ({_gallery.Count})";
            if (GalleryPanel.Visibility == Visibility.Visible) BuildGallery();
            await RenderDescriptionAsync();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception)
        {
            if (!_active) return;
            LoadError.Message = App.L("catalog.project_error");
            LoadError.IsOpen = true;
            RetryButton.Visibility = Visibility.Visible;
            DescriptionFallback.Text = App.L("catalog.project_error");
        }
        finally
        {
            if (ReferenceEquals(_projectCancellation, cancellation))
            {
                _projectCancellation = null;
                LoadingRing.IsActive = false;
            }
        }
    }

    private async Task LoadVersionsAsync()
    {
        if (_request == null || !_active) return;
        _versionsCancellation?.Cancel();
        using var cancellation = new CancellationTokenSource();
        _versionsCancellation = cancellation;
        _loadingVersions = true;
        _versionsError = false;
        VersionPicker.Items.Clear();
        UpdateInstallState();
        var target = _target;
        try
        {
            if (ContentType != "modpack" && (target == null || (ContentType == "mod" && target.Loader == LoaderType.None))) return;
            var loader = ContentType == "datapack" ? "datapack" :
                ContentType == "mod" ? target!.Loader.ToString().ToLowerInvariant() : null;
            var mcVersion = ContentType == "modpack" ? _request.MinecraftVersion : target!.McVersion;
            var versions = await _client.GetProjectVersionsAsync(_request.Project.ProjectId,
                mcVersion, loader, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (!_active || !ReferenceEquals(_versionsCancellation, cancellation)) return;
            var extension = ContentType == "modpack" ? ".mrpack" : ContentType == "mod" ? ".jar" : ".zip";
            foreach (var version in versions.Where(v => (mcVersion == null || v.GameVersions.Contains(mcVersion)) &&
                         (loader == null || v.Loaders.Contains(loader, StringComparer.OrdinalIgnoreCase)) &&
                         v.Files.Any(f => f.Filename.EndsWith(extension, StringComparison.OrdinalIgnoreCase)))
                         .OrderByDescending(v => v.DatePublished))
            {
                var suffix = version.VersionType == "release" ? "" : " · " + version.VersionType;
                VersionPicker.Items.Add(new ComboBoxItem { Content = version.VersionNumber + suffix, Tag = version });
            }
            VersionPicker.SelectedItem = VersionPicker.Items.Cast<ComboBoxItem>()
                .FirstOrDefault(i => ((ModrinthVersion)i.Tag).VersionType == "release") ?? VersionPicker.Items.FirstOrDefault();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception)
        {
            if (!_active || !ReferenceEquals(_versionsCancellation, cancellation)) return;
            _versionsError = true;
            RetryButton.Visibility = Visibility.Visible;
        }
        finally
        {
            if (ReferenceEquals(_versionsCancellation, cancellation))
            {
                _versionsCancellation = null;
                _loadingVersions = false;
                UpdateInstallState();
            }
        }
    }

    private void RefreshWorlds()
    {
        _updating = true;
        WorldPicker.Items.Clear();
        WorldPicker.Visibility = ContentType == "datapack" ? Visibility.Visible : Visibility.Collapsed;
        try
        {
            if (_target != null && ContentType == "datapack")
            {
                var saves = Path.Combine(_instances.GetGameDir(_target.Id), "saves");
                if (Directory.Exists(saves))
                    foreach (var path in Directory.EnumerateDirectories(saves).Where(p => File.Exists(Path.Combine(p, "level.dat"))).Order())
                        WorldPicker.Items.Add(new ComboBoxItem { Content = Path.GetFileName(path), Tag = Path.GetFileName(path) });
                if (WorldPicker.Items.Count == 1) WorldPicker.SelectedIndex = 0;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        finally { _updating = false; }
    }

    private async void Target_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_updating || !_active || _busy) return;
        _target = (TargetPicker.SelectedItem as ComboBoxItem)?.Tag as GameInstance;
        InstallStatus.Text = "";
        RefreshWorlds();
        await LoadVersionsAsync();
    }

    private void Version_Changed(object sender, SelectionChangedEventArgs e) { if (!_updating) UpdateInstallState(); }
    private void World_Changed(object sender, SelectionChangedEventArgs e) { if (!_updating) UpdateInstallState(); }
    private void RunningChanged() => DispatcherQueue.TryEnqueue(() => { if (_active) UpdateInstallState(); });

    private void UpdateInstallState()
    {
        if (!_active) return;
        var version = SelectedVersion;
        var pack = ContentType == "modpack";
        TargetSummary.Text = pack ? (_request?.MinecraftVersion is { } mc ? App.L("catalog.mc_target", mc) : App.L("catalog.all_versions")) :
            _target != null ? ModsPage.InstanceText(_target) : "";
        VersionPicker.IsEnabled = !_busy && !_loadingVersions && VersionPicker.Items.Count > 0;
        TargetPicker.IsEnabled = !_busy;
        WorldPicker.IsEnabled = !_busy;
        BackButton.IsEnabled = true;
        InstallButton.Content = App.L(pack ? "catalog.create_instance" : "mods.install");
        var reason = _busy ? App.L("catalog.installing") :
            !pack && _target == null ? App.L("catalog.select_instance") :
            ContentType == "mod" && _target?.Loader == LoaderType.None ? App.L("catalog.loader_required") :
            _loadingVersions ? App.L("catalog.loading_versions") :
            _versionsError ? App.L("catalog.versions_error") :
            version == null ? App.L(pack ? (_request?.MinecraftVersion == null ? "catalog.pack_no_files" : "catalog.pack_no_versions") : "catalog.no_versions") :
            ContentType == "datapack" && SelectedWorld == null ? App.L(WorldPicker.Items.Count == 0 ? "catalog.no_worlds" : "catalog.select_world") :
            !pack && _target != null && App.RunningInstances.TryGetValue(_target.Id, out var running) && !running.HasExited ? App.L("inst.busy") : null;
        if (reason == null && !pack && _target != null)
        {
            try
            {
                var directory = ModInstaller.GetContentDirectory(_instances.GetGameDir(_target.Id), ContentType, SelectedWorld);
                var selectedFile = ModInstaller.SelectFile(version!, ContentType == "mod" ? ".jar" : ".zip").Filename;
                var installedFiles = VersionPicker.Items.Cast<ComboBoxItem>()
                    .SelectMany(item => ((ModrinthVersion)item.Tag).Files)
                    .Select(file => file.Filename).Where(name => Path.GetFileName(name) == name)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Where(name => File.Exists(Path.Combine(directory, name)) || File.Exists(Path.Combine(directory, name + ".disabled")))
                    .ToList();
                if (installedFiles.Any(name => !name.Equals(selectedFile, StringComparison.OrdinalIgnoreCase)) ||
                    File.Exists(Path.Combine(directory, selectedFile + ".disabled")))
                    reason = App.L("catalog.existing_version");
                else if (installedFiles.Count > 0)
                    InstallButton.Content = App.L("catalog.verify_install");
            }
            catch (Exception ex) { reason = ex.Message; }
        }
        InstallButton.IsEnabled = reason == null && App.EventConfig?.Ui?.AllowModInstall != false;
        CompatibilityInfo.Text = reason ?? App.L("catalog.hint." + ContentType);
        if (App.EventConfig?.Ui?.AllowModInstall == false)
            CompatibilityInfo.Text = App.L("catalog.install_locked");
        if (version == null) { VersionInfo.Text = ""; return; }
        var file = ModInstaller.SelectFile(version, pack ? ".mrpack" : ContentType == "mod" ? ".jar" : ".zip");
        var games = string.Join(", ", version.GameVersions.Take(6)) + (version.GameVersions.Count > 6 ? "…" : "");
        VersionInfo.Text = $"Minecraft {games}\n{string.Join(", ", version.Loaders)} · {file.SizeFormatted}" +
            (version.DatePublished is { } date ? "\n" + date.ToLocalTime().ToString("d") : "") +
            (version.VersionType != "release" ? "\n" + App.L("catalog.prerelease") : "");
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        var version = SelectedVersion;
        var target = _target;
        var world = SelectedWorld;
        var type = ContentType;
        var pack = type == "modpack";
        if (_busy || version == null || !InstallButton.IsEnabled || App.EventConfig?.Ui?.AllowModInstall == false) return;
        if (!pack && (target == null || App.RunningInstances.TryGetValue(target.Id, out var running) && !running.HasExited)) { UpdateInstallState(); return; }
        _busy = true;
        InstallProgress.Visibility = Visibility.Visible;
        CancelButton.Visibility = Visibility.Visible;
        CancelButton.IsEnabled = true;
        OpenInstanceButton.Visibility = Visibility.Collapsed;
        InstallStatus.Text = App.L("downloads.queued");
        UpdateInstallState();
        var instances = new InstanceManager();
        var pageReference = new WeakReference<ModProjectPage>(this);
        var alternativeFiles = VersionPicker.Items.Cast<ComboBoxItem>().SelectMany(item => ((ModrinthVersion)item.Tag).Files)
            .Select(file => file.Filename).Where(name => Path.GetFileName(name) == name).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var job = App.Downloads.Enqueue($"{_request!.Project.Title} · {version.VersionNumber}" +
            (target != null && !pack ? $" → {target.Name}" : ""), pack ? null : target?.Id, async token =>
        {
            var cancellation = DownloadQueue.CurrentCancellation!;
            await App.LaunchPreparationGate.WaitAsync(token);
            try
            {
                if (App.EventConfig?.Ui?.AllowModInstall == false)
                    throw new InvalidOperationException(App.L("catalog.install_locked"));
                if (!pack && App.IsInstanceBusy(target!.Id))
                    throw new InvalidOperationException(App.L("feature.busy"));
                App.PreparationCancellation = cancellation;
                App.PreparingInstanceId = pack ? "modpack-import-" + Guid.NewGuid().ToString("N") : target!.Id;
                App.NotifyRunningChanged();
                var installer = new ModInstaller();
                installer.StatusChanged += message =>
                {
                    if (pageReference.TryGetTarget(out var page)) page.DispatcherQueue.TryEnqueue(() =>
                    {
                        if (page._active && page._busy) page.InstallStatus.Text = App.L("catalog.downloading", message);
                    });
                };
                if (pack)
                {
                    var created = await installer.ImportModpackAsync(version, instances, token);
                    DownloadQueue.Current!.ResultInstanceId = created.Id;
                }
                else
                {
                    var current = instances.GetInstance(target!.Id);
                    if (current == null || current.McVersion != target.McVersion || current.Loader != target.Loader)
                        throw new InvalidOperationException(App.L("catalog.target_changed"));
                    var directory = ModInstaller.GetContentDirectory(instances.GetGameDir(current.Id), type, world);
                    var selectedFile = ModInstaller.SelectFile(version, type == "mod" ? ".jar" : ".zip").Filename;
                    if (alternativeFiles.Any(name => !name.Equals(selectedFile, StringComparison.OrdinalIgnoreCase) &&
                        (File.Exists(Path.Combine(directory, name)) || File.Exists(Path.Combine(directory, name + ".disabled")))) ||
                        File.Exists(Path.Combine(directory, selectedFile + ".disabled")))
                        throw new InvalidOperationException(App.L("catalog.existing_version"));
                    await installer.InstallContentAsync(version, instances.GetGameDir(current.Id), type, current.McVersion,
                        current.Loader == LoaderType.None ? null : current.Loader.ToString().ToLowerInvariant(), world, cancellation.Token);
                }
            }
            finally
            {
                if (ReferenceEquals(App.PreparationCancellation, cancellation))
                {
                    App.PreparationCancellation = null;
                    App.PreparingInstanceId = null;
                }
                App.LaunchPreparationGate.Release();
                App.NotifyRunningChanged();
            }
        });
        _installJob = job;
        await job.Completion;
        var result = job.Snapshot();
        _createdInstanceId = job.ResultInstanceId;
        InstallStatus.Text = result.State == DownloadState.Completed
            ? (pack ? App.L("catalog.created", _instances.GetInstance(_createdInstanceId!)?.Name ?? "") : App.L("catalog.installed_to", target!.Name))
            : result.Error ?? App.L("downloads." + result.State.ToString().ToLowerInvariant());
        OpenInstanceButton.Visibility = result.State == DownloadState.Completed && pack ? Visibility.Visible : Visibility.Collapsed;
        _busy = false;
        _installJob = null;
        InstallProgress.Visibility = Visibility.Collapsed;
        CancelButton.Visibility = Visibility.Collapsed;
        UpdateInstallState();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _installJob?.Cancel();
        CancelButton.IsEnabled = false;
    }

    private void Downloads_Click(object sender, RoutedEventArgs e)
    {
        if (App.MainWindow is MainWindow main) main.NavigateToTag("Downloads");
    }

    private void OpenInstance_Click(object sender, RoutedEventArgs e)
    {
        if (_createdInstanceId == null) return;
        App.Settings.SelectedInstanceId = _createdInstanceId;
        App.Settings.Save();
        if (App.MainWindow is MainWindow main) main.NavigateToTag("Home");
    }

    private void BuildGallery()
    {
        _galleryBuilt = true;
        GalleryThumbnails.Children.Clear();
        GalleryTab.Content = App.L("catalog.gallery") + $" ({_gallery.Count})";
        for (var i = 0; i < _gallery.Count; i++)
        {
            var index = i;
            var entry = _gallery[i];
            var button = new Button { Padding = new Thickness(2), CornerRadius = new CornerRadius(8),
                Content = new Image { Source = new BitmapImage(new Uri(entry.Url)) { DecodePixelWidth = 220 },
                    Width = 104, Height = 64, Stretch = Stretch.UniformToFill } };
            AutomationProperties.SetName(button, entry.Title ?? App.L("catalog.image_number", i + 1));
            button.Click += (_, _) => ShowImage(index);
            GalleryThumbnails.Children.Add(button);
        }
        ShowImage(0);
    }

    private void ShowImage(int index)
    {
        PreviousImage.IsEnabled = _gallery.Count > 1;
        NextImage.IsEnabled = _gallery.Count > 1;
        if (_gallery.Count == 0)
        {
            GalleryImage.Source = null;
            GalleryMessage.Text = App.L("catalog.no_gallery");
            GalleryMessage.Visibility = Visibility.Visible;
            GalleryCaption.Text = "";
            return;
        }
        _imageIndex = (index + _gallery.Count) % _gallery.Count;
        var image = _gallery[_imageIndex];
        GalleryMessage.Text = App.L("catalog.loading");
        GalleryMessage.Visibility = Visibility.Visible;
        GalleryImage.Source = new BitmapImage(new Uri(image.Url)) { DecodePixelWidth = 1600 };
        GalleryCaption.Text = $"{_imageIndex + 1} / {_gallery.Count}" +
            (string.IsNullOrWhiteSpace(image.Title) ? "" : " · " + image.Title) +
            (string.IsNullOrWhiteSpace(image.Description) ? "" : " — " + image.Description);
        for (var i = 0; i < GalleryThumbnails.Children.Count; i++)
        {
            var button = (Button)GalleryThumbnails.Children[i];
            button.BorderBrush = Brush(i == _imageIndex ? "SelectionTextBrush" : "CardBorderBrush");
            button.BorderThickness = new Thickness(i == _imageIndex ? 2 : 1);
        }
    }

    private void PreviousImage_Click(object sender, RoutedEventArgs e) => ShowImage(_imageIndex - 1);
    private void NextImage_Click(object sender, RoutedEventArgs e) => ShowImage(_imageIndex + 1);
    private void GalleryImage_Opened(object sender, RoutedEventArgs e) => GalleryMessage.Visibility = Visibility.Collapsed;
    private void GalleryImage_Failed(object sender, ExceptionRoutedEventArgs e)
    {
        GalleryMessage.Text = App.L("catalog.image_error");
        GalleryMessage.Visibility = Visibility.Visible;
    }
    private void Description_Click(object sender, RoutedEventArgs e) => ShowDescription();
    private void ShowDescription()
    {
        DescriptionPanel.Visibility = Visibility.Visible;
        GalleryPanel.Visibility = Visibility.Collapsed;
        DescriptionTab.Background = Brush("SelectionBrush");
        DescriptionTab.Foreground = Brush("SelectionTextBrush");
        GalleryTab.Background = Brush("CardBrush");
        GalleryTab.Foreground = Brush("PrimaryBrush");
    }
    private void Gallery_Click(object sender, RoutedEventArgs e)
    {
        if (!_galleryBuilt) BuildGallery();
        DescriptionPanel.Visibility = Visibility.Collapsed;
        GalleryPanel.Visibility = Visibility.Visible;
        DescriptionTab.Background = Brush("CardBrush");
        DescriptionTab.Foreground = Brush("PrimaryBrush");
        GalleryTab.Background = Brush("SelectionBrush");
        GalleryTab.Foreground = Brush("SelectionTextBrush");
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e) => await RenderDescriptionAsync();
    private void Page_Unloaded(object sender, RoutedEventArgs e)
    {
        _descriptionCancellation?.Cancel();
        _descriptionView?.Close();
        if (_descriptionView != null) DescriptionHost.Children.Remove(_descriptionView);
        _descriptionView = null;
    }

    private async Task RenderDescriptionAsync()
    {
        if (!_active || !IsLoaded || _project == null || _creatingWebView) return;
        _creatingWebView = true;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        _descriptionCancellation = cancellation;
        DescriptionFallback.Text = App.L("catalog.loading");
        DescriptionFallback.Visibility = Visibility.Visible;
        try
        {
            if (_descriptionView == null)
            {
                var view = new WebView2 { Opacity = 0 };
                _descriptionView = view;
                var attached = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                void OnLoaded(object sender, RoutedEventArgs args) => attached.TrySetResult(true);
                view.Loaded += OnLoaded;
                try
                {
                    DescriptionHost.Children.Add(view);
                    if (!view.IsLoaded) await attached.Task.WaitAsync(cancellation.Token);
                }
                finally { view.Loaded -= OnLoaded; }
                var environment = await CoreWebView2Environment.CreateWithOptionsAsync(null,
                    Path.Combine(LauncherPaths.DataDirectory, "catalog-webview"), new CoreWebView2EnvironmentOptions()).AsTask(cancellation.Token);
                if (!_active || _descriptionView != view) return;
                await view.EnsureCoreWebView2Async(environment).AsTask(cancellation.Token);
                if (!_active || _descriptionView != view) return;
                var core = view.CoreWebView2;
                core.Settings.IsScriptEnabled = false;
                core.Settings.AreHostObjectsAllowed = false;
                core.Settings.AreDefaultScriptDialogsEnabled = false;
                core.Settings.IsWebMessageEnabled = false;
                core.Settings.IsPasswordAutosaveEnabled = false;
                core.Settings.IsGeneralAutofillEnabled = false;
                core.Settings.AreDevToolsEnabled = false;
                core.Settings.AreDefaultContextMenusEnabled = false;
                core.PermissionRequested += (_, args) => args.State = CoreWebView2PermissionState.Deny;
                core.DownloadStarting += (_, args) => args.Cancel = true;
                core.NewWindowRequested += async (_, args) =>
                {
                    args.Handled = true;
                    if (args.IsUserInitiated) await OpenLinkAsync(args.Uri);
                };
                core.NavigationStarting += async (_, args) =>
                {
                    if (_descriptionView != view) { args.Cancel = true; return; }
                    if (_pendingDescriptionUri != null && !args.IsUserInitiated &&
                        (args.Uri == _pendingDescriptionUri || args.Uri == "about:blank"))
                    {
                        _pendingDescriptionUri = null;
                        _descriptionNavigationId = args.NavigationId;
                        return;
                    }
                    if (args.Uri == "about:blank" || args.Uri.StartsWith("about:blank#", StringComparison.Ordinal)) return;
                    args.Cancel = true;
                    if (args.IsUserInitiated) await OpenLinkAsync(args.Uri);
                };
                core.DOMContentLoaded += (_, args) =>
                {
                    if (_descriptionView == view && args.NavigationId == _descriptionNavigationId)
                        _descriptionLoaded?.TrySetResult(true);
                };
                core.NavigationCompleted += (_, args) =>
                {
                    if (_descriptionView != view || args.NavigationId != _descriptionNavigationId) return;
                    if (args.IsSuccess) _descriptionLoaded?.TrySetResult(true);
                    else _descriptionLoaded?.TrySetException(new InvalidOperationException("Description navigation failed: " + args.WebErrorStatus));
                };
            }
            var descriptionView = _descriptionView;
            descriptionView.Opacity = 0;
            var html = ProjectDescription.Render(
                string.IsNullOrWhiteSpace(_project.Body) ? _project.Description ?? App.L("catalog.no_description") : _project.Body,
                ActualTheme == ElementTheme.Dark);
            _pendingDescriptionUri = ProjectDescription.GetNavigationUri(html);
            _descriptionNavigationId = null;
            _descriptionLoaded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            descriptionView.NavigateToString(html);
            await _descriptionLoaded.Task.WaitAsync(cancellation.Token);
            if (!_active || _descriptionView != descriptionView) return;
            descriptionView.Opacity = 1;
            DescriptionFallback.Visibility = Visibility.Collapsed;
        }
        catch (OperationCanceledException) when (!_active) { }
        catch (Exception ex)
        {
            _descriptionView?.Close();
            if (_descriptionView != null) DescriptionHost.Children.Remove(_descriptionView);
            _descriptionView = null;
            DescriptionFallback.Visibility = Visibility.Visible;
            DescriptionFallback.Text = App.L("catalog.description_error");
            RetryButton.Visibility = Visibility.Visible;
            try
            {
                var log = Path.Combine(LauncherPaths.DataDirectory, "logs", "catalog.log");
                Directory.CreateDirectory(Path.GetDirectoryName(log)!);
                File.AppendAllText(log, $"{DateTimeOffset.Now:O} Description: {ex.GetType().Name}: {ex.Message}{Environment.NewLine}");
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        finally
        {
            _pendingDescriptionUri = null;
            _descriptionNavigationId = null;
            _descriptionLoaded = null;
            _descriptionCancellation = null;
            _creatingWebView = false;
        }
    }

    private void Body_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (InstallScroll == null) return;
        var compact = e.NewSize.Width < 700;
        BodyGrid.ColumnDefinitions[1].Width = compact ? new GridLength(0) : new GridLength(280);
        BodyGrid.RowDefinitions[0].Height = compact ? GridLength.Auto : new GridLength(1, GridUnitType.Star);
        BodyGrid.RowDefinitions[1].Height = compact ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
        Grid.SetColumn(InstallScroll, compact ? 0 : 1);
        Grid.SetRow(DetailsPanel, compact ? 1 : 0);
        InstallScroll.MaxHeight = compact ? 220 : double.PositiveInfinity;
    }

    private async Task OpenLinkAsync(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "https" && uri.Scheme != "http")) return;
        try { await Windows.System.Launcher.LaunchUriAsync(uri); }
        catch (Exception) { InstallStatus.Text = App.L("catalog.link_error"); }
    }
    private static bool TryHttps(string? value, out Uri uri)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var result) && result.Scheme == "https") { uri = result; return true; }
        uri = null!;
        return false;
    }
    private void Back_Click(object sender, RoutedEventArgs e) { if (Frame.CanGoBack) Frame.GoBack(); }
    private async void Website_Click(object sender, RoutedEventArgs e)
    {
        if (_request != null) await OpenLinkAsync("https://modrinth.com/" +
            (ContentType == "datapack" ? "datapack" : ContentType) + "/" + Uri.EscapeDataString(_project?.Slug ?? _request.Project.Slug));
    }
    private async void Retry_Click(object sender, RoutedEventArgs e) => await Task.WhenAll(LoadProjectAsync(), LoadVersionsAsync());
}
