using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace MechanicaLauncher.Views;

public sealed partial class SettingsPage : Page
{
    private static Core.Profiles.LauncherSettings S => App.Settings;
    private bool _loading;

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
}
