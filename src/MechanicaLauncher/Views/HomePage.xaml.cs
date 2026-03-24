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
        var java = JavaFinder.FindJava();
        JavaVersionText.Text = java != null ? JavaFinder.GetJavaVersion(java) : "Not found";

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
            {
                ProfileSelector.Items.Add(new ComboBoxItem { Content = installed, Tag = installed });
            }

            if (_manifest.Latest.Release is { } latest)
            {
                var item = new ComboBoxItem { Content = $"{latest} (download)", Tag = latest };
                ProfileSelector.Items.Add(item);
            }

            if (ProfileSelector.Items.Count > 0)
                ProfileSelector.SelectedIndex = 0;

            NotificationBar.Message = $"Latest: {_manifest.Latest.Release}. {_versionManager.GetInstalledVersions().Count} versions installed.";
        }
        catch (Exception ex)
        {
            NotificationBar.Severity = InfoBarSeverity.Warning;
            NotificationBar.Message = $"Offline mode — {ex.Message}";
        }
    }

    private async void PlayButton_Click(object sender, RoutedEventArgs e)
    {
        var java = JavaFinder.FindJava();
        if (java == null)
        {
            NotificationBar.Severity = InfoBarSeverity.Error;
            NotificationBar.Message = "Java not found. Install Java 21 or set path in Settings.";
            NotificationBar.IsOpen = true;
            return;
        }

        var selectedItem = ProfileSelector.SelectedItem as ComboBoxItem;
        var versionId = selectedItem?.Tag?.ToString();
        if (string.IsNullOrEmpty(versionId) || _manifest == null)
        {
            NotificationBar.Severity = InfoBarSeverity.Warning;
            NotificationBar.Message = "Select a version first.";
            NotificationBar.IsOpen = true;
            return;
        }

        PlayButton.IsEnabled = false;
        ProgressPanel.Visibility = Visibility.Visible;

        try
        {
            var entry = _manifest.Versions.FirstOrDefault(v => v.Id == versionId);
            if (entry == null) return;

            var meta = await _versionManager.GetVersionMetaAsync(entry);

            if (!_versionManager.IsVersionInstalled(versionId))
            {
                ProgressText.Text = "Downloading...";
                DownloadProgress.IsIndeterminate = true;

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
            var launcher = new GameLauncher(_versionManager.GameDir);
            launcher.Launch(meta, java, _settings.Username,
                uuid: _settings.Uuid, accessToken: _settings.AccessToken,
                minMem: _settings.MinMemoryMb, maxMem: _settings.MaxMemoryMb,
                extraJvmArgs: _settings.JvmArgs);

            ProgressText.Text = "Game launched!";
            DownloadProgress.Value = 100;
            DownloadProgress.IsIndeterminate = false;
        }
        catch (Exception ex)
        {
            NotificationBar.Severity = InfoBarSeverity.Error;
            NotificationBar.Message = $"Error: {ex.Message}";
            NotificationBar.IsOpen = true;
        }
        finally
        {
            PlayButton.IsEnabled = true;
            await Task.Delay(3000);
            ProgressPanel.Visibility = Visibility.Collapsed;
            DownloadProgress.Value = 0;
        }
    }
}
