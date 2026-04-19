using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using MechanicaLauncher.Core.Auth;
using MechanicaLauncher.Core.Game;
using MechanicaLauncher.Core.Instances;
using MechanicaLauncher.Core.Models;
using MechanicaLauncher.Core.Profiles;
using MechanicaLauncher.Helpers;

namespace MechanicaLauncher.Views;

public sealed partial class HomePage : Page
{
    private static LauncherSettings S => App.Settings;
    private readonly InstanceManager _im = new();
    private VersionManager _vm = null!;
    private VersionManifest? _manifest;
    private bool _loading;
    private bool _logAutoScroll = true;
    private readonly HashSet<string> _killedByUser = new();

    public HomePage()
    {
        this.InitializeComponent();
        _vm = new VersionManager(_im.SharedDir);
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        InstanceManager.InstancesChanged += OnInstancesChanged;
        App.RunningInstancesChanged += OnInstancesChanged;
        _ = LoadAsync();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        InstanceManager.InstancesChanged -= OnInstancesChanged;
        App.RunningInstancesChanged -= OnInstancesChanged;
    }

    private void OnInstancesChanged() =>
        DispatcherQueue.TryEnqueue(RefreshInstancesUi);

    private bool IsInstanceRunning(string id) =>
        App.RunningInstances.TryGetValue(id, out var p) && !p.HasExited;

    private void RefreshInstancesUi()
    {
        _loading = true;
        var instances = _im.GetAllInstances();
        InstanceCountText.Text = instances.Count.ToString();

        var previousId = (ProfileSelector.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? S.SelectedInstanceId;
        ProfileSelector.Items.Clear();

        foreach (var inst in instances)
        {
            var running = IsInstanceRunning(inst.Id);
            var prefix = running ? "▶  " : "";
            var label = inst.Loader != LoaderType.None
                ? $"{prefix}{inst.Name}  ·  {inst.McVersion}  ·  {inst.Loader}"
                : $"{prefix}{inst.Name}  ·  {inst.McVersion}";
            ProfileSelector.Items.Add(new ComboBoxItem { Content = label, Tag = inst.Id });
        }

        if (ProfileSelector.Items.Count > 0)
        {
            var idx = 0;
            if (previousId != null)
                for (int i = 0; i < ProfileSelector.Items.Count; i++)
                    if ((ProfileSelector.Items[i] as ComboBoxItem)?.Tag?.ToString() == previousId)
                    { idx = i; break; }
            ProfileSelector.SelectedIndex = idx;
        }

        _loading = false;
        UpdatePlayButton();
        UpdateInstanceInfo();
        RebuildRecentChips(instances);
        RebuildInstancePickerFlyout(instances);
        UpdateHeroCard();
    }

    private void UpdateHeroCard()
    {
        var inst = GetSelectedInstance();
        if (inst == null)
        {
            HeroName.Text = "No instance";
            HeroIconGrid.Children.Clear();
            HeroIconGrid.Children.Add(new FontIcon { Glyph = "\uE74C", FontSize = 22, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
            HeroIconBorder.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
            return;
        }
        HeroName.Text = inst.Name;

        HeroIconGrid.Children.Clear();
        var iconPath = _im.GetIconAbsolutePath(inst);
        if (iconPath != null)
        {
            HeroIconGrid.Children.Add(new Image
            {
                Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(iconPath)) { CreateOptions = Microsoft.UI.Xaml.Media.Imaging.BitmapCreateOptions.IgnoreImageCache },
                Stretch = Microsoft.UI.Xaml.Media.Stretch.UniformToFill,
            });
            HeroIconBorder.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
        }
        else
        {
            var (glyph, accent) = LoaderIconFor(inst.Loader);
            HeroIconGrid.Children.Add(new FontIcon { Glyph = glyph, FontSize = 22, Foreground = new SolidColorBrush(accent), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
            HeroIconBorder.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x1A, accent.R, accent.G, accent.B));
        }
    }

    private static (string Glyph, Windows.UI.Color Color) LoaderIconFor(LoaderType loader) => loader switch
    {
        LoaderType.Fabric   => ("\uE74C", Windows.UI.Color.FromArgb(0xFF, 0x4C, 0xAF, 0x50)),
        LoaderType.Quilt    => ("\uE74C", Windows.UI.Color.FromArgb(0xFF, 0xAB, 0x47, 0xBC)),
        LoaderType.Forge    => ("\uE74C", Windows.UI.Color.FromArgb(0xFF, 0xFF, 0x98, 0x00)),
        LoaderType.NeoForge => ("\uE74C", Windows.UI.Color.FromArgb(0xFF, 0xE6, 0x5C, 0x00)),
        _                   => ("\uE74C", Windows.UI.Color.FromArgb(0xFF, 0x78, 0x90, 0x9C)),
    };

    private void RebuildInstancePickerFlyout(List<Core.Instances.GameInstance> instances)
    {
        InstancePickerFlyout.Items.Clear();
        foreach (var inst in instances)
        {
            var item = new MenuFlyoutItem { Text = $"{inst.Name}   ·   {inst.McVersion}" + (inst.Loader != LoaderType.None ? $" · {inst.Loader}" : "") };
            var iconPath = _im.GetIconAbsolutePath(inst);
            if (iconPath != null)
            {
                // MenuFlyoutItem.Icon only accepts IconElement subclasses; use ImageIcon to show the instance icon.
                item.Icon = new ImageIcon
                {
                    Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(iconPath)),
                };
            }
            else
            {
                var (glyph, color) = LoaderIconFor(inst.Loader);
                item.Icon = new FontIcon { Glyph = glyph, Foreground = new SolidColorBrush(color) };
            }
            var id = inst.Id;
            item.Click += (_, _) =>
            {
                for (int i = 0; i < ProfileSelector.Items.Count; i++)
                    if ((ProfileSelector.Items[i] as ComboBoxItem)?.Tag?.ToString() == id)
                    { ProfileSelector.SelectedIndex = i; break; }
            };
            InstancePickerFlyout.Items.Add(item);
        }
    }

    private void RebuildRecentChips(List<Core.Instances.GameInstance> instances)
    {
        RecentChips.Children.Clear();
        if (instances.Count <= 1)
        {
            RecentScroll.Visibility = Visibility.Collapsed;
            return;
        }

        // Quick-switch rail: sort by LastPlayed (or CreatedAt), take 6, but always include the currently
        // selected instance so a just-created one appears immediately.
        var ranked = instances
            .OrderByDescending(i => i.LastPlayed ?? i.CreatedAt)
            .Take(6)
            .ToList();
        if (S.SelectedInstanceId != null && ranked.All(i => i.Id != S.SelectedInstanceId))
        {
            var sel = instances.FirstOrDefault(i => i.Id == S.SelectedInstanceId);
            if (sel != null) ranked.Insert(0, sel);
        }

        RecentScroll.Visibility = Visibility.Visible;
        foreach (var inst in ranked)
        {
            var running = IsInstanceRunning(inst.Id);
            var isSelected = inst.Id == S.SelectedInstanceId;
            var btn = new Button
            {
                MinHeight = 30,
                Padding = new Thickness(12, 4, 12, 4),
                CornerRadius = new CornerRadius(16),
                Background = new SolidColorBrush(isSelected
                    ? Windows.UI.Color.FromArgb(0x40, 0x4C, 0xAF, 0x50)
                    : Windows.UI.Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)),
                BorderBrush = new SolidColorBrush(isSelected
                    ? Windows.UI.Color.FromArgb(0xAA, 0x4C, 0xAF, 0x50)
                    : Windows.UI.Color.FromArgb(0x00, 0, 0, 0)),
                BorderThickness = new Thickness(1),
            };
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            if (running)
                sp.Children.Add(new Microsoft.UI.Xaml.Shapes.Ellipse
                {
                    Width = 8, Height = 8,
                    Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x4C, 0xAF, 0x50)),
                    VerticalAlignment = VerticalAlignment.Center
                });
            sp.Children.Add(new TextBlock
            {
                Text = inst.Name,
                FontSize = 12,
                FontWeight = isSelected ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
                VerticalAlignment = VerticalAlignment.Center
            });
            sp.Children.Add(new TextBlock
            {
                Text = inst.McVersion,
                FontSize = 11,
                Opacity = 0.6,
                VerticalAlignment = VerticalAlignment.Center
            });
            btn.Content = sp;
            var id = inst.Id;
            btn.Click += (_, _) =>
            {
                for (int i = 0; i < ProfileSelector.Items.Count; i++)
                    if ((ProfileSelector.Items[i] as ComboBoxItem)?.Tag?.ToString() == id)
                    { ProfileSelector.SelectedIndex = i; break; }
            };
            RecentChips.Children.Add(btn);
        }
    }

    private async Task LoadAsync()
    {
        // Event mode branding
        if (App.IsEventMode && App.EventConfig?.Branding != null)
        {
            var b = App.EventConfig.Branding;
            if (b.SplashTexts?.Count > 0)
                SplashText.Text = b.SplashTexts[Random.Shared.Next(b.SplashTexts.Count)];
            else if (b.Subtitle != null)
                SplashText.Text = b.Subtitle;
            else
                SplashText.Text = SettingsPage.GetRandomSplash();
        }
        else
        {
            SplashText.Text = SettingsPage.GetRandomSplash();
        }

        ModsLabel.Text = App.L("home.mods");
        AccountLabel.Text = App.L("home.account");
        InstancesLabel.Text = App.L("home.instances");
        ProfileSelector.PlaceholderText = App.L("home.select_instance");

        var allJava = JavaFinder.FindAllJava();
        AccountText.Text = S.Username;

        RefreshInstancesUi();

        AnimationHelper.SlideIn(Card0, 80);
        AnimationHelper.SlideIn(Card1, 140);
        AnimationHelper.SlideIn(Card2, 200);
        AnimationHelper.AddCardHover(Card0);
        AnimationHelper.AddCardHover(Card1);
        AnimationHelper.AddCardHover(Card2);
        AnimationHelper.AddButtonSpring(PlayButton);
        AnimationHelper.StartBreathing(PlayButton);

        try
        {
            _manifest = await _vm.GetManifestAsync();
            var running = App.RunningInstances.Count(kv => !kv.Value.HasExited);
            var count = _im.GetAllInstances().Count;
            NotificationBar.Message = count > 0
                ? $"{count} instance(s){(running > 0 ? $", {running} running" : "")}. Latest MC: {_manifest.Latest.Release}"
                : "Create an instance in the Instances tab.";
        }
        catch (Exception ex)
        {
            NotificationBar.Severity = InfoBarSeverity.Warning;
            NotificationBar.Message = $"Offline — {ex.Message}";
        }

        _ = ShowUpdateNotificationAsync();
    }

    private async Task ShowUpdateNotificationAsync()
    {
        for (int i = 0; i < 10 && App.LatestUpdate == null; i++)
            await Task.Delay(500);

        if (App.LatestUpdate is { IsAvailable: true } update)
        {
            NotificationBar.Title = "Mechanica";
            NotificationBar.Message = $"Update v{update.LatestVersion} available!";
            NotificationBar.Severity = InfoBarSeverity.Informational;
            NotificationBar.IsOpen = true;
            NotificationBar.ActionButton = new HyperlinkButton
            {
                Content = App.L("set.download_update"),
                NavigateUri = new Uri(update.ReleaseUrl ?? "https://github.com/Ytin24/MechanicaLauncher/releases")
            };
        }
    }

    private void UpdateInstanceInfo()
    {
        var inst = GetSelectedInstance();
        if (inst == null)
        {
            InstanceInfo.Text = "";
            ModCountText.Text = "0";
            return;
        }

        var modsDir = Path.Combine(_im.GetGameDir(inst.Id), "mods");
        var modCount = Directory.Exists(modsDir) ? Directory.GetFiles(modsDir, "*.jar").Length : 0;
        ModCountText.Text = $"{modCount} active";

        var parts = new List<string> { inst.McVersion };
        if (inst.Loader != LoaderType.None)
            parts.Add($"{inst.Loader} {inst.LoaderVersion}");
        parts.Add($"{inst.MinMemoryMb}-{inst.MaxMemoryMb} MB");
        if (inst.LastPlayed.HasValue)
            parts.Add($"Last played {inst.LastPlayed.Value:MMM dd}");
        InstanceInfo.Text = string.Join("  ·  ", parts);
    }

    private GameInstance? GetSelectedInstance()
    {
        var id = (ProfileSelector.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        return id != null ? _im.GetInstance(id) : null;
    }

    private void ProfileSelector_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        var id = (ProfileSelector.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        if (id != null)
        {
            S.SelectedInstanceId = id;
            S.Save();
            UpdatePlayButton();
            UpdateInstanceInfo();
            UpdateHeroCard();
            // Recent pills also update to highlight the new selection.
            RebuildRecentChips(_im.GetAllInstances());
        }
    }

    // --- Navigation cards ---
    private void Card_Mods_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e) =>
        NavigateTo("Mods");
    private void Card_Account_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e) =>
        NavigateTo("Account");
    private void Card_Instances_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e) =>
        NavigateTo("Instances");

    private void NavigateTo(string tag)
    {
        if (this.Frame?.Parent is NavigationView nav)
        {
            foreach (var item in nav.MenuItems.Concat(nav.FooterMenuItems))
            {
                if (item is NavigationViewItem nvi && nvi.Tag?.ToString() == tag)
                {
                    nav.SelectedItem = nvi;
                    break;
                }
            }
        }
    }

    // --- Play / Kill ---
    private DispatcherTimer? _elapsedTimer;
    private TextBlock? _elapsedLabel;

    private void UpdatePlayButton()
    {
        var inst = GetSelectedInstance();
        var running = inst != null && IsInstanceRunning(inst.Id);

        PlayButton.IsEnabled = true;
        var label = running ? App.L("home.kill") : App.L("home.play");
        if (inst != null && !running)
        {
            if (App.IsEventMode && App.EventConfig?.Ui?.PlayButtonText != null)
                label = App.EventConfig.Ui.PlayButtonText;
            else
                label = App.L("home.play_with", inst.Name);
        }

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        row.Children.Add(new FontIcon
        {
            Glyph = running ? "\uE71A" : "\uE768", FontSize = 22,
            Foreground = new SolidColorBrush(running ? Windows.UI.Color.FromArgb(0xFF, 0xFF, 0x6B, 0x6B) : Microsoft.UI.Colors.White)
        });
        row.Children.Add(new TextBlock
        {
            Text = label, FontSize = 18,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        });

        _elapsedLabel = null;
        _elapsedTimer?.Stop();
        _elapsedTimer = null;

        if (running && inst != null && App.RunningInstances.TryGetValue(inst.Id, out var proc))
        {
            var startTime = proc.StartTime;
            _elapsedLabel = new TextBlock
            {
                FontSize = 13, Opacity = 0.85,
                VerticalAlignment = VerticalAlignment.Center,
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono,Consolas,monospace"),
            };
            UpdateElapsedText(startTime);
            row.Children.Add(new TextBlock
            {
                Text = "·", FontSize = 16, Opacity = 0.5,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0),
            });
            row.Children.Add(_elapsedLabel);

            _elapsedTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _elapsedTimer.Tick += (_, _) =>
            {
                if (!IsInstanceRunning(inst.Id)) { _elapsedTimer?.Stop(); return; }
                UpdateElapsedText(startTime);
            };
            _elapsedTimer.Start();
        }

        PlayButton.Content = row;
        PlayButton.Background = running
            ? new SolidColorBrush(Windows.UI.Color.FromArgb(0x33, 0xFF, 0x00, 0x00))
            : (Brush)Application.Current.Resources["AccentBrush"];
    }

    private void UpdateElapsedText(DateTime startTime)
    {
        if (_elapsedLabel == null) return;
        var elapsed = DateTime.Now - startTime;
        _elapsedLabel.Text = elapsed.TotalHours >= 1
            ? $"{(int)elapsed.TotalHours}:{elapsed.Minutes:D2}:{elapsed.Seconds:D2}"
            : $"{elapsed.Minutes:D2}:{elapsed.Seconds:D2}";
    }

    private string? _pendingServer;
    private int? _pendingPort;
    private bool _isReconnecting;

    public async void LaunchWithServer(string instanceId, string server, int port)
    {
        _pendingServer = server;
        _pendingPort = port;
        _isReconnecting = true;

        // Wait for LoadAsync to populate dropdown
        await LoadAsync();
        await Task.Delay(200);

        PlayButton_Click(this, new RoutedEventArgs());
    }

    private async void PlayButton_Click(object sender, RoutedEventArgs e)
    {
        var instance = GetSelectedInstance();
        if (instance == null) { ShowNotification(InfoBarSeverity.Warning, "Select an instance."); return; }

        if (IsInstanceRunning(instance.Id))
        {
            _killedByUser.Add(instance.Id);
            if (App.RunningInstances.TryGetValue(instance.Id, out var p))
            {
                try { p.Kill(); } catch { }
                App.RunningInstances.TryRemove(instance.Id, out _);
                App.NotifyRunningChanged();
            }
            UpdatePlayButton();
            ShowNotification(InfoBarSeverity.Informational, $"{instance.Name} killed.");
            return;
        }

        PlayButton.IsEnabled = false;
        var originalContent = PlayButton.Content;
        PlayButton.Content = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 12,
            Children =
            {
                new ProgressRing { IsActive = true, Width = 22, Height = 22, Foreground = new SolidColorBrush(Microsoft.UI.Colors.White) },
                new TextBlock { Text = App.L("home.loading"), FontSize = 18, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center }
            }
        };
        S.SelectedInstanceId = instance.Id;
        S.Save();
        ProgressPanel.Visibility = Visibility.Visible;

        try
        {
            var gameDir = _im.GetGameDir(instance.Id);
            var versionId = instance.GetEffectiveVersionId();
            var isModded = instance.Loader != LoaderType.None;

            if (S.AuthMode == "microsoft" && S.AccessToken != "0")
            {
                ProgressText.Text = "Validating session...";
                DownloadProgress.IsIndeterminate = true;
                if (!await MicrosoftAuth.ValidateTokenAsync(S.AccessToken))
                {
                    if (!string.IsNullOrEmpty(S.MsRefreshToken))
                    {
                        try
                        {
                            ProgressText.Text = "Refreshing session...";
                            var refreshed = await new MicrosoftAuth().RefreshAsync(S.MsRefreshToken);
                            S.Username = refreshed.Username;
                            S.Uuid = refreshed.Uuid;
                            S.AccessToken = refreshed.AccessToken;
                            S.MsRefreshToken = refreshed.RefreshToken ?? S.MsRefreshToken;
                            S.Save();
                        }
                        catch
                        {
                            S.MsRefreshToken = "";
                            S.Save();
                            ShowNotification(InfoBarSeverity.Warning, App.L("acc.session_expired"));
                            return;
                        }
                    }
                    else
                    {
                        ShowNotification(InfoBarSeverity.Warning, App.L("acc.session_expired"));
                        return;
                    }
                }
            }

            ProgressText.Text = "Loading version...";
            DownloadProgress.IsIndeterminate = true;
            _manifest ??= await _vm.GetManifestAsync();

            var vanillaEntry = _manifest.Versions.FirstOrDefault(v => v.Id == instance.McVersion);
            if (vanillaEntry == null) { ShowNotification(InfoBarSeverity.Error, $"MC {instance.McVersion} not found."); return; }

            var vanillaMeta = await _vm.GetVersionMetaAsync(vanillaEntry);

            var javaComponent = vanillaMeta.JavaVersion?.Component ?? "java-runtime-delta";
            var javaPath = !string.IsNullOrEmpty(instance.JavaPath) && File.Exists(instance.JavaPath)
                ? instance.JavaPath : JavaFinder.FindJava(javaComponent);

            if (javaPath == null)
            {
                ProgressText.Text = $"Downloading Java ({javaComponent})...";
                javaPath = await JavaFinder.DownloadJavaAsync(javaComponent, _im.SharedDir,
                    status => DispatcherQueue.TryEnqueue(() => ProgressText.Text = status));
                if (javaPath == null) { await ShowRepairDialogAsync(instance.Id, -1); return; }
            }

            var vanillaJar = Path.Combine(gameDir, "versions", instance.McVersion, $"{instance.McVersion}.jar");
            if (!File.Exists(vanillaJar) || new FileInfo(vanillaJar).Length == 0)
            {
                ProgressText.Text = "Downloading game...";
                var dl = new AssetDownloader(_im.SharedDir, gameDir);
                dl.ProgressChanged += (s, p) => DispatcherQueue.TryEnqueue(() =>
                {
                    ProgressText.Text = s;
                    if (p >= 0) { DownloadProgress.IsIndeterminate = false; DownloadProgress.Value = p; }
                });
                await Task.Run(() => dl.DownloadVersionAsync(vanillaMeta));
            }

            // Run the loader installer on first launch (instance create is lightweight and the
            // patched/remapped Minecraft jars only exist after processors run).
            if (isModded && !string.IsNullOrEmpty(instance.LoaderVersion))
            {
                var loaderVersionJson = Path.Combine(gameDir, "versions", versionId, $"{versionId}.json");
                if (!File.Exists(loaderVersionJson))
                {
                    ProgressText.Text = $"Installing {instance.Loader} {instance.LoaderVersion}...";
                    DownloadProgress.IsIndeterminate = true;
                    try
                    {
                        await RunLoaderInstallAsync(instance, gameDir);
                    }
                    catch (Exception ex)
                    {
                        ShowNotification(InfoBarSeverity.Error, $"Loader install failed:\n{ex.Message}");
                        return;
                    }
                }
            }

            var meta = isModded ? await _vm.GetMergedMetaAsync(versionId, gameDir) : vanillaMeta;

            if (isModded)
            {
                ProgressText.Text = "Loader libraries...";
                var dl = new AssetDownloader(_im.SharedDir, gameDir);
                dl.ProgressChanged += (s, _) => DispatcherQueue.TryEnqueue(() => ProgressText.Text = s);
                await Task.Run(() => dl.DownloadVersionAsync(new VersionMeta { Id = versionId, Libraries = meta.Libraries, Downloads = [] }));
            }

            // Event integrity check
            if (App.IsEventMode && App.EventConfig?.Integrity is { CheckBeforeLaunch: true })
            {
                ProgressText.Text = "Checking integrity...";
                var result = await Core.Config.IntegrityChecker.VerifyAsync(App.EventConfig, gameDir);
                if (!result.IsValid)
                {
                    var msg = string.Join("\n", result.Violations.Take(5));
                    ShowNotification(InfoBarSeverity.Error, $"Integrity check failed:\n{msg}");
                    if (App.EventConfig.Integrity.BlockOnFailure) return;
                }
            }

            ProgressText.Text = "Launching...";
            DownloadProgress.IsIndeterminate = false;
            DownloadProgress.Value = 95;

            var launcher = new GameLauncher(gameDir, _im.SharedDir);
            var proc = launcher.Launch(meta, javaPath, S.Username,
                uuid: S.Uuid, accessToken: S.AccessToken,
                minMem: instance.MinMemoryMb, maxMem: instance.MaxMemoryMb,
                extraJvmArgs: instance.JvmArgs,
                windowWidth: instance.WindowWidth, windowHeight: instance.WindowHeight,
                vanillaVersionId: isModded ? instance.McVersion : null,
                server: _pendingServer ?? (App.EventConfig?.Server?.AutoConnect == true ? App.EventConfig.Server.Host : null),
                port: _pendingPort ?? (App.EventConfig?.Server?.AutoConnect == true ? App.EventConfig.Server.Port : null));

            _pendingServer = null;
            _pendingPort = null;

            LogText.Text = "";
            _logAutoScroll = true;

            var launcherLogPath = Path.Combine(gameDir, "logs", "launcher-latest.log");
            Directory.CreateDirectory(Path.GetDirectoryName(launcherLogPath)!);
            var logWriter = new StreamWriter(launcherLogPath, append: false) { AutoFlush = true };

            proc.OutputDataReceived += (_, args) =>
            {
                if (args.Data == null) return;
                AppendLog(args.Data);
                App.Discord.ProcessLogLine(args.Data);
                try { logWriter.WriteLine(args.Data); } catch { }
            };
            proc.ErrorDataReceived += (_, args) =>
            {
                if (args.Data == null) return;
                AppendLog($"[ERR] {args.Data}");
                try { logWriter.WriteLine($"[ERR] {args.Data}"); } catch { }
            };
            proc.Exited += (_, _) => { try { logWriter.Dispose(); } catch { } };
            proc.EnableRaisingEvents = true;
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            App.RunningInstances[instance.Id] = proc;
            App.NotifyRunningChanged();
            instance.LastPlayed = DateTime.UtcNow;
            _im.SaveInstance(instance);
            UpdatePlayButton();

            var modsDir2 = Path.Combine(gameDir, "mods");
            var mc = Directory.Exists(modsDir2) ? Directory.GetFiles(modsDir2, "*.jar").Length : 0;
            App.Discord.SetInstance(instance, mc);

            var instId = instance.Id;
            _ = Task.Run(async () =>
            {
                await proc.WaitForExitAsync();
                var exit = proc.ExitCode;
                App.RunningInstances.TryRemove(instId, out _);
                App.NotifyRunningChanged();
                App.Discord.OnGameExit();
                if (App.IsHidden && !App.HasRunningInstances())
                    App.ShowWindow();
                DispatcherQueue.TryEnqueue(() =>
                {
                    UpdatePlayButton();
                    if (App.IsReconnecting)
                    {
                        App.IsReconnecting = false;
                    }
                    else if (_killedByUser.Remove(instId))
                    {
                        // User hit Kill — no repair dialog, no crash noise.
                    }
                    else if (exit != 0)
                    {
                        _ = ShowRepairDialogAsync(instId, exit);
                    }
                    else
                    {
                        ShowNotification(InfoBarSeverity.Success, App.L("home.game_closed"));
                    }
                });
            });

            ProgressText.Text = "Game launched!";
            DownloadProgress.Value = 100;

            if (S.CloseOnLaunch)
                App.HideWindow();

        }
        catch (Exception ex)
        {
            ShowNotification(InfoBarSeverity.Error, FriendlyError(ex));
        }
        finally
        {
            if (GetSelectedInstance() is not { } si || !IsInstanceRunning(si.Id))
            {
                PlayButton.IsEnabled = true;
                UpdatePlayButton();
            }
            await Task.Delay(3000);
            ProgressPanel.Visibility = Visibility.Collapsed;
            DownloadProgress.Value = 0;
        }
    }

    // --- Log panel ---
    private void ToggleLog_Click(object sender, RoutedEventArgs e)
    {
        var expanding = LogExpanded.Visibility == Visibility.Collapsed;
        LogExpanded.Visibility = expanding ? Visibility.Visible : Visibility.Collapsed;
        LogBarCollapsed.Visibility = expanding ? Visibility.Collapsed : Visibility.Visible;
        if (expanding)
        {
            AnimationHelper.SlideIn(LogExpanded, 0);
            _logAutoScroll = true;
            ScrollLogToBottom();
        }
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e) => LogText.Text = "";

    private void LogScroll_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
    {
        if (e.IsIntermediate) return;
        var sv = LogScroll;
        _logAutoScroll = sv.VerticalOffset >= sv.ScrollableHeight - 20;
    }

    private void AppendLog(string line)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (LogText.Text.Length > 10000)
                LogText.Text = LogText.Text[5000..];
            LogText.Text += line + "\n";
            LogBarStatus.Text = line.Length > 80 ? line[..80] + "..." : line;

            if (_logAutoScroll)
                ScrollLogToBottom();
        });
    }

    private void ScrollLogToBottom()
    {
        LogScroll.UpdateLayout();
        LogScroll.ChangeView(null, LogScroll.ScrollableHeight, null, true);
    }

    private async Task RunLoaderInstallAsync(Core.Instances.GameInstance inst, string gameDir)
    {
        void OnProgress(string status, double pct)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                ProgressText.Text = status;
                if (pct >= 0) { DownloadProgress.IsIndeterminate = false; DownloadProgress.Value = pct; }
            });
        }

        switch (inst.Loader)
        {
            case LoaderType.Fabric:
                {
                    var i = new FabricInstaller(_im.SharedDir, gameDir);
                    i.ProgressChanged += OnProgress;
                    await i.InstallAsync(inst.McVersion, inst.LoaderVersion!);
                    break;
                }
            case LoaderType.Quilt:
                {
                    var i = new QuiltInstaller(_im.SharedDir, gameDir);
                    i.ProgressChanged += OnProgress;
                    await i.InstallAsync(inst.McVersion, inst.LoaderVersion!);
                    break;
                }
            case LoaderType.Forge:
                {
                    var i = new ForgeInstaller(_im.SharedDir, gameDir);
                    i.ProgressChanged += OnProgress;
                    await i.InstallAsync(inst.McVersion, inst.LoaderVersion!);
                    break;
                }
            case LoaderType.NeoForge:
                {
                    var i = new NeoForgeInstaller(_im.SharedDir, gameDir);
                    i.ProgressChanged += OnProgress;
                    await i.InstallAsync(inst.McVersion, inst.LoaderVersion!);
                    break;
                }
        }
    }

    private async Task ShowRepairDialogAsync(string instanceId, int exitCode)
    {
        var inst = _im.GetInstance(instanceId);
        if (inst == null) return;

        var gameDir = _im.GetGameDir(instanceId);
        var list = new StackPanel { Spacing = 6 };
        var scroll = new ScrollViewer { Content = list, MaxHeight = 500, MinWidth = 500 };

        list.Children.Add(new TextBlock
        {
            Text = exitCode >= 0 ? App.L("home.crashed", exitCode) : "Java not found",
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xFF, 0x6B, 0x6B)),
            FontSize = 14,
            Margin = new Thickness(0, 0, 0, 4)
        });

        var busy = new ProgressRing { IsActive = true, Width = 20, Height = 20 };
        list.Children.Add(busy);

        var dialog = new ContentDialog
        {
            Title = "Diagnostics",
            Content = scroll,
            CloseButtonText = "Close",
            PrimaryButtonText = "Open instance folder",
            XamlRoot = this.XamlRoot
        };
        dialog.PrimaryButtonClick += (_, args) =>
        {
            args.Cancel = true;
            if (Directory.Exists(gameDir))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = gameDir, UseShellExecute = true });
        };

        var showTask = dialog.ShowAsync().AsTask();

        var reports = await InstanceDiagnostics.RunAsync(inst, _im, _vm, S.AccessToken);

        list.Children.Remove(busy);
        foreach (var r in reports)
            list.Children.Add(BuildReportCard(r, () => _ = RefreshDiagnostics(inst, list)));

        await showTask;
    }

    private async Task RefreshDiagnostics(Core.Instances.GameInstance inst, StackPanel list)
    {
        list.Children.Clear();
        var busy = new ProgressRing { IsActive = true, Width = 20, Height = 20 };
        list.Children.Add(busy);
        var reports = await InstanceDiagnostics.RunAsync(inst, _im, _vm, S.AccessToken);
        list.Children.Remove(busy);
        foreach (var r in reports)
            list.Children.Add(BuildReportCard(r, () => _ = RefreshDiagnostics(inst, list)));
    }

    private static Border BuildReportCard(DiagnosticReport r, Action onRefresh)
    {
        var (glyph, color) = r.Severity switch
        {
            DiagnosticSeverity.Ok => ("\uE73E", Windows.UI.Color.FromArgb(0xFF, 0x4C, 0xAF, 0x50)),
            DiagnosticSeverity.Warning => ("\uE7BA", Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xB0, 0x00)),
            _ => ("\uEA39", Windows.UI.Color.FromArgb(0xFF, 0xFF, 0x6B, 0x6B)),
        };

        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        header.Children.Add(new FontIcon { Glyph = glyph, FontSize = 16, Foreground = new SolidColorBrush(color), VerticalAlignment = VerticalAlignment.Center });
        header.Children.Add(new TextBlock { Text = r.Title, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });

        var body = new StackPanel { Spacing = 6, Margin = new Thickness(0, 4, 0, 0) };
        body.Children.Add(header);

        if (!string.IsNullOrEmpty(r.Detail))
            body.Children.Add(new TextBlock
            {
                Text = r.Detail,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0xAA, 0xFF, 0xFF, 0xFF)),
                Margin = new Thickness(26, 0, 0, 0)
            });

        if (r.FixLabel != null && r.Fix != null)
        {
            var btn = new Button { Content = r.FixLabel, Margin = new Thickness(26, 4, 0, 0), MinHeight = 30 };
            btn.Click += async (_, _) =>
            {
                btn.IsEnabled = false;
                var original = btn.Content;
                btn.Content = "Working...";
                try { await r.Fix(); btn.Content = "Done"; onRefresh(); }
                catch (Exception ex) { btn.Content = $"Failed: {ex.Message[..Math.Min(60, ex.Message.Length)]}"; }
            };
            body.Children.Add(btn);
        }

        return new Border
        {
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10),
            Child = body
        };
    }

    private void ShowNotification(InfoBarSeverity severity, string message)
    {
        NotificationBar.Severity = severity;
        NotificationBar.Message = message;
        NotificationBar.Title = "";
        NotificationBar.ActionButton = null;
        NotificationBar.IsOpen = true;
    }

    private static string FriendlyError(Exception ex) => ex switch
    {
        System.Net.Http.HttpRequestException => $"{App.L("gen.no_internet")}\n{ex.Message}",
        IOException io => $"{App.L("gen.file_error")}\n{io.Message}",
        System.Text.Json.JsonException => $"{App.L("gen.corrupted_data")}\n{ex.Message}",
        _ => $"{ex.GetType().Name}: {ex.Message}"
    };
}
