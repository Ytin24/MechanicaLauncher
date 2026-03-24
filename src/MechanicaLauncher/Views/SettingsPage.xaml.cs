using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MechanicaLauncher.Core.Profiles;

namespace MechanicaLauncher.Views;

public sealed partial class SettingsPage : Page
{
    private readonly LauncherSettings _settings = LauncherSettings.Load();

    public SettingsPage()
    {
        this.InitializeComponent();
        ThemeSelector.SelectedIndex = _settings.Theme switch
        {
            "Dark" => 0,
            "Light" => 1,
            _ => 2
        };
    }

    private void Theme_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (ThemeSelector?.SelectedItem is not ComboBoxItem item) return;
        var theme = item.Content?.ToString() ?? "Dark";
        _settings.Theme = theme;
        _settings.Save();

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
}
