using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using MechanicaLauncher.Core.Profiles;

namespace MechanicaLauncher.Views;

public sealed partial class SettingsPage : Page
{
    private readonly LauncherSettings _settings = LauncherSettings.Load();

    public SettingsPage()
    {
        this.InitializeComponent();
        LoadSettings();
    }

    private void LoadSettings()
    {
        MinMemSlider.Value = _settings.MinMemoryMb;
        MaxMemSlider.Value = _settings.MaxMemoryMb;
        JvmArgsBox.Text = _settings.JvmArgs;
        JavaPathBox.Text = _settings.JavaPath ?? "";

        ThemeSelector.SelectedIndex = _settings.Theme switch
        {
            "Dark" => 0,
            "Light" => 1,
            _ => 2
        };
    }

    private void MinMemSlider_Changed(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (MinMemText != null) MinMemText.Text = ((int)e.NewValue).ToString();
        _settings.MinMemoryMb = (int)e.NewValue;
        _settings.Save();
    }

    private void MaxMemSlider_Changed(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (MaxMemText != null) MaxMemText.Text = ((int)e.NewValue).ToString();
        _settings.MaxMemoryMb = (int)e.NewValue;
        _settings.Save();
    }

    private void Theme_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (ThemeSelector?.SelectedItem is not ComboBoxItem item) return;

        var themeName = item.Content?.ToString() ?? "Dark";
        _settings.Theme = themeName;
        _settings.Save();

        if (App.MainWindow?.Content is FrameworkElement root)
        {
            root.RequestedTheme = themeName switch
            {
                "Light" => ElementTheme.Light,
                "Dark" => ElementTheme.Dark,
                _ => ElementTheme.Default
            };
        }
    }
}
