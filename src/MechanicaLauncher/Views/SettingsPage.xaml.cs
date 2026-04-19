using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using MechanicaLauncher.Core.Localization;
using MechanicaLauncher.Core.Updates;

namespace MechanicaLauncher.Views;

public sealed partial class SettingsPage : Page
{
    private static Core.Profiles.LauncherSettings S => App.Settings;
    private bool _loading;
    private UpdateInfo? _updateInfo;

    private static readonly string[] Splashes =
    [
        "Also try Terraria!", "Woo, /give!", "Reticulating splines...",
        "Now with extra cats!", "Contains 100% recycled pixels",
        "Blocks all the way down", "sudo rm -rf /boredom",
        "git push --force-with-cats", "async all the things!",
        "Powered by mass caffeine consumption", "Mass production of fun!",
        "Now with Mica!", "42 is the answer", "Hello from .NET 9!",
        "Compiled with love", "Not a Prism!",
    ];

    public SettingsPage()
    {
        this.InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _loading = true;

        ApplyLocale();

        ThemeSelector.SelectedIndex = S.Theme switch { "Dark" => 0, "Light" => 1, _ => 2 };
        CloseOnLaunchToggle.IsOn = S.CloseOnLaunch;
        ShowSnapshotsToggle.IsOn = S.ShowSnapshots;
        DiscordRpcToggle.IsOn = S.DiscordRpc;
        DiscordServerToggle.IsOn = S.DiscordShowServer;
        DiscordDimensionToggle.IsOn = S.DiscordShowDimension;
        DiscordAchievementToggle.IsOn = S.DiscordShowAchievements;
        DiscordModsToggle.IsOn = S.DiscordShowMods;
        // SettingsCard hosts the label/description now; clear the ToggleSwitch's own header so it
        // doesn't render on top of the card.
        // SettingsCard hosts the label/description now; clear the ToggleSwitch's own header + the
        // default "Вкл/Выкл" captions so it doesn't render on top of the card.
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

        _ = CheckUpdatesAsync();
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
        try
        {
            PageTitle.Text = App.L("set.title");
            AppearanceLabel.Text = App.L("set.appearance");
            LauncherLabel.Text = App.L("set.launcher");
            ThemeCard.Header = App.L("set.theme");
            LangCard.Header = App.L("set.language");
            ThemeDark.Content = App.L("set.dark");
            ThemeLight.Content = App.L("set.light");
            ThemeSystem.Content = App.L("set.system");
            CloseOnLaunchCard.Header = App.L("set.close_on_launch");
            ShowSnapshotsCard.Header = App.L("set.show_snapshots");
        }
        catch { }
    }

    private void Theme_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ThemeSelector?.SelectedItem is not ComboBoxItem item) return;
        var theme = item.Content?.ToString() ?? "Dark";
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
        ApplyLocale();
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
        if (S.DiscordRpc) App.Discord.Init();
        else App.Discord.Dispose();
    }

    private void DiscordSetting_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        S.DiscordShowServer = DiscordServerToggle.IsOn;
        S.DiscordShowDimension = DiscordDimensionToggle.IsOn;
        S.DiscordShowAchievements = DiscordAchievementToggle.IsOn;
        S.DiscordShowMods = DiscordModsToggle.IsOn;
        S.Save();
    }

    private void ExitEvent_Click(object sender, RoutedEventArgs e)
    {
        App.ClearEvent();
        EventCard.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
        if (App.MainWindow is MainWindow mw)
            mw.ApplyEventNavigation();
    }

    public static string GetRandomSplash() => Splashes[Random.Shared.Next(Splashes.Length)];
}
