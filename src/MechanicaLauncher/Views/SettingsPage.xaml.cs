using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using MechanicaLauncher.Core.Localization;
using MechanicaLauncher.Core.Updates;
using MechanicaLauncher.Core.Discord;

namespace MechanicaLauncher.Views;

public sealed partial class SettingsPage : Page
{
    private static Core.Profiles.LauncherSettings S => App.Settings;
    private bool _loading = true;
    private UpdateInfo? _updateInfo;
    private readonly DispatcherTimer _discordTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    public SettingsPage()
    {
        this.InitializeComponent();
        _discordTimer.Tick += (_, _) => RefreshDiscord();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _loading = true;

        ApplyLocale();

        ThemeSelector.SelectedIndex = S.Theme switch { "Dark" or "Тёмная" => 0, "Light" or "Светлая" => 1, _ => 2 };
        CloseOnLaunchToggle.IsOn = S.CloseOnLaunch;
        ShowSnapshotsToggle.IsOn = S.ShowSnapshots;
        DiscordRpcToggle.IsOn = S.DiscordRpc;
        DiscordServerToggle.IsOn = S.DiscordShowServer;
        DiscordDimensionToggle.IsOn = S.DiscordShowDimension;
        DiscordAchievementToggle.IsOn = S.DiscordShowAchievements;
        DiscordModsToggle.IsOn = S.DiscordShowMods;
        foreach (var t in new[] { DiscordRpcToggle, DiscordServerToggle, DiscordDimensionToggle,
                                  DiscordAchievementToggle, DiscordModsToggle,
                                  CloseOnLaunchToggle, ShowSnapshotsToggle })
        {
            t.Header = null;
            t.OnContent = "";
            t.OffContent = "";
            t.MinWidth = 0;
        }

        for (int i = 0; i < LangSelector.Items.Count; i++)
        {
            if ((LangSelector.Items[i] as ComboBoxItem)?.Tag?.ToString() == Locale.CurrentLanguage)
            { LangSelector.SelectedIndex = i; break; }
        }

        // Event mode
        if (App.IsEventMode)
        {
            EventCard.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
            EventInfoText.Text = $"{App.EventConfig?.Name ?? "Unknown"}\n{App.Settings.ActiveEventUrl}";
        }

        _loading = false;
        RefreshDiscord();
        _discordTimer.Start();

        _ = CheckUpdatesAsync();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _discordTimer.Stop();
        base.OnNavigatedFrom(e);
    }

    private async Task CheckUpdatesAsync()
    {
        VersionText.Text = $"v{UpdateChecker.GetCurrentVersion()}  ·  WinUI 3  ·  .NET 9";
        UpdateStatus.Text = App.L("set.checking_updates");

        for (int i = 0; i < 20 && App.LatestUpdate == null; i++)
            await Task.Delay(500);

        _updateInfo = App.LatestUpdate;
        if (_updateInfo == null)
        {
            UpdateStatus.Text = App.L("set.update_check_failed");
            return;
        }

        if (_updateInfo.IsAvailable)
        {
            UpdateStatus.Text = App.L("set.update_available", _updateInfo.LatestVersion);
            UpdateBtn.Content = App.L("set.download_update");
            UpdateBtn.Visibility = Visibility.Visible;
        }
        else
        {
            UpdateStatus.Text = App.L("set.up_to_date");
        }
    }

    private void Update_Click(object sender, RoutedEventArgs e)
    {
        if (_updateInfo?.ReleaseUrl != null)
        {
            _ = Windows.System.Launcher.LaunchUriAsync(new Uri(_updateInfo.ReleaseUrl));
        }
    }

    private void ApplyLocale()
    {
        PageTitle.Text = App.L("set.title");
        AppearanceLabel.Text = App.L("set.appearance");
        LauncherLabel.Text = App.L("set.launcher");
        ThemeCard.Header = App.L("set.theme");
        ThemeCard.Description = App.L("set.theme_desc");
        LangCard.Header = App.L("set.language");
        LangCard.Description = App.L("set.language_desc");
        ThemeDark.Content = App.L("set.dark");
        ThemeLight.Content = App.L("set.light");
        ThemeSystem.Content = App.L("set.system");
        CloseOnLaunchCard.Header = App.L("set.close_on_launch");
        CloseOnLaunchCard.Description = App.L("set.close_on_launch_desc");
        ShowSnapshotsCard.Header = App.L("set.show_snapshots");
        ShowSnapshotsCard.Description = App.L("set.show_snapshots_desc");
        DiscordCard.Description = App.L("discord.description");
        DiscordServerCard.Header = App.L("discord.server");
        DiscordServerCard.Description = App.L("discord.server_hint");
        DiscordDimensionCard.Header = App.L("discord.dimension");
        DiscordDimensionCard.Description = App.L("discord.dimension_hint");
        DiscordAchievementCard.Header = App.L("discord.achievement");
        DiscordAchievementCard.Description = App.L("discord.achievement_hint");
        DiscordModsCard.Header = App.L("discord.mods");
        DiscordModsCard.Description = App.L("discord.mods_hint");
        DiscordReconnectButton.Content = App.L("discord.reconnect");
        DiscordPreviewLabel.Text = App.L("discord.preview");
        RefreshDiscord();
    }

    private void Theme_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ThemeSelector?.SelectedItem is not ComboBoxItem item) return;
        var theme = item.Tag?.ToString() ?? "Dark";
        S.Theme = theme;
        S.Save();

        if (App.MainWindow?.Content is FrameworkElement root)
        {
            root.RequestedTheme = theme switch
            {
                "Light" or "Светлая" => ElementTheme.Light,
                "Dark" or "Тёмная" => ElementTheme.Dark,
                _ => ElementTheme.Default
            };
        }
    }

    private void Lang_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || LangSelector?.SelectedItem is not ComboBoxItem item) return;
        var lang = item.Tag?.ToString() ?? "en";
        S.Language = lang;
        S.Save();
        Locale.Init(lang);
        App.Discord.Configure(S, Locale.CurrentLanguage);
        ApplyLocale();
        if (App.MainWindow is MainWindow mainWindow) mainWindow.ApplyLocale();
    }

    private void CloseOnLaunch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        S.CloseOnLaunch = CloseOnLaunchToggle.IsOn;
        S.Save();
    }

    private void ShowSnapshots_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        S.ShowSnapshots = ShowSnapshotsToggle.IsOn;
        S.Save();
    }

    private void DiscordRpc_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        S.DiscordRpc = DiscordRpcToggle.IsOn;
        S.Save();
        App.Discord.Configure(S, Locale.CurrentLanguage);
        RefreshDiscord();
    }

    private void DiscordSetting_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        S.DiscordShowServer = DiscordServerToggle.IsOn;
        S.DiscordShowDimension = DiscordDimensionToggle.IsOn;
        S.DiscordShowAchievements = DiscordAchievementToggle.IsOn;
        S.DiscordShowMods = DiscordModsToggle.IsOn;
        S.Save();
        App.Discord.Configure(S, Locale.CurrentLanguage);
        RefreshDiscord();
    }

    private void DiscordReconnect_Click(object sender, RoutedEventArgs e)
    {
        App.Discord.Reconnect();
        RefreshDiscord();
    }

    private void RefreshDiscord()
    {
        var status = App.Discord.Snapshot;
        var key = status.Status switch
        {
            DiscordConnectionStatus.Connected => "connected",
            DiscordConnectionStatus.Connecting => "connecting",
            DiscordConnectionStatus.WaitingForDiscord => "waiting",
            DiscordConnectionStatus.Error => "error",
            _ => "disabled"
        };
        DiscordStatusText.Text = App.L("discord." + key);
        DiscordStatusHint.Text = App.L("discord." + key + "_hint");
        if (status.Status == DiscordConnectionStatus.Error && status.Error != null)
            DiscordStatusHint.Text += "\n" + status.Error;
        DiscordReconnectButton.IsEnabled = S.DiscordRpc && status.Status != DiscordConnectionStatus.Connecting;
        foreach (var toggle in new[] { DiscordServerToggle, DiscordDimensionToggle, DiscordAchievementToggle, DiscordModsToggle })
            toggle.IsEnabled = S.DiscordRpc;
        DiscordPreview.Visibility = S.DiscordRpc ? Visibility.Visible : Visibility.Collapsed;
        DiscordDetailsText.Text = status.Details;
        DiscordStateText.Text = status.State;
        DiscordTimeText.Visibility = status.StartedAt.HasValue ? Visibility.Visible : Visibility.Collapsed;
        if (status.StartedAt is { } start)
        {
            var elapsed = DateTimeOffset.UtcNow - start;
            if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
            DiscordTimeText.Text = App.L("discord.elapsed", $"{Math.Max(0, (int)elapsed.TotalHours):00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}");
        }
    }

    private void ExitEvent_Click(object sender, RoutedEventArgs e)
    {
        App.ClearEvent();
        EventCard.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
        if (App.MainWindow is MainWindow mw)
            mw.ApplyEventNavigation();
    }

}
