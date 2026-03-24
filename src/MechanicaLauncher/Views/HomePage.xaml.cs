using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MechanicaLauncher.Core.Game;
using MechanicaLauncher.Core.Instances;
using MechanicaLauncher.Core.Models;
using MechanicaLauncher.Core.Profiles;

namespace MechanicaLauncher.Views;

public sealed partial class HomePage : Page
{
    private readonly LauncherSettings _settings = LauncherSettings.Load();
    private readonly InstanceManager _im = new();
    private VersionManager _vm = null!;
    private VersionManifest? _manifest;
    private Process? _gameProcess;

    public HomePage()
    {
        this.InitializeComponent();
        _vm = new VersionManager(_im.SharedDir);
        _ = LoadAsync();
    }

    private bool IsGameRunning => _gameProcess is { HasExited: false };

    private async Task LoadAsync()
    {
        var allJava = JavaFinder.FindAllJava();
        JavaVersionText.Text = allJava.Count > 0 ? $"Java {allJava.Max(j => j.MajorVersion)}" : "Not found";
        AccountText.Text = _settings.Username;

        var instances = _im.GetAllInstances();
        ProfileSelector.Items.Clear();

        foreach (var inst in instances)
        {
            var label = inst.Loader != LoaderType.None
                ? $"{inst.Name}  ·  {inst.McVersion}  ·  {inst.Loader}"
                : $"{inst.Name}  ·  {inst.McVersion}";
            ProfileSelector.Items.Add(new ComboBoxItem { Content = label, Tag = inst.Id });
        }

        if (ProfileSelector.Items.Count > 0)
        {
            var selectedIdx = 0;
            if (_settings.SelectedInstanceId != null)
            {
                for (int i = 0; i < ProfileSelector.Items.Count; i++)
                {
                    if ((ProfileSelector.Items[i] as ComboBoxItem)?.Tag?.ToString() == _settings.SelectedInstanceId)
                    { selectedIdx = i; break; }
                }
            }
            ProfileSelector.SelectedIndex = selectedIdx;
        }

        var selected = GetSelectedInstance();
        if (selected != null)
        {
            var modsDir = Path.Combine(_im.GetGameDir(selected.Id), "mods");
            ModCountText.Text = Directory.Exists(modsDir) ? $"{Directory.GetFiles(modsDir, "*.jar").Length} active" : "0 active";
        }

        try
        {
            _manifest = await _vm.GetManifestAsync();
            NotificationBar.Message = instances.Count > 0
                ? $"{instances.Count} instance(s). Latest MC: {_manifest.Latest.Release}"
                : "No instances. Go to Instances to create one.";
        }
        catch (Exception ex)
        {
            NotificationBar.Severity = InfoBarSeverity.Warning;
            NotificationBar.Message = $"Offline — {ex.Message}";
        }
    }

    private GameInstance? GetSelectedInstance()
    {
        var id = (ProfileSelector.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        return id != null ? _im.GetInstance(id) : null;
    }

    private void UpdatePlayButton()
    {
        PlayButton.IsEnabled = !IsGameRunning;
        PlayButton.Content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            Children =
            {
                new FontIcon { Glyph = IsGameRunning ? "\uE769" : "\uE768", FontSize = 22, Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White) },
                new TextBlock { Text = IsGameRunning ? "RUNNING" : "P L A Y", FontSize = 20, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center }
            }
        };
    }

    private async void PlayButton_Click(object sender, RoutedEventArgs e)
    {
        if (IsGameRunning) { ShowNotification(InfoBarSeverity.Warning, "Game is already running."); return; }

        var instance = GetSelectedInstance();
        if (instance == null) { ShowNotification(InfoBarSeverity.Warning, "Select an instance first."); return; }

        _settings.SelectedInstanceId = instance.Id;
        _settings.Save();

        PlayButton.IsEnabled = false;
        ProgressPanel.Visibility = Visibility.Visible;

        try
        {
            var gameDir = _im.GetGameDir(instance.Id);
            var versionId = instance.GetEffectiveVersionId();

            ProgressText.Text = "Loading version...";
            DownloadProgress.IsIndeterminate = true;

            if (_manifest == null) _manifest = await _vm.GetManifestAsync();

            var vanillaEntry = _manifest.Versions.FirstOrDefault(v => v.Id == instance.McVersion);
            if (vanillaEntry != null)
                await _vm.GetVersionMetaAsync(vanillaEntry);

            var meta = await _vm.GetMergedMetaAsync(versionId, gameDir);

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

            if (!_vm.IsVersionInstalled(versionId, gameDir))
            {
                ProgressText.Text = "Downloading game...";
                var downloader = new AssetDownloader(_im.SharedDir, gameDir);
                downloader.ProgressChanged += (status, progress) =>
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        ProgressText.Text = status;
                        if (progress >= 0) { DownloadProgress.IsIndeterminate = false; DownloadProgress.Value = progress; }
                    });
                await Task.Run(() => downloader.DownloadVersionAsync(meta));
            }

            ProgressText.Text = "Launching...";
            DownloadProgress.IsIndeterminate = false;
            DownloadProgress.Value = 95;

            var launcher = new GameLauncher(gameDir, _im.SharedDir);
            _gameProcess = launcher.Launch(meta, javaPath, _settings.Username,
                uuid: _settings.Uuid, accessToken: _settings.AccessToken,
                minMem: instance.MinMemoryMb, maxMem: instance.MaxMemoryMb,
                extraJvmArgs: instance.JvmArgs,
                windowWidth: instance.WindowWidth, windowHeight: instance.WindowHeight);

            instance.LastPlayed = DateTime.UtcNow;
            _im.SaveInstance(instance);
            UpdatePlayButton();

            _ = Task.Run(async () =>
            {
                var stderr = await _gameProcess.StandardError.ReadToEndAsync();
                await _gameProcess.WaitForExitAsync();
                var exit = _gameProcess.ExitCode;
                _gameProcess = null;
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
            if (!IsGameRunning) PlayButton.IsEnabled = true;
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
