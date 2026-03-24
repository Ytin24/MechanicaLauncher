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

    public HomePage()
    {
        this.InitializeComponent();
        _versionManager = new VersionManager(_settings.GameDir);
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        var allJava = JavaFinder.FindAllJava();
        if (allJava.Count > 0)
        {
            var best = allJava.OrderByDescending(j => j.MajorVersion).First();
            JavaVersionText.Text = $"Java {best.MajorVersion}";
        }
        else
        {
            JavaVersionText.Text = "Not found";
        }

        var modsDir = Path.Combine(_versionManager.GameDir, "mods");
        if (Directory.Exists(modsDir))
        {
            var count = Directory.GetFiles(modsDir, "*.jar").Length;
            ModCountText.Text = $"{count} active";
        }

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

    private async void PlayButton_Click(object sender, RoutedEventArgs e)
    {
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
                ShowNotification(InfoBarSeverity.Error, $"Version {versionId} not found in manifest.");
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
                    ShowNotification(InfoBarSeverity.Error,
                        $"Java not found and download failed. Install Java manually or set path in Settings.");
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
            var proc = launcher.Launch(meta, javaPath, _settings.Username,
                uuid: _settings.Uuid, accessToken: _settings.AccessToken,
                minMem: _settings.MinMemoryMb, maxMem: _settings.MaxMemoryMb,
                extraJvmArgs: _settings.JvmArgs,
                windowWidth: _settings.WindowWidth, windowHeight: _settings.WindowHeight);

            _ = Task.Run(async () =>
            {
                var stderr = await proc.StandardError.ReadToEndAsync();
                await proc.WaitForExitAsync();
                if (proc.ExitCode != 0)
                {
                    DispatcherQueue.TryEnqueue(() =>
                        ShowNotification(InfoBarSeverity.Error,
                            $"Game crashed (exit {proc.ExitCode}): {stderr[..Math.Min(300, stderr.Length)]}"));
                }
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
