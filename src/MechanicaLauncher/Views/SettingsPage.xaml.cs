using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace MechanicaLauncher.Views;

public sealed partial class SettingsPage : Page
{
    private static Core.Profiles.LauncherSettings S => App.Settings;
    private bool _loading;

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
        ThemeSelector.SelectedIndex = S.Theme switch
        {
            "Dark" => 0,
            "Light" => 1,
            _ => 2
        };
        CloseOnLaunchToggle.IsOn = S.CloseOnLaunch;
        ShowSnapshotsToggle.IsOn = S.ShowSnapshots;
        _loading = false;
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
                "Light" => ElementTheme.Light,
                "Dark" => ElementTheme.Dark,
                _ => ElementTheme.Default
            };
        }
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
