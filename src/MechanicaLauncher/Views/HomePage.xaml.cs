using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MechanicaLauncher.Core.Game;
using MechanicaLauncher.Core.Models;
using MechanicaLauncher.Core.Profiles;

namespace MechanicaLauncher.Views;

public sealed partial class HomePage : Page
{
    private readonly LauncherSettings _settings = LauncherSettings.Load();
    private readonly VersionManager _versionManager;
    private VersionManifest? _manifest;
    private Process? _gameProcess;

    public HomePage()
    {
        this.InitializeComponent();
        _versionManager = new VersionManager(_settings.GameDir);
        _ = LoadAsync();
    }

    private bool IsGameRunning => _gameProcess is { HasExited: false };

    private async Task LoadAsync()
    {
        var allJava = JavaFinder.FindAllJava();
        JavaVersionText.Text = allJava.Count > 0
            ? $"Java {allJava.Max(j => j.MajorVersion)}"
            : "Not found";

        var modsDir = Path.Combine(_versionManager.GameDir, "mods");
        if (Directory.Exists(modsDir))
            ModCountText.Text = $"{Directory.GetFiles(modsDir, "*.jar").Length} active";

        AccountText.Text = _settings.Username;

        try
        {
            _manifest = await _versionManager.GetManifestAsync();
            ProfileSelector.Items.Clear();

            foreach (var installed in _versionManager.GetInstalledVersions().Take(5))
                ProfileSelector.Items.Add(new ComboBoxItem { Content = installed, Tag = installed });

            if (_manifest.Latest.Release is { } latest)
                ProfileSelector.Items.Add(new ComboBoxItem { Content = $"{latest} (download)", Tag = latest });

            if (ProfileSelector.Items.Count > 0)
                ProfileSelector.SelectedIndex = 0;

            NotificationBar.Message = $"Latest: {_manifest.Latest.Release}. {_versionManager.GetInstalledVersions().Count} installed.";
        }
        catch (Exception ex)
        {
            NotificationBar.Severity = InfoBarSeverity.Warning;
            NotificationBar.Message = $"Offline mode — {ex.Message}";
        }
    }

    private void UpdatePlayButton()
    {
        if (IsGameRunning)
        {
            PlayButton.IsEnabled = false;
            PlayButton.Content = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 12,
                Children =
                {
                    new FontIcon { Glyph = "\uE769", FontSize = 22, Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White) },
                    new TextBlock { Text = "RUNNING", FontSize = 20, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center }
                }
            };
        }
        else
        {
            PlayButton.IsEnabled = true;
            PlayButton.Content = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 12,
                Children =
                {
                    new FontIcon { Glyph = "\uE768", FontSize = 22, Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White) },
                    new TextBlock { Text = "P L A Y", FontSize = 20, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center }
                }
            };
        }
    }

    private async void PlayButton_Click(object sender, RoutedEventArgs e)
    {
        if (IsGameRunning)
        {
            ShowNotification(InfoBarSeverity.Warning, "Game is already running.");
            return;
        }

        var selectedItem = ProfileSelector.SelectedItem as ComboBoxItem;
        var versionId = selectedItem?.Tag?.ToString();
        if (string.IsNullOrEmpty(versionId) || _manifest == null)
        {
            ShowNotification(InfoBarSeverity.Warning, "Select a version first.");
            return;
        }

        PlayButton.IsEnabled = false;
        ProgressPanel.Visibility = Visibility.Visible;

        try
        {
            var entry = _manifest.Versions.FirstOrDefault(v => v.Id == versionId);
            if (entry == null)
            {
                ShowNotification(InfoBarSeverity.Error, $"Version {versionId} not found.");
                return;
            }

            ProgressText.Text = "Loading version info...";
            DownloadProgress.IsIndeterminate = true;

            var meta = await _versionManager.GetVersionMetaAsync(entry);

            var javaComponent = meta.JavaVersion?.Component ?? "java-runtime-delta";
            var javaPath = !string.IsNullOrEmpty(_settings.JavaPath) && File.Exists(_settings.JavaPath)
                ? _settings.JavaPath
                : JavaFinder.FindJava(javaComponent);

            if (javaPath == null)
            {
                ProgressText.Text = $"Downloading Java ({javaComponent})...";
                javaPath = await JavaFinder.DownloadJavaAsync(javaComponent, _versionManager.GameDir,
                    status => DispatcherQueue.TryEnqueue(() => ProgressText.Text = status));

                if (javaPath == null)
                {
                    ShowNotification(InfoBarSeverity.Error, "Java not found. Set path in Settings.");
                    return;
                }
            }

            if (!_versionManager.IsVersionInstalled(versionId))
            {
                ProgressText.Text = "Downloading...";
                var downloader = new AssetDownloader(_versionManager.GameDir);
                downloader.ProgressChanged += (status, progress) =>
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        ProgressText.Text = status;
                        if (progress >= 0)
                        {
                            DownloadProgress.IsIndeterminate = false;
                            DownloadProgress.Value = progress;
                        }
                    });
                };
                await Task.Run(() => downloader.DownloadVersionAsync(meta));
            }

            ProgressText.Text = "Launching...";
            DownloadProgress.IsIndeterminate = false;
            DownloadProgress.Value = 95;

            var launcher = new GameLauncher(_versionManager.GameDir);
            _gameProcess = launcher.Launch(meta, javaPath, _settings.Username,
                uuid: _settings.Uuid, accessToken: _settings.AccessToken,
                minMem: _settings.MinMemoryMb, maxMem: _settings.MaxMemoryMb,
                extraJvmArgs: _settings.JvmArgs,
                windowWidth: _settings.WindowWidth, windowHeight: _settings.WindowHeight);

            UpdatePlayButton();

            _ = Task.Run(async () =>
            {
                var stderr = await _gameProcess.StandardError.ReadToEndAsync();
                await _gameProcess.WaitForExitAsync();
                var exitCode = _gameProcess.ExitCode;
                _gameProcess = null;

                DispatcherQueue.TryEnqueue(() =>
                {
                    UpdatePlayButton();
                    if (exitCode != 0)
                        ShowNotification(InfoBarSeverity.Error,
                            $"Game crashed (exit {exitCode}): {stderr[..Math.Min(300, stderr.Length)]}");
                    else
                        ShowNotification(InfoBarSeverity.Success, "Game closed.");
                });
            });

            ProgressText.Text = "Game launched!";
            DownloadProgress.Value = 100;
        }
        catch (Exception ex)
        {
            ShowNotification(InfoBarSeverity.Error, $"Error: {ex.Message}");
        }
        finally
        {
            if (!IsGameRunning)
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
