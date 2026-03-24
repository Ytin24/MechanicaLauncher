using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace MechanicaLauncher.Views;

public sealed partial class SettingsPage : Page
{
    public SettingsPage() => this.InitializeComponent();

    private void MinMemSlider_Changed(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (MinMemText != null)
            MinMemText.Text = ((int)e.NewValue).ToString();
    }

    private void MaxMemSlider_Changed(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (MaxMemText != null)
            MaxMemText.Text = ((int)e.NewValue).ToString();
    }

    private void Theme_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (ThemeSelector?.SelectedItem is ComboBoxItem item && App.MainWindow?.Content is FrameworkElement root)
        {
            root.RequestedTheme = item.Content?.ToString() switch
            {
                "Dark" => ElementTheme.Dark,
                "Light" => ElementTheme.Light,
                _ => ElementTheme.Default
            };
        }
    }
}
