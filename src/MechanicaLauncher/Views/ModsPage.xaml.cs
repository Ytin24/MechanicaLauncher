using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using MechanicaLauncher.Core.Instances;
using MechanicaLauncher.Core.Mods;
using MechanicaLauncher.Core.Models;
using MechanicaLauncher.Core.Profiles;

namespace MechanicaLauncher.Views;

public sealed partial class ModsPage : Page
{
    private readonly InstanceManager _im = new();
    private readonly LauncherSettings _settings = LauncherSettings.Load();
    private readonly ModrinthClient _modrinth = new();
    private GameInstance? _instance;
    private string _modsDir = "";

    public ModsPage()
    {
        this.InitializeComponent();
        LoadInstance();
    }

    private void LoadInstance()
    {
        _instance = _settings.SelectedInstanceId != null ? _im.GetInstance(_settings.SelectedInstanceId) : null;

        InstalledModsPanel.Children.Clear();

        if (_instance == null)
        {
            InstalledModsPanel.Children.Add(new TextBlock
            {
                Text = "Select an instance first.",
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)),
                FontSize = 13
            });
            return;
        }

        _modsDir = Path.Combine(_im.GetGameDir(_instance.Id), "mods");
        var mods = ModInstaller.GetInstalledMods(_modsDir);

        foreach (var mod in mods)
        {
            var card = new Border
            {
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF)),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(16, 12, 16, 12),
                MinHeight = 48,
            };

            var grid = new Grid { ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto }, new ColumnDefinition { Width = GridLength.Auto } } };

            var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            info.Children.Add(new TextBlock { Text = mod.FileName, FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            info.Children.Add(new TextBlock { Text = mod.SizeFormatted, FontSize = 11, Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)) });

            var toggle = new ToggleSwitch { IsOn = mod.Enabled, VerticalAlignment = VerticalAlignment.Center, Tag = mod.FilePath };
            toggle.Toggled += (s, _) =>
            {
                if (s is ToggleSwitch ts && ts.Tag is string path)
                    ModInstaller.ToggleMod(path);
            };
            Grid.SetColumn(toggle, 1);

            var deleteBtn = new Button
            {
                Content = new FontIcon { Glyph = "\uE74D", FontSize = 12 },
                Padding = new Thickness(6), MinWidth = 32, MinHeight = 32,
                CornerRadius = new CornerRadius(6),
                VerticalAlignment = VerticalAlignment.Center,
                Tag = mod.FilePath
            };
            deleteBtn.Click += (s, _) =>
            {
                if (s is Button b && b.Tag is string path)
                {
                    ModInstaller.RemoveMod(path);
                    LoadInstance();
                }
            };
            Grid.SetColumn(deleteBtn, 2);

            grid.Children.Add(info);
            grid.Children.Add(toggle);
            grid.Children.Add(deleteBtn);
            card.Child = grid;
            InstalledModsPanel.Children.Add(card);
        }

        if (mods.Count == 0)
        {
            InstalledModsPanel.Children.Add(new TextBlock
            {
                Text = "No mods installed.",
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)),
                FontSize = 13
            });
        }
    }

    private async void ModSearch_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        var query = args.QueryText?.Trim();
        if (string.IsNullOrEmpty(query) || _instance == null) return;

        SearchResultsPanel.Children.Clear();
        SearchResultsPanel.Children.Add(new ProgressRing { IsActive = true, Width = 24, Height = 24, HorizontalAlignment = HorizontalAlignment.Center });

        try
        {
            var loader = _instance.Loader != LoaderType.None ? _instance.Loader.ToString().ToLowerInvariant() : null;
            var result = await _modrinth.SearchAsync(query, _instance.McVersion, loader);

            SearchResultsPanel.Children.Clear();

            foreach (var project in result.Hits)
            {
                var card = new Border
                {
                    Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF)),
                    CornerRadius = new CornerRadius(10),
                    Padding = new Thickness(16),
                    MinHeight = 64,
                };

                var grid = new Grid { ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } } };

                var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
                info.Children.Add(new TextBlock { Text = project.Title, FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
                info.Children.Add(new TextBlock
                {
                    Text = project.Description,
                    FontSize = 12,
                    MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis,
                    Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF))
                });
                info.Children.Add(new TextBlock
                {
                    Text = $"{project.DownloadsFormatted} downloads",
                    FontSize = 11,
                    Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF))
                });

                var installBtn = new Button
                {
                    Content = "Install",
                    Style = (Style)Application.Current.Resources["AccentSmallButton"],
                    VerticalAlignment = VerticalAlignment.Center,
                    Tag = project.ProjectId
                };
                installBtn.Click += InstallMod_Click;
                Grid.SetColumn(installBtn, 1);

                grid.Children.Add(info);
                grid.Children.Add(installBtn);
                card.Child = grid;
                SearchResultsPanel.Children.Add(card);
            }

            if (result.Hits.Count == 0)
            {
                SearchResultsPanel.Children.Add(new TextBlock
                {
                    Text = "No mods found.",
                    Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)),
                    HorizontalAlignment = HorizontalAlignment.Center, FontSize = 13
                });
            }
        }
        catch (Exception ex)
        {
            SearchResultsPanel.Children.Clear();
            SearchResultsPanel.Children.Add(new TextBlock { Text = $"Error: {ex.Message}", Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xFF, 0x66, 0x66)), TextWrapping = TextWrapping.Wrap });
        }
    }

    private async void InstallMod_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string projectId || _instance == null) return;

        btn.IsEnabled = false;
        btn.Content = "...";

        try
        {
            var loader = _instance.Loader != LoaderType.None ? _instance.Loader.ToString().ToLowerInvariant() : null;
            var versions = await _modrinth.GetProjectVersionsAsync(projectId, _instance.McVersion, loader);
            var version = versions.FirstOrDefault();
            if (version == null) { btn.Content = "N/A"; return; }

            var installer = new ModInstaller();
            await installer.InstallModAsync(version, _modsDir, _instance.McVersion, loader);

            btn.Content = "Done";
            LoadInstance();
        }
        catch
        {
            btn.Content = "Error";
        }
    }
}
