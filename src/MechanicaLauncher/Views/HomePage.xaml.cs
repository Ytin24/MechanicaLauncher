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

        var allJava = JavaFinder.FindAllJava();
        JavaVersionText.Text = allJava.Count > 0 ? $"Java {allJava.Max(j => j.MajorVersion)}" : "Not found";
        AccountText.Text = S.Username;

        var instances = _im.GetAllInstances();
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
        UpdateModCount();

        // Animate status cards
        int delay = 100;
        foreach (var child in ((Grid)((Grid)Content).Children[2]).Children)
        {
            if (child is Border b) { AnimationHelper.SlideIn(b, delay); delay += 60; }
        }

        try
        {
            _manifest = await _vm.GetManifestAsync();
            var running = App.RunningInstances.Count(kv => !kv.Value.HasExited);
            NotificationBar.Message = instances.Count > 0
                ? $"{instances.Count} instance(s){(running > 0 ? $", {running} running" : "")}. Latest: {_manifest.Latest.Release}"
                : "Create an instance in the Instances tab.";
        }
        catch (Exception ex)
        {
            NotificationBar.Severity = InfoBarSeverity.Warning;
            NotificationBar.Message = $"Offline — {ex.Message}";
        }
    }

    private void UpdateModCount()
    {
        var inst = GetSelectedInstance();
        if (inst != null)
        {
            var modsDir = Path.Combine(_im.GetGameDir(inst.Id), "mods");
            ModCountText.Text = Directory.Exists(modsDir) ? $"{Directory.GetFiles(modsDir, "*.jar").Length} active" : "0";
        }
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
            UpdateModCount();
        }
    }

    private void UpdatePlayButton()
    {
        var inst = GetSelectedInstance();
        var running = inst != null && IsInstanceRunning(inst.Id);

        PlayButton.IsEnabled = !running;
        PlayButton.Content = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 12,
            Children =
            {
                new FontIcon { Glyph = running ? "\uE769" : "\uE768", FontSize = 22, Foreground = new SolidColorBrush(Microsoft.UI.Colors.White) },
                new TextBlock { Text = running ? "RUNNING" : "P L A Y", FontSize = 20, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center }
            }
        };
    }

    private async void PlayButton_Click(object sender, RoutedEventArgs e)
    {
        var instance = GetSelectedInstance();
        if (instance == null) { ShowNotification(InfoBarSeverity.Warning, "Select an instance."); return; }
        if (IsInstanceRunning(instance.Id)) { ShowNotification(InfoBarSeverity.Warning, "Already running."); return; }

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
                ? instance.JavaPath
                : JavaFinder.FindJava(javaComponent);

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

            var instId = instance.Id;
            _ = Task.Run(async () =>
            {
                var stderr = await proc.StandardError.ReadToEndAsync();
                await proc.WaitForExitAsync();
                var exit = proc.ExitCode;
                App.RunningInstances.TryRemove(instId, out _);
                DispatcherQueue.TryEnqueue(() =>
                {
                    UpdatePlayButton();
                    ShowNotification(exit != 0 ? InfoBarSeverity.Error : InfoBarSeverity.Success,
                        exit != 0 ? $"Crashed (exit {exit}): {stderr[..Math.Min(300, stderr.Length)]}" : "Game closed.");
                });
            });

            ProgressText.Text = "Game launched!";
            DownloadProgress.Value = 100;
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

    private void ShowNotification(InfoBarSeverity severity, string message)
    {
        NotificationBar.Severity = severity;
        NotificationBar.Message = message;
        NotificationBar.IsOpen = true;
    }
}
