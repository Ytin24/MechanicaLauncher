using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using MechanicaLauncher.Core.Auth;
using MechanicaLauncher.Core.Game;
using MechanicaLauncher.Core.IO;
using MechanicaLauncher.Core.Mods;
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
    private bool _loading;
    private bool _preparing;
    private bool _logAutoScroll = true;
    private readonly HashSet<string> _killedByUser = new();

    public HomePage()
    {
        this.InitializeComponent();
        PlayButton.Resources["ButtonBackgroundPointerOver"] = new SolidColorBrush { Opacity = 0.9 };
        PlayButton.Resources["ButtonBackgroundPressed"] = new SolidColorBrush { Opacity = 0.8 };
        AnimationHelper.AddButtonFeedback(PlayButton);
        AnimationHelper.AddButtonFeedback(InstancePicker);
        AnimationHelper.AddButtonFeedback(ModsCard);
        AnimationHelper.AddButtonFeedback(AccountCard);
        AnimationHelper.AddButtonFeedback(InstancesCard);
        _vm = new VersionManager(_im.SharedDir);
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        InstanceManager.InstancesChanged += OnInstancesChanged;
        App.RunningInstancesChanged += OnInstancesChanged;
        LoadPage();
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
        DetailsButton.Content = App.L("feature.details");
        DetailsButton.IsEnabled = inst != null;
        var cover = inst == null ? null : _im.GetCoverAbsolutePath(inst);
        CoverImage.Source = cover == null ? null : new Microsoft.UI.Xaml.Media.Imaging.BitmapImage { DecodePixelWidth = 1200, UriSource = new Uri(cover) };
        CoverBorder.Visibility = cover == null ? Visibility.Collapsed : Visibility.Visible;
        InstancePicker.BorderBrush = InstanceDetailsPage.AccentBrush(inst?.AccentColor);
        InstancePicker.IsEnabled = inst != null;
        if (inst == null)
        {
            HeroName.Text = App.L("home.no_instance");
            ToolTipService.SetToolTip(InstancePicker, App.L("home.create_instance"));
            HeroIconGrid.Children.Clear();
            HeroIconGrid.Children.Add(new FontIcon { Glyph = "\uE74C", FontSize = 22, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
            HeroIconBorder.Background = (Brush)Application.Current.Resources["CardHoverBrush"];
            return;
        }
        HeroName.Text = inst.Name;
        ToolTipService.SetToolTip(InstancePicker, $"{inst.Name}\n{InstanceInfo.Text}");

        HeroIconGrid.Children.Clear();
        var iconPath = _im.GetIconAbsolutePath(inst);
        if (iconPath != null)
        {
            HeroIconGrid.Children.Add(new Image
            {
                Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(iconPath)) { CreateOptions = Microsoft.UI.Xaml.Media.Imaging.BitmapCreateOptions.IgnoreImageCache },
                Stretch = Microsoft.UI.Xaml.Media.Stretch.UniformToFill,
            });
            HeroIconBorder.Background = (Brush)Application.Current.Resources["CardHoverBrush"];
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

    private void Details_Click(object sender, RoutedEventArgs e)
    {
        if (GetSelectedInstance() is { } instance) Frame.Navigate(typeof(InstanceDetailsPage), new InstanceDetailsRequest(instance.Id));
    }

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
                CornerRadius = new CornerRadius(6),
                Background = (Brush)Application.Current.Resources[isSelected ? "CardHoverBrush" : "CardBrush"],
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
                MaxWidth = 180,
                TextTrimming = TextTrimming.CharacterEllipsis,
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
            AnimationHelper.AddButtonFeedback(btn);
            ToolTipService.SetToolTip(btn, inst.Name);
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

    private void LoadPage()
    {
        ModsLabel.Text = App.L("home.mods");
        AccountLabel.Text = App.L("home.account");
        InstancesLabel.Text = App.L("home.instances");
        SelectedInstanceLabel.Text = App.L("home.selected_instance");
        LogHeader.Text = App.L("home.game_output");
        ProfileSelector.PlaceholderText = App.L("home.select_instance");
        if (App.RunningInstances.Count == 0) LogBarStatus.Text = App.L("home.game_output");

        AccountText.Text = S.Username;

        RefreshInstancesUi();
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
            InstanceInfo.Text = App.L("home.create_instance");
            ModCountText.Text = "0";
            return;
        }

        var modsDir = Path.Combine(_im.GetGameDir(inst.Id), "mods");
        var modCount = Directory.Exists(modsDir) ? Directory.GetFiles(modsDir, "*.jar").Length : 0;
        ModCountText.Text = App.L("home.active_mods", modCount);

        var parts = new List<string> { inst.McVersion };
        if (inst.Loader != LoaderType.None)
            parts.Add($"{inst.Loader} {inst.LoaderVersion}");
        parts.Add($"{inst.MinMemoryMb}-{inst.MaxMemoryMb} MB");
        if (inst.LastPlayed.HasValue)
            parts.Add(App.L("inst.last_played", inst.LastPlayed.Value.ToLocalTime().ToString("d")));
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
            AnimationHelper.SlideIn(HeroDetails);
            // Recent pills also update to highlight the new selection.
            RebuildRecentChips(_im.GetAllInstances());
        }
    }

    // --- Navigation cards ---
    private void Card_Mods_Click(object sender, RoutedEventArgs e) =>
        NavigateTo("Mods");
    private void Card_Account_Click(object sender, RoutedEventArgs e) =>
        NavigateTo("Account");
    private void Card_Instances_Click(object sender, RoutedEventArgs e) =>
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
        var previousLabel = Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(PlayButton);
        if (_preparing || App.LaunchPreparationGate.CurrentCount == 0)
        {
            PlayButton.IsEnabled = App.PreparationCancellation?.IsCancellationRequested == false;
            var preparationLabel = App.L(PlayButton.IsEnabled ? "home.cancel_preparation" : "home.cancelling");
            ToolTipService.SetToolTip(PlayButton, preparationLabel);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(PlayButton, preparationLabel);
            PlayButton.Content = new TextBlock
            {
                Text = preparationLabel,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center
            };
            if (previousLabel != preparationLabel && PlayButton.IsLoaded)
                AnimationHelper.SlideIn((UIElement)PlayButton.Content);
            return;
        }
        var inst = GetSelectedInstance();
        var running = inst != null && IsInstanceRunning(inst.Id);

        PlayButton.IsEnabled = inst != null;
        var label = running ? App.L("home.kill") : App.L("home.play");
        if (!running && App.IsEventMode && App.EventConfig?.Ui?.PlayButtonText is { } eventLabel)
            label = eventLabel;
        ToolTipService.SetToolTip(PlayButton, $"{label} (Ctrl+Enter)");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(PlayButton, label);

        var row = new Grid { ColumnSpacing = 12 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.Children.Add(new FontIcon
        {
            Glyph = running ? "\uE71A" : "\uE768", FontSize = 20
        });
        var text = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        text.Children.Add(new TextBlock
        {
            Text = label, FontSize = 18,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap, MaxLines = 2,
            TextTrimming = TextTrimming.CharacterEllipsis
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
            text.Children.Add(_elapsedLabel);

            _elapsedTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _elapsedTimer.Tick += (_, _) =>
            {
                if (!IsInstanceRunning(inst.Id)) { _elapsedTimer?.Stop(); return; }
                UpdateElapsedText(startTime);
            };
            _elapsedTimer.Start();
        }

        PlayButton.Content = row;
        if (previousLabel != label && PlayButton.IsLoaded)
            AnimationHelper.SlideIn(row);
        var background = running
            ? new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xA6, 0x3E, 0x3E))
            : (SolidColorBrush)Application.Current.Resources[App.IsEventMode ? "AccentBrush" : "PlayBrush"];
        PlayButton.Background = background;
        ((SolidColorBrush)PlayButton.Resources["ButtonBackgroundPointerOver"]).Color = background.Color;
        ((SolidColorBrush)PlayButton.Resources["ButtonBackgroundPressed"]).Color = background.Color;
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
    public void LaunchWithServer(string instanceId, string? server, int port)
    {
        try
        {
            if (_preparing || App.LaunchPreparationGate.CurrentCount == 0 || IsInstanceRunning(instanceId))
            {
                ShowNotification(InfoBarSeverity.Informational, "A launch is already in progress or this instance is running.");
                return;
            }
            RefreshInstancesUi();
            for (int i = 0; i < ProfileSelector.Items.Count; i++)
                if ((ProfileSelector.Items[i] as ComboBoxItem)?.Tag?.ToString() == instanceId)
                { ProfileSelector.SelectedIndex = i; break; }
            if (GetSelectedInstance()?.Id != instanceId)
                throw new InvalidOperationException("The requested instance was not found.");
            _pendingServer = server;
            _pendingPort = server == null ? null : port;
            PlayButton_Click(this, new RoutedEventArgs());
        }
        catch (Exception ex) { ShowNotification(InfoBarSeverity.Error, FriendlyError(ex)); }
    }

    private async void PlayButton_Click(object sender, RoutedEventArgs e)
    {
        if (App.PreparationCancellation is { } preparation)
        {
            preparation.Cancel();
            PlayButton.IsEnabled = false;
            ProgressText.Text = App.L("home.cancelling");
            return;
        }
        var server = _pendingServer;
        var port = _pendingPort;
        _pendingServer = null;
        _pendingPort = null;
        if (_preparing) return;
        var instance = GetSelectedInstance();
        if (instance == null) { ShowNotification(InfoBarSeverity.Warning, App.L("home.select_instance")); return; }

        if (IsInstanceRunning(instance.Id))
        {
            if (App.RunningInstances.TryGetValue(instance.Id, out var p))
            {
                try
                {
                    _killedByUser.Add(instance.Id);
                    p.Kill(entireProcessTree: true);
                }
                catch (Exception ex)
                {
                    _killedByUser.Remove(instance.Id);
                    ShowNotification(InfoBarSeverity.Error, FriendlyError(ex));
                    return;
                }
            }
            UpdatePlayButton();
            ShowNotification(InfoBarSeverity.Informational, App.L("home.stopped", instance.Name));
            return;
        }

        if (!await App.LaunchPreparationGate.WaitAsync(0))
        {
            ShowNotification(InfoBarSeverity.Informational, "Another launch is being prepared. Wait for it to finish.");
            return;
        }
        _preparing = true;
        using var cancellation = new CancellationTokenSource();
        var cancellationToken = cancellation.Token;
        using var download = App.Downloads.Track(App.L("downloads.launch", instance.Name), instance.Id, cancellation, () =>
        {
            if (App.MainWindow is MainWindow main) main.LaunchServer(instance.Id, server, port ?? 25565);
        });
        App.PreparationCancellation = cancellation;
        App.PreparingInstanceId = instance.Id;
        var discordSession = App.Discord.BeginPreparation(instance);
        TextWriter? launchLog = null;
        try
        {
            App.NotifyRunningChanged();
            NotificationBar.IsOpen = false;
            LogBarStatus.Text = App.L("home.game_output");
            PlayButton.IsEnabled = true;
            PlayButton.Content = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 12,
                Children =
                {
                    new ProgressRing { IsActive = true, Width = 22, Height = 22, Foreground = new SolidColorBrush(Microsoft.UI.Colors.White) },
                    new TextBlock { Text = App.L("home.cancel_preparation"), FontSize = 18, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center }
                }
            };
            ProgressPanel.Visibility = Visibility.Visible;
            S.SelectedInstanceId = instance.Id;
            S.Save();
            var gameDir = _im.GetGameDir(instance.Id);
            var versionId = instance.GetEffectiveVersionId();
            var isModded = instance.Loader != LoaderType.None;
            if (isModded && string.IsNullOrWhiteSpace(instance.LoaderVersion))
                throw new InvalidOperationException("Select a loader version in instance settings.");
            var launcherLogPath = Path.Combine(gameDir, "logs", "launcher-latest.log");
            Directory.CreateDirectory(Path.GetDirectoryName(launcherLogPath)!);
            launchLog = TextWriter.Synchronized(new StreamWriter(launcherLogPath) { AutoFlush = true });
            launchLog.WriteLine($"{DateTimeOffset.Now:O} Preparing {instance.Name}: {instance.McVersion}, {instance.Loader} {instance.LoaderVersion}");

            var compatibility = await new ModCompatibilityChecker().CheckAsync(instance, gameDir, false, cancellationToken);
            if (compatibility.Issues.Any(i => i.IsError))
            {
                var dialog = new ContentDialog { XamlRoot = (App.MainWindow.Content as FrameworkElement)?.XamlRoot,
                    Title = App.L("compat.launch"),
                    Content = string.Join("\n\n", compatibility.Issues.Where(i => i.IsError).Take(5).Select(i => App.L("compat." + i.Code) + "\n" + i.Detail)) + "\n\n" + App.L("compat.local"),
                    PrimaryButtonText = App.L("compat.fix"), SecondaryButtonText = App.L("compat.continue"), CloseButtonText = App.L("feature.cancel") };
                using var closeDialog = cancellationToken.Register(() => DispatcherQueue.TryEnqueue(dialog.Hide));
                var choice = await dialog.ShowAsync();
                if (choice != ContentDialogResult.Secondary)
                {
                    cancellation.Cancel();
                    if (choice == ContentDialogResult.Primary && App.MainWindow is MainWindow main) main.ShowInstanceDetails(instance.Id, "compatibility");
                    return;
                }
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (S.AuthMode == "microsoft")
            {
                if (string.IsNullOrWhiteSpace(S.AccessToken) || S.AccessToken == "0")
                    throw new InvalidOperationException(App.L("acc.session_expired"));
                ProgressText.Text = "Validating session...";
                DownloadProgress.IsIndeterminate = true;
                if (!await MicrosoftAuth.ValidateTokenAsync(S.AccessToken, cancellationToken))
                {
                    if (!string.IsNullOrEmpty(S.MsRefreshToken))
                    {
                        try
                        {
                            ProgressText.Text = "Refreshing session...";
                            var refreshed = await new MicrosoftAuth(S.MsClientId).RefreshAsync(S.MsRefreshToken, cancellationToken);
                            S.Username = refreshed.Username;
                            S.Uuid = refreshed.Uuid;
                            S.AccessToken = refreshed.AccessToken;
                            S.MsRefreshToken = refreshed.RefreshToken ?? S.MsRefreshToken;
                            S.Save();
                        }
                        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                        {
                            launchLog.WriteLine(ex);
                            ShowNotification(InfoBarSeverity.Warning, FriendlyError(ex));
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
            var vanillaMeta = await _vm.GetVersionMetaAsync(instance.McVersion, cancellationToken);

            var requiredJava = vanillaMeta.JavaVersion?.MajorVersion ?? 8;
            var javaComponent = vanillaMeta.JavaVersion?.Component ?? "jre-legacy";
            var javaPath = !string.IsNullOrWhiteSpace(instance.JavaPath)
                ? instance.JavaPath : JavaFinder.FindJava(javaComponent, requiredJava);

            if (javaPath == null)
            {
                ProgressText.Text = $"Downloading Java ({javaComponent})...";
                javaPath = await JavaFinder.DownloadJavaAsync(javaComponent, _im.SharedDir,
                    status => DispatcherQueue.TryEnqueue(() => ProgressText.Text = status), cancellationToken);
                if (javaPath == null) { await ShowRepairDialogAsync(instance.Id, -1, captureCrash: false); return; }
            }
            JavaFinder.ValidateJava(javaPath, requiredJava);
            launchLog.WriteLine($"Java {requiredJava}: {javaPath}; memory {instance.MinMemoryMb}-{instance.MaxMemoryMb} MB");

            {
                ProgressText.Text = "Checking game files...";
                var dl = new AssetDownloader(_im.SharedDir, gameDir);
                dl.ProgressChanged += (s, p) => DispatcherQueue.TryEnqueue(() =>
                {
                    ProgressText.Text = s;
                    DownloadProgress.IsIndeterminate = p < 0;
                    if (p >= 0) DownloadProgress.Value = p;
                });
                await Task.Run(() => dl.DownloadVersionAsync(vanillaMeta, cancellationToken));
            }

            // Run the loader installer on first launch (instance create is lightweight and the
            // patched/remapped Minecraft jars only exist after processors run).
            if (isModded && !string.IsNullOrEmpty(instance.LoaderVersion))
            {
                var loaderVersionJson = Path.Combine(gameDir, "versions", versionId, $"{versionId}.json");
                var completePath = Path.Combine(Path.GetDirectoryName(loaderVersionJson)!, ".complete");
                if (!File.Exists(loaderVersionJson) || !File.Exists(completePath))
                {
                    ProgressText.Text = $"Installing {instance.Loader} {instance.LoaderVersion}...";
                    DownloadProgress.IsIndeterminate = true;
                    try
                    {
                        await RunLoaderInstallAsync(instance, gameDir, javaPath, cancellationToken);
                    }
                    catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                    {
                        launchLog.WriteLine(ex);
                        ShowNotification(InfoBarSeverity.Error, $"Loader install failed:\n{ex.Message}");
                        return;
                    }
                }
            }

            var meta = isModded ? await _vm.GetMergedMetaAsync(versionId, gameDir, cancellationToken) : vanillaMeta;

            if (isModded)
            {
                ProgressText.Text = "Loader libraries...";
                var dl = new AssetDownloader(_im.SharedDir, gameDir);
                dl.ProgressChanged += (s, _) => DispatcherQueue.TryEnqueue(() => ProgressText.Text = s);
                await Task.Run(() => dl.DownloadVersionAsync(new VersionMeta { Id = instance.McVersion, Libraries = meta.Libraries }, cancellationToken));
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
            var modsDirectory = Path.Combine(gameDir, "mods");
            var enabledModFiles = Directory.Exists(modsDirectory) ? Directory.GetFiles(modsDirectory, "*.jar").Length : 0;
            cancellationToken.ThrowIfCancellationRequested();
            var proc = launcher.Launch(meta, javaPath, S.Username,
                uuid: S.Uuid, accessToken: S.AccessToken,
                minMem: instance.MinMemoryMb, maxMem: instance.MaxMemoryMb,
                extraJvmArgs: instance.JvmArgs,
                windowWidth: instance.WindowWidth, windowHeight: instance.WindowHeight,
                vanillaVersionId: isModded ? instance.McVersion : null,
                server: server ?? (App.EventConfig?.Server?.AutoConnect == true ? App.EventConfig.Server.Host : null),
                port: port ?? (App.EventConfig?.Server?.AutoConnect == true ? App.EventConfig.Server.Port : null));
            App.Discord.GameStarted(discordSession, enabledModFiles);

            LogText.Text = "";
            _logAutoScroll = true;

            var logWriter = launchLog;
            launchLog = null;
            var crashLines = new System.Collections.Concurrent.ConcurrentQueue<string>();
            void RememberLine(string line)
            {
                crashLines.Enqueue(line[..Math.Min(line.Length, 4000)]);
                while (crashLines.Count > 500) crashLines.TryDequeue(out _);
            }

            proc.OutputDataReceived += (_, args) =>
            {
                if (args.Data == null) return;
                AppendLog(args.Data);
                RememberLine(args.Data);
                App.Discord.ProcessLogLine(discordSession, args.Data);
                try { logWriter.WriteLine(args.Data); } catch { }
            };
            proc.ErrorDataReceived += (_, args) =>
            {
                if (args.Data == null) return;
                AppendLog($"[ERR] {args.Data}");
                RememberLine(args.Data);
                App.Discord.ProcessLogLine(discordSession, args.Data);
                try { logWriter.WriteLine($"[ERR] {args.Data}"); } catch { }
            };
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            App.RunningInstances[instance.Id] = proc;
            App.NotifyRunningChanged();
            var instId = instance.Id;
            _ = Task.Run(async () =>
            {
                await proc.WaitForExitAsync();
                var exit = proc.ExitCode;
                try { logWriter.WriteLine($"{DateTimeOffset.Now:O} Exit code: {exit}"); logWriter.Dispose(); }
                catch (IOException ex) { Debug.WriteLine(ex.Message); }
                ((ICollection<KeyValuePair<string, Process>>)App.RunningInstances).Remove(new(instId, proc));
                App.NotifyRunningChanged();
                App.Discord.EndSession(discordSession);
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (App.IsHidden && !App.HasRunningInstances())
                        App.ShowWindow();
                    LogBarStatus.Text = exit == 0 || _killedByUser.Contains(instId)
                        ? App.L("home.game_closed") : App.L("home.crashed", exit);
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
                        _ = ShowRepairDialogAsync(instId, exit, capturedLog: string.Join("\n", crashLines));
                    }
                    else
                    {
                        ShowNotification(InfoBarSeverity.Success, App.L("home.game_closed"));
                    }
                });
            });

            instance.LastPlayed = DateTime.UtcNow;
            _im.SaveInstance(instance);

            ProgressText.Text = "Game launched!";
            DownloadProgress.Value = 100;
            download.Job.Complete();

            if (S.CloseOnLaunch && !proc.HasExited)
                App.HideWindow();

        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            launchLog?.WriteLine($"{DateTimeOffset.Now:O} Preparation cancelled.");
            ShowNotification(InfoBarSeverity.Informational, App.L("home.cancelled"));
        }
        catch (Exception ex)
        {
            download.Job.Fail(ex);
            try { launchLog?.WriteLine(ex); } catch (IOException) { }
            ShowNotification(InfoBarSeverity.Error, FriendlyError(ex));
        }
        finally
        {
            App.Discord.EndPreparation(discordSession);
            _preparing = false;
            App.PreparationCancellation = null;
            App.PreparingInstanceId = null;
            App.LaunchPreparationGate.Release();
            try { launchLog?.Dispose(); } catch (IOException ex) { Debug.WriteLine(ex.Message); }
            ProgressPanel.Visibility = Visibility.Collapsed;
            DownloadProgress.Value = 0;
            App.NotifyRunningChanged();
            UpdatePlayButton();
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

            if (_logAutoScroll)
                ScrollLogToBottom();
        });
    }

    private void ScrollLogToBottom()
    {
        LogScroll.UpdateLayout();
        LogScroll.ChangeView(null, LogScroll.ScrollableHeight, null, true);
    }

    private async Task RunLoaderInstallAsync(Core.Instances.GameInstance inst, string gameDir, string? javaPath = null,
        CancellationToken cancellationToken = default)
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
                    await i.InstallAsync(inst.McVersion, inst.LoaderVersion!, cancellationToken);
                    break;
                }
            case LoaderType.Quilt:
                {
                    var i = new QuiltInstaller(_im.SharedDir, gameDir);
                    i.ProgressChanged += OnProgress;
                    await i.InstallAsync(inst.McVersion, inst.LoaderVersion!, cancellationToken);
                    break;
                }
            case LoaderType.Forge:
                {
                    var i = new ForgeInstaller(_im.SharedDir, gameDir);
                    i.ProgressChanged += OnProgress;
                    await i.InstallAsync(inst.McVersion, inst.LoaderVersion!, javaPath, cancellationToken);
                    break;
                }
            case LoaderType.NeoForge:
                {
                    var i = new NeoForgeInstaller(_im.SharedDir, gameDir);
                    i.ProgressChanged += OnProgress;
                    await i.InstallAsync(inst.McVersion, inst.LoaderVersion!, javaPath, cancellationToken);
                    break;
                }
        }
    }

    private async Task ShowRepairDialogAsync(string instanceId, int exitCode, bool captureCrash = true, string? capturedLog = null)
    {
        var inst = _im.GetInstance(instanceId);
        if (inst == null) return;

        var gameDir = _im.GetGameDir(instanceId);
        if (captureCrash)
        {
            try
            {
                var report = capturedLog == null
                    ? await CrashAnalyzer.CaptureAsync(gameDir, exitCode, [S.AccessToken, S.MsRefreshToken])
                    : CrashAnalyzer.Analyze(capturedLog, exitCode, [S.AccessToken, S.MsRefreshToken]);
                if (capturedLog != null) CrashAnalyzer.SaveReport(gameDir, report);
                var summary = new StackPanel { Spacing = 12 };
                summary.Children.Add(new TextBlock { Text = App.L("crash." + report.Reason), FontSize = 18, TextWrapping = TextWrapping.Wrap });
                summary.Children.Add(new TextBlock { Text = App.L("crash." + report.Reason + ".help"), TextWrapping = TextWrapping.Wrap });
                if (report.Evidence.Length > 0) summary.Children.Add(new TextBlock { Text = report.Evidence, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, FontSize = 12 });
                var crashDialog = new ContentDialog { XamlRoot = (App.MainWindow.Content as FrameworkElement)?.XamlRoot,
                    Title = App.L("crash.title", exitCode), Content = summary,
                    PrimaryButtonText = App.L("feature.crashes"), SecondaryButtonText = App.L("feature.compatibility"), CloseButtonText = App.L("feature.back") };
                var choice = await crashDialog.ShowAsync();
                if (choice != ContentDialogResult.None && App.MainWindow is MainWindow main)
                    main.ShowInstanceDetails(instanceId, choice == ContentDialogResult.Primary ? "crashes" : "compatibility");
            }
            catch (Exception ex) { Debug.WriteLine(ex); ShowNotification(InfoBarSeverity.Error, App.L("crash.title", exitCode)); }
            return;
        }
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
                Foreground = (Brush)Application.Current.Resources["SubtleBrush"],
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
            Background = (Brush)Application.Current.Resources["CardBrush"],
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
        System.Net.Http.HttpRequestException { StatusCode: { } status } http => $"{App.L("gen.service_error", (int)status)}\n{http.Message}",
        System.Net.Http.HttpRequestException => $"{App.L("gen.no_internet")}\n{ex.Message}",
        TaskCanceledException or TimeoutException => $"{App.L("gen.request_timeout")}\n{ex.Message}",
        InvalidDataException => $"{App.L("gen.corrupted_data")}\n{ex.Message}",
        IOException io => $"{App.L("gen.file_error")}\n{io.Message}",
        System.Text.Json.JsonException => $"{App.L("gen.corrupted_data")}\n{ex.Message}",
        _ => $"{ex.GetType().Name}: {ex.Message}"
    };
}
