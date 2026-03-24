using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using MechanicaLauncher.Core.Game;
using MechanicaLauncher.Core.Models;
using MechanicaLauncher.Core.Profiles;

namespace MechanicaLauncher.Views;

public sealed partial class VersionsPage : Page
{
    private readonly LauncherSettings _settings = LauncherSettings.Load();
    private readonly VersionManager _vm;

    public VersionsPage()
    {
        this.InitializeComponent();
        _vm = new VersionManager(_settings.GameDir);
        _ = LoadVersionsAsync();
    }

    private async Task LoadVersionsAsync()
    {
        try
        {
            var installed = _vm.GetInstalledVersions();
            var manifest = await _vm.GetManifestAsync();

            if (installed.Count > 0)
            {
                AddSection("INSTALLED");
                foreach (var id in installed)
                    AddVersionCard(id, true, id == manifest.Latest.Release);
            }

            AddSection("AVAILABLE");
            var releases = manifest.Versions
                .Where(v => v.Type == "release" && !installed.Contains(v.Id))
                .Take(20);

            foreach (var v in releases)
                AddVersionCard(v.Id, false, v.Id == manifest.Latest.Release);
        }
        catch (Exception ex)
        {
            VersionsList.Children.Add(new TextBlock
            {
                Text = $"Failed to load: {ex.Message}",
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0x99, 0xFF, 0x66, 0x66)),
                TextWrapping = TextWrapping.Wrap
            });
        }
        finally
        {
            LoadingRing.IsActive = false;
            LoadingRing.Visibility = Visibility.Collapsed;
        }
    }

    private void AddSection(string title)
    {
        VersionsList.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 14,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)),
            CharacterSpacing = 60,
            Margin = new Thickness(0, VersionsList.Children.Count > 0 ? 16 : 0, 0, 8)
        });
    }

    private void AddVersionCard(string id, bool isInstalled, bool isLatest)
    {
        var card = new Border
        {
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF)),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(20),
        };

        var grid = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition(),
                new ColumnDefinition { Width = GridLength.Auto }
            }
        };

        var iconBorder = new Border
        {
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x2D, 0x2D, 0x2D)),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12),
            VerticalAlignment = VerticalAlignment.Center
        };
        var iconColor = isInstalled
            ? Windows.UI.Color.FromArgb(0xFF, 0x4C, 0xAF, 0x50)
            : isLatest
                ? Windows.UI.Color.FromArgb(0xFF, 0xFF, 0x98, 0x00)
                : Windows.UI.Color.FromArgb(0xFF, 0x88, 0x88, 0x88);
        iconBorder.Child = new FontIcon { Glyph = "\uE74C", FontSize = 22, Foreground = new SolidColorBrush(iconColor) };

        var info = new StackPanel { Margin = new Thickness(16, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Spacing = 4 };
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        titleRow.Children.Add(new TextBlock { Text = id, FontSize = 17, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });

        if (isLatest)
        {
            var badge = new Border
            {
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xFF, 0x98, 0x00)),
                CornerRadius = new CornerRadius(4), Padding = new Thickness(8, 3, 8, 3),
                VerticalAlignment = VerticalAlignment.Center
            };
            badge.Child = new TextBlock { Text = "Latest", FontSize = 10, Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
            titleRow.Children.Add(badge);
        }

        info.Children.Add(titleRow);
        info.Children.Add(new TextBlock
        {
            Text = isInstalled ? "Installed" : "Available",
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)), FontSize = 12
        });

        Grid.SetColumn(info, 1);

        var btn = new Button
        {
            Content = isInstalled ? "Play" : "Install",
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 13, Padding = new Thickness(16, 6, 16, 6),
            CornerRadius = new CornerRadius(6)
        };
        if (isInstalled)
        {
            btn.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x4C, 0xAF, 0x50));
            btn.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF));
        }
        Grid.SetColumn(btn, 2);

        grid.Children.Add(iconBorder);
        grid.Children.Add(info);
        grid.Children.Add(btn);
        card.Child = grid;
        VersionsList.Children.Add(card);
    }
}
