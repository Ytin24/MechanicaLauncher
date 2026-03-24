using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace MechanicaLauncher.Views;

public sealed partial class ModsPage : Page
{
    public ModsPage()
    {
        this.InitializeComponent();
        LoadInstalledMods();
    }

    private void LoadInstalledMods()
    {
        var modsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            ".minecraft", "mods");

        if (!Directory.Exists(modsDir)) return;

        foreach (var file in Directory.GetFiles(modsDir, "*.jar"))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            InstalledModsPanel.Children.Add(CreateModCard(name, true));
        }

        foreach (var file in Directory.GetFiles(modsDir, "*.jar.disabled"))
        {
            var name = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(file));
            InstalledModsPanel.Children.Add(CreateModCard(name, false));
        }
    }

    private Border CreateModCard(string name, bool enabled)
    {
        var card = new Border
        {
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF)),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(16, 12, 16, 12),
        };

        var grid = new Grid { ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } } };

        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        info.Children.Add(new TextBlock { Text = name, FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });

        var toggle = new ToggleSwitch { IsOn = enabled, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(toggle, 1);

        grid.Children.Add(info);
        grid.Children.Add(toggle);
        card.Child = grid;
        return card;
    }

    private void ModSearch_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        SearchResultsPanel.Children.Clear();

        var placeholder = new Border
        {
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF)),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(16),
        };
        placeholder.Child = new TextBlock
        {
            Text = $"Searching \"{args.QueryText}\"... (Modrinth API not yet connected)",
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)),
            FontSize = 13,
        };
        SearchResultsPanel.Children.Add(placeholder);
    }
}
