using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
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

    public HomePage()
    {
        this.InitializeComponent();
        _vm = new VersionManager(_im.SharedDir);
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _ = LoadAsync();
    }

    private bool IsInstanceRunning(string id) =>
        App.RunningInstances.TryGetValue(id, out var p) && !p.HasExited;

    private async Task LoadAsync()
    {
        _loading = true;
        SplashText.Text = SettingsPage.GetRandomSplash();

        ModsLabel.Text = App.L("home.mods");
        AccountLabel.Text = App.L("home.account");
        InstancesLabel.Text = App.L("home.instances");
        ProfileSelector.PlaceholderText = App.L("home.select_instance");

        var allJava = JavaFinder.FindAllJava();
        AccountText.Text = S.Username;

        var instances = _im.GetAllInstances();
        InstanceCountText.Text = instances.Count.ToString();
        ProfileSelector.Items.Clear();

        foreach (var inst in instances)
        {
            var running = IsInstanceRunning(inst.Id);
            var label = inst.Loader != LoaderType.None
                ? $"{inst.Name}  ·  {inst.McVersion}  ·  {inst.Loader}"
                : $"{inst.Name}  ·  {inst.McVersion}";
            if (running) label += "  ▶";
            ProfileSelector.Items.Add(new ComboBoxItem { Content = label, Tag = inst.Id });
        }

        if (ProfileSelector.Items.Count > 0)
        {
            var idx = 0;
            if (S.SelectedInstanceId != null)
                for (int i = 0; i < ProfileSelector.Items.Count; i++)
                    if ((ProfileSelector.Items[i] as ComboBoxItem)?.Tag?.ToString() == S.SelectedInstanceId)
                    { idx = i; break; }
            ProfileSelector.SelectedIndex = idx;
        }

        _loading = false;

        UpdatePlayButton();
        UpdateInstanceInfo();

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
            NotificationBar.Message = instances.Count > 0
                ? $"{instances.Count} instance(s){(running > 0 ? $", {running} running" : "")}. Latest MC: {_manifest.Latest.Release}"
                : "Create an instance in the Instances tab.";
        }
        catch (Exception ex)
        {
            NotificationBar.Severity = InfoBarSeverity.Warning;
            NotificationBar.Message = $"Offline — {ex.Message}";
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
    private void UpdatePlayButton()
    {
        var inst = GetSelectedInstance();
        var running = inst != null && IsInstanceRunning(inst.Id);

        PlayButton.IsEnabled = true;
        var label = running ? App.L("home.kill") : App.L("home.play");
        if (inst != null && !running)
            label = App.L("home.play_with", inst.Name);

        PlayButton.Content = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 12,
            Children =
            {
                new FontIcon { Glyph = running ? "\uE71A" : "\uE768", FontSize = 22,
                    Foreground = new SolidColorBrush(running ? Windows.UI.Color.FromArgb(0xFF, 0xFF, 0x6B, 0x6B) : Microsoft.UI.Colors.White) },
                new TextBlock { Text = label, FontSize = 18, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center }
            }
        };

        PlayButton.Background = running
            ? new SolidColorBrush(Windows.UI.Color.FromArgb(0x33, 0xFF, 0x00, 0x00))
            : (Brush)Application.Current.Resources["AccentBrush"];
    }

    private async void PlayButton_Click(object sender, RoutedEventArgs e)
    {
        var instance = GetSelectedInstance();
        if (instance == null) { ShowNotification(InfoBarSeverity.Warning, "Select an instance."); return; }

        if (IsInstanceRunning(instance.Id))
        {
            if (App.RunningInstances.TryGetValue(instance.Id, out var p))
            {
                try { p.Kill(); } catch { }
                App.RunningInstances.TryRemove(instance.Id, out _);
            }
            UpdatePlayButton();
            ShowNotification(InfoBarSeverity.Informational, $"{instance.Name} killed.");
            return;
        }

        S.SelectedInstanceId = instance.Id;
        S.Save();
        PlayButton.IsEnabled = false;
        ProgressPanel.Visibility = Visibility.Visible;

        try
        {
            var gameDir = _im.GetGameDir(instance.Id);
            var versionId = instance.GetEffectiveVersionId();
            var isModded = instance.Loader != LoaderType.None;

            ProgressText.Text = "Loading version...";
            DownloadProgress.IsIndeterminate = true;
            _manifest ??= await _vm.GetManifestAsync();

            var vanillaEntry = _manifest.Versions.FirstOrDefault(v => v.Id == instance.McVersion);
            if (vanillaEntry == null) { ShowNotification(InfoBarSeverity.Error, $"MC {instance.McVersion} not found."); return; }

            var vanillaMeta = await _vm.GetVersionMetaAsync(vanillaEntry);
            var meta = isModded ? await _vm.GetMergedMetaAsync(versionId, gameDir) : vanillaMeta;

            var javaComponent = meta.JavaVersion?.Component ?? "java-runtime-delta";
            var javaPath = !string.IsNullOrEmpty(instance.JavaPath) && File.Exists(instance.JavaPath)
                ? instance.JavaPath : JavaFinder.FindJava(javaComponent);

            if (javaPath == null)
            {
                ProgressText.Text = $"Downloading Java ({javaComponent})...";
                javaPath = await JavaFinder.DownloadJavaAsync(javaComponent, _im.SharedDir,
                    status => DispatcherQueue.TryEnqueue(() => ProgressText.Text = status));
                if (javaPath == null) { ShowNotification(InfoBarSeverity.Error, "Java not found."); return; }
            }

            var vanillaJar = Path.Combine(gameDir, "versions", instance.McVersion, $"{instance.McVersion}.jar");
            if (!File.Exists(vanillaJar))
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

            if (isModded)
            {
                ProgressText.Text = "Loader libraries...";
                var dl = new AssetDownloader(_im.SharedDir, gameDir);
                dl.ProgressChanged += (s, _) => DispatcherQueue.TryEnqueue(() => ProgressText.Text = s);
                await Task.Run(() => dl.DownloadVersionAsync(new VersionMeta { Id = versionId, Libraries = meta.Libraries, Downloads = [] }));
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
                vanillaVersionId: isModded ? instance.McVersion : null);

            App.RunningInstances[instance.Id] = proc;
            instance.LastPlayed = DateTime.UtcNow;
            _im.SaveInstance(instance);
            UpdatePlayButton();

            LogText.Text = "";
            _logAutoScroll = true;
            proc.OutputDataReceived += (_, args) => { if (args.Data != null) AppendLog(args.Data); };
            proc.ErrorDataReceived += (_, args) => { if (args.Data != null) AppendLog($"[ERR] {args.Data}"); };
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            var instId = instance.Id;
            _ = Task.Run(async () =>
            {
                await proc.WaitForExitAsync();
                var exit = proc.ExitCode;
                App.RunningInstances.TryRemove(instId, out _);
                DispatcherQueue.TryEnqueue(() =>
                {
                    UpdatePlayButton();
                    ShowNotification(exit != 0 ? InfoBarSeverity.Error : InfoBarSeverity.Success,
                        exit != 0 ? App.L("home.crashed", exit) : App.L("home.game_closed"));
                });
            });

            ProgressText.Text = "Game launched!";
            DownloadProgress.Value = 100;

            if (S.CloseOnLaunch)
                App.MainWindow.Close();
        }
        catch (Exception ex)
        {
            ShowNotification(InfoBarSeverity.Error, ex.Message);
        }
        finally
        {
            if (GetSelectedInstance() is not { } si || !IsInstanceRunning(si.Id))
                PlayButton.IsEnabled = true;
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

    private void ShowNotification(InfoBarSeverity severity, string message)
    {
        NotificationBar.Severity = severity;
        NotificationBar.Message = message;
        NotificationBar.IsOpen = true;
    }
}
