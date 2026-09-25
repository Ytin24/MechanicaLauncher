using System.Diagnostics;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using MechanicaLauncher.Core.Instances;
using MechanicaLauncher.Core.Security;
using MechanicaLauncher.Helpers;
using MechanicaLauncher.Views;
using System.Numerics;
using WinRT.Interop;

namespace MechanicaLauncher;

public sealed partial class MainWindow : Window
{
    private TLauncherScanResult? _scanResult;
    private bool _closeWhenIdle;

    public MainWindow()
    {
        this.Closed += (_, args) =>
        {
            if (App.LaunchPreparationGate.CurrentCount == 0 || App.Downloads.HasPending)
            {
                args.Handled = true;
                _closeWhenIdle = true;
                App.Downloads.CancelAll();
                App.PreparationCancellation?.Cancel();
                return;
            }
            if (App.HasRunningInstances())
            {
                args.Handled = true;
                App.HideWindow();
            }
        };

        this.InitializeComponent();
        ApplyLocale();
        this.SystemBackdrop = new MicaBackdrop { Kind = MicaKind.BaseAlt };
        this.ExtendsContentIntoTitleBar = true;

        var hwnd = WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
        var appWindow = AppWindow.GetFromWindowId(windowId);
        appWindow.Resize(new Windows.Graphics.SizeInt32(1100, 720));
        appWindow.Title = "Mechanica Launcher";

        var presenter = appWindow.Presenter as OverlappedPresenter;
        if (presenter != null)
        {
            presenter.IsMinimizable = true;
            presenter.IsMaximizable = true;
            presenter.IsResizable = true;
        }

        this.SizeChanged += (_, _) =>
        {
            var size = appWindow.Size;
            if (size.Width < 800 || size.Height < 500)
                appWindow.Resize(new Windows.Graphics.SizeInt32(
                    Math.Max(size.Width, 800),
                    Math.Max(size.Height, 500)));
        };

        if (Content is FrameworkElement root)
        {
            void UpdateTitleBar()
            {
                var foreground = root.ActualTheme == ElementTheme.Light ? Colors.Black : Colors.White;
                appWindow.TitleBar.ButtonForegroundColor = foreground;
                appWindow.TitleBar.ButtonHoverForegroundColor = foreground;
                appWindow.TitleBar.ButtonPressedForegroundColor = foreground;
                appWindow.TitleBar.ButtonInactiveForegroundColor = Windows.UI.Color.FromArgb(0x80, foreground.R, foreground.G, foreground.B);
                appWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
                appWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
                appWindow.TitleBar.ButtonHoverBackgroundColor = Windows.UI.Color.FromArgb(0x18, foreground.R, foreground.G, foreground.B);
                appWindow.TitleBar.ButtonPressedBackgroundColor = Windows.UI.Color.FromArgb(0x28, foreground.R, foreground.G, foreground.B);
            }

            root.ActualThemeChanged += (_, _) => UpdateTitleBar();
            root.RequestedTheme = App.Settings.Theme switch
            {
                "Light" or "Светлая" => ElementTheme.Light,
                "Dark" or "Тёмная" => ElementTheme.Dark,
                _ => ElementTheme.Default
            };
            UpdateTitleBar();
        }

        ContentFrame.CacheSize = 5;
        ContentFrame.ContentTransitions = new TransitionCollection
        {
            new NavigationThemeTransition { DefaultNavigationTransitionInfo = new SuppressNavigationTransitionInfo() }
        };
        ContentFrame.Navigated += (_, _) =>
        {
            if (ContentFrame.Content is UIElement page) AnimationHelper.SlideIn(page);
        };

        App.RunningInstancesChanged += OnRunningChanged;
        App.Downloads.Changed += OnRunningChanged;
        this.Closed += (_, args) =>
        {
            if (!args.Handled)
            {
                App.RunningInstancesChanged -= OnRunningChanged;
                App.Downloads.Changed -= OnRunningChanged;
                App.Discord.Dispose();
            }
        };

        _scanResult = TLauncherDetector.Scan();
        if (_scanResult.IsDetected)
        {
            ShowTLauncherLockscreen();
        }
        else
        {
            ApplyEventNavigation();
            ContentFrame.Navigate(typeof(HomePage));
            if (App.PendingEvent != null)
                _ = HandlePendingEventAsync();
            else if (App.PendingConnect != null)
                _ = HandlePendingConnectAsync();
            else if (App.PendingMrpack != null)
                _ = ImportMrpackAsync(App.PendingMrpack);
        }
    }

    public void HandlePendingConnect() => _ = HandlePendingConnectAsync();
    public void LaunchServer(string instanceId, string? host, int port)
    {
        NavigateToTag("Home");
        if (ContentFrame.Content is not HomePage) ContentFrame.Navigate(typeof(HomePage));
        if (ContentFrame.Content is HomePage home) home.LaunchWithServer(instanceId, host, port);
    }
    internal void ShowInstanceDetails(string instanceId, string tab = "settings")
    {
        NavigateToTag("Instances");
        ContentFrame.Navigate(typeof(InstanceDetailsPage), new InstanceDetailsRequest(instanceId, tab));
    }
    public Task HandlePendingEventPublicAsync() => HandlePendingEventAsync();

    public void ApplyLocale()
    {
        foreach (var item in NavView.MenuItems.Concat(NavView.FooterMenuItems).OfType<NavigationViewItem>())
        {
            if (item.Tag is string tag)
                item.Content = App.L("nav." + tag.ToLowerInvariant());
        }
    }

    public void NavigateToTag(string tag)
    {
        var item = NavView.MenuItems.OfType<NavigationViewItem>()
            .Concat(NavView.FooterMenuItems.OfType<NavigationViewItem>())
            .FirstOrDefault(i => (i.Tag as string) == tag);
        if (item != null) NavView.SelectedItem = item;
    }

    private void OnRunningChanged()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            var downloads = App.Downloads.Jobs.Count(j => j.State is Core.IO.DownloadState.Queued or Core.IO.DownloadState.Running);
            DownloadsBadge.Value = downloads;
            DownloadsBadge.Visibility = downloads > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (_closeWhenIdle && App.LaunchPreparationGate.CurrentCount > 0 && !App.Downloads.HasPending)
            {
                _closeWhenIdle = false;
                Close();
                return;
            }
            var count = App.RunningInstances.Count(kv => !kv.Value.HasExited);
            RunningBadge.Value = count;
            RunningBadge.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        });
    }

    // Drag-drop: any .mrpack dropped on the window becomes an import.
    private void Root_DragOver(object sender, Microsoft.UI.Xaml.DragEventArgs e)
    {
        e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
        e.DragUIOverride.Caption = "Import modpack";
        e.DragUIOverride.IsCaptionVisible = true;
        e.DragUIOverride.IsGlyphVisible = true;
    }

    private async void Root_Drop(object sender, Microsoft.UI.Xaml.DragEventArgs e)
    {
        if (!e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems)) return;
        var items = await e.DataView.GetStorageItemsAsync();
        var mrpack = items.OfType<Windows.Storage.StorageFile>()
                          .FirstOrDefault(f => f.FileType.Equals(".mrpack", StringComparison.OrdinalIgnoreCase));
        if (mrpack != null)
            await ImportMrpackAsync(mrpack.Path);
    }

    public async Task ImportMrpackAsync(string mrpackPath)
    {
        App.PendingMrpack = null;
        var job = App.Downloads.Enqueue(App.L("inst.import") + ": " + Path.GetFileName(mrpackPath), null, async token =>
        {
            await App.LaunchPreparationGate.WaitAsync(token);
            var cancellation = Core.IO.DownloadQueue.CurrentCancellation!;
            try
            {
                if (App.EventConfig?.Ui?.AllowModInstall == false) throw new InvalidOperationException(App.L("catalog.install_locked"));
                App.PreparationCancellation = cancellation;
                App.PreparingInstanceId = "modpack-import-" + Guid.NewGuid().ToString("N");
                App.NotifyRunningChanged();
                var created = await new Core.Mods.ModpackInstaller().ImportAsync(mrpackPath, new InstanceManager(), token);
                Core.IO.DownloadQueue.Current!.ResultInstanceId = created.Id;
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
        NavigateToTag("Downloads");
        await job.Completion;
    }

    public void ApplyEventNavigation()
    {
        var ui = App.EventConfig?.Ui;
        if (ui == null) return;

        foreach (var item in NavView.MenuItems.OfType<NavigationViewItem>())
        {
            var tag = item.Tag?.ToString();
            item.Visibility = tag switch
            {
                "Instances" => ui.ShowInstances ? Visibility.Visible : Visibility.Collapsed,
                "Mods" => ui.ShowMods ? Visibility.Visible : Visibility.Collapsed,
                _ => Visibility.Visible
            };
        }

        foreach (var item in NavView.FooterMenuItems.OfType<NavigationViewItem>())
        {
            var tag = item.Tag?.ToString();
            item.Visibility = tag switch
            {
                "Account" => ui.ShowAccount ? Visibility.Visible : Visibility.Collapsed,
                "Settings" => ui.ShowSettings ? Visibility.Visible : Visibility.Collapsed,
                _ => Visibility.Visible
            };
        }

        if (App.EventConfig?.Branding?.Title != null)
        {
            var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(
                Microsoft.UI.Win32Interop.GetWindowIdFromWindow(
                    WinRT.Interop.WindowNative.GetWindowHandle(this)));
            appWindow.Title = App.EventConfig.Branding.Title;
        }
    }

    private async Task HandlePendingEventAsync()
    {
        var eventReq = App.PendingEvent;
        if (eventReq == null) return;
        App.PendingEvent = null;

        try
        {
            await App.LoadEventAsync(eventReq.ConfigUrl);
            ApplyEventNavigation();

            // Create instance for event if needed
            var config = App.EventConfig;
            if (config?.Minecraft != null)
            {
                var im = new InstanceManager();
                var existing = im.GetAllInstances()
                    .FirstOrDefault(i => i.McVersion == config.Minecraft.Version);

                if (existing == null)
                {
                    var loader = config.Minecraft.Loader?.ToLowerInvariant() switch
                    {
                        "fabric" => Core.Instances.LoaderType.Fabric,
                        "quilt" => Core.Instances.LoaderType.Quilt,
                        "forge" => Core.Instances.LoaderType.Forge,
                        "neoforge" => Core.Instances.LoaderType.NeoForge,
                        _ => Core.Instances.LoaderType.None
                    };
                    existing = im.CreateInstance(
                        config.Name, config.Minecraft.Version,
                        loader, config.Minecraft.LoaderVersion);
                }

                App.Settings.SelectedInstanceId = existing.Id;
                App.Settings.Save();

                // Sync mods
                if (config.Mods != null)
                {
                    var modsDir = Path.Combine(im.GetGameDir(existing.Id), "mods");
                    var syncer = new Core.Config.ModSyncer();
                    var syncResult = await syncer.SyncAsync(config, modsDir);
                    if (syncResult.HasFailures)
                        Debug.WriteLine($"Mod sync failures: {string.Join(", ", syncResult.FailedMods)}");
                }
            }

            ContentFrame.Navigate(typeof(HomePage));
        }
        catch { }
    }

    private async Task HandlePendingConnectAsync()
    {
        var connect = App.PendingConnect;
        if (connect == null) return;
        App.PendingConnect = null;

        var im = new InstanceManager();
        var instances = im.GetAllInstances();
        var match = instances.FirstOrDefault(i => i.McVersion == connect.Version);

        // If MC running — use overlay popup instead
        if (App.HasRunningInstances())
        {
            OverlayPopup.Show(connect);
            return;
        }

        if (match == null)
        {
            var dialog = new ContentDialog
            {
                Title = $"Подключение к {connect.Server}",
                Content = $"Для подключения нужен Minecraft {connect.Version}.\nУ вас нет подходящего инстанса.",
                PrimaryButtonText = $"Создать Vanilla {connect.Version}",
                CloseButtonText = "Отмена",
                XamlRoot = Content.XamlRoot
            };

            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

            match = im.CreateInstance($"Server ({connect.Version})", connect.Version);
        }

        App.Settings.SelectedInstanceId = match.Id;
        App.Settings.Save();

        // Navigate to home and trigger play with server
        ContentFrame.Navigate(typeof(HomePage));

        if (ContentFrame.Content is HomePage home)
        {
            home.LaunchWithServer(match.Id, connect.Server, connect.Port);
        }
    }

    private void ShowTLauncherLockscreen()
    {
        TLauncherOverlay.Visibility = Visibility.Visible;
        NavView.IsEnabled = false;

        TLTitle.Text = App.L("tl.title");
        TLDesc.Text = App.L("tl.desc");
        TLThreatCount.Text = App.L("tl.threats", _scanResult!.ThreatCount);
        TLAcceptBtn.Content = App.L("tl.accept");
        TLDeclineBtn.Content = App.L("tl.decline");

        StartTitleDistortion();
        StartTitleColorShimmer();
        StartButtonTimer();
    }

    private void StartTitleDistortion()
    {
        var sb = new Storyboard();
        var anim = new DoubleAnimationUsingKeyFrames();
        anim.KeyFrames.Add(new EasingDoubleKeyFrame { KeyTime = TimeSpan.FromMilliseconds(0), Value = 0 });
        anim.KeyFrames.Add(new EasingDoubleKeyFrame { KeyTime = TimeSpan.FromMilliseconds(400), Value = 12,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } });
        anim.KeyFrames.Add(new EasingDoubleKeyFrame { KeyTime = TimeSpan.FromMilliseconds(800), Value = 0,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } });
        anim.KeyFrames.Add(new EasingDoubleKeyFrame { KeyTime = TimeSpan.FromMilliseconds(1200), Value = -12,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } });
        anim.KeyFrames.Add(new EasingDoubleKeyFrame { KeyTime = TimeSpan.FromMilliseconds(1600), Value = 0,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } });
        anim.RepeatBehavior = RepeatBehavior.Forever;
        Storyboard.SetTarget(anim, TLTitleRotation);
        Storyboard.SetTargetProperty(anim, "Angle");
        sb.Children.Add(anim);
        sb.Begin();
    }

    private void StartTitleColorShimmer()
    {
        var sb = new Storyboard();
        var colorAnim = new ColorAnimationUsingKeyFrames();
        colorAnim.KeyFrames.Add(new LinearColorKeyFrame { KeyTime = TimeSpan.FromMilliseconds(0), Value = Windows.UI.Color.FromArgb(0xFF, 0xFF, 0x44, 0x44) });
        colorAnim.KeyFrames.Add(new LinearColorKeyFrame { KeyTime = TimeSpan.FromMilliseconds(1000), Value = Windows.UI.Color.FromArgb(0xFF, 0xFF, 0x88, 0x00) });
        colorAnim.KeyFrames.Add(new LinearColorKeyFrame { KeyTime = TimeSpan.FromMilliseconds(2000), Value = Windows.UI.Color.FromArgb(0xFF, 0xFF, 0x44, 0x44) });
        colorAnim.RepeatBehavior = RepeatBehavior.Forever;
        Storyboard.SetTarget(colorAnim, TLTitle);
        Storyboard.SetTargetProperty(colorAnim, "(TextBlock.Foreground).(SolidColorBrush.Color)");
        TLTitle.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xFF, 0x44, 0x44));
        sb.Children.Add(colorAnim);
        sb.Begin();
    }

    private async void StartButtonTimer()
    {
        for (int i = 20; i > 0; i--)
        {
            TLTimer.Text = App.L("tl.timer", i);
            await Task.Delay(1000);
        }
        TLTimer.Text = "";
        TLAcceptBtn.IsEnabled = true;
        TLDeclineBtn.IsEnabled = true;
    }

    private async void TLAccept_Click(object sender, RoutedEventArgs e)
    {
        TLAcceptBtn.Visibility = Visibility.Collapsed;
        TLDeclineBtn.Visibility = Visibility.Collapsed;
        TLTimer.Visibility = Visibility.Collapsed;
        TLProgress.Visibility = Visibility.Visible;
        TLProgressBar.IsIndeterminate = false;
        TLProgressBar.Value = 0;

        void UpdateUI(string key, double progress)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                TLProgressText.Text = App.L(key);
                TLProgressBar.Value = progress;
            });
        }

        var im = new InstanceManager();

        if (_scanResult?.MinecraftDir != null)
        {
            UpdateUI("tl.scanning", 5);
            await Task.Delay(300);

            var migrator = new TLauncherMigrator(im);
            migrator.StatusChanged += s =>
                DispatcherQueue.TryEnqueue(() => TLProgressText.Text = s);

            UpdateUI("tl.migrating", 10);
            await Task.Run(() => migrator.MigrateAsync(_scanResult.MinecraftDir));
            UpdateUI("tl.migration_done", 60);
            await Task.Delay(300);
        }

        UpdateUI("tl.removing", 65);
        TLauncherCleaner.StatusChanged += s =>
            DispatcherQueue.TryEnqueue(() => TLProgressText.Text = s);
        await Task.Run(TLauncherCleaner.Clean);

        UpdateUI("tl.registry", 85);
        await Task.Delay(300);

        UpdateUI("tl.almost", 95);
        await Task.Delay(300);

        UpdateUI("tl.done", 100);
        await Task.Delay(1500);

        TLauncherOverlay.Visibility = Visibility.Collapsed;
        NavView.IsEnabled = true;
        ContentFrame.Navigate(typeof(HomePage));
    }

    private void TLDecline_Click(object sender, RoutedEventArgs e)
    {
        App.MainWindow.Close();
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItemContainer is NavigationViewItem item)
        {
            var tag = item.Tag?.ToString();
            var pageType = tag switch
            {
                "Home" => typeof(HomePage),
                "Instances" => typeof(InstancesPage),
                "Mods" => typeof(ModsPage),
                "Downloads" => typeof(DownloadsPage),
                "Servers" => typeof(ServersPage),
                "Account" => typeof(AccountPage),
                "Settings" => typeof(SettingsPage),
                _ => typeof(HomePage)
            };
            ContentFrame.Navigate(pageType);
        }
    }
}
