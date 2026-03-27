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

        for (int i = 0; i < LangSelector.Items.Count; i++)
        {
            if ((LangSelector.Items[i] as ComboBoxItem)?.Tag?.ToString() == Locale.CurrentLanguage)
            { LangSelector.SelectedIndex = i; break; }
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
        PageTitle.Text = App.L("set.title");
        AppearanceLabel.Text = App.L("set.appearance");
        LauncherLabel.Text = App.L("set.launcher");
        ThemeSelector.Header = App.L("set.theme");
        ThemeDark.Content = App.L("set.dark");
        ThemeLight.Content = App.L("set.light");
        ThemeSystem.Content = App.L("set.system");
        CloseOnLaunchToggle.Header = App.L("set.close_on_launch");
        ShowSnapshotsToggle.Header = App.L("set.show_snapshots");
        LangSelector.Header = App.L("set.language");
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

    public static string GetRandomSplash() => Splashes[Random.Shared.Next(Splashes.Length)];
}
