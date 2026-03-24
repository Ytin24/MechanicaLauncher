using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using MechanicaLauncher.Core.Instances;
using MechanicaLauncher.Core.Mods;
using MechanicaLauncher.Core.Models;
using MechanicaLauncher.Core.Profiles;

namespace MechanicaLauncher.Views;

public sealed partial class ModsPage : Page
{
    private static readonly SolidColorBrush CardBg = new(Windows.UI.Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush Dim = new(Windows.UI.Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush Subtle = new(Windows.UI.Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush Green = new(Windows.UI.Color.FromArgb(0xFF, 0x4C, 0xAF, 0x50));
    private static readonly SolidColorBrush White = new(Microsoft.UI.Colors.White);

    private readonly InstanceManager _im = new();
    private readonly LauncherSettings _settings = LauncherSettings.Load();
    private readonly ModrinthClient _modrinth = new();
    private GameInstance? _instance;
    private string _modsDir = "";
    private string _lastQuery = "";

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
            InstanceLabel.Text = "No instance selected";
            ModCountLabel.Text = "0";
            InstalledModsPanel.Children.Add(MakeText("Select an instance on the Home page first."));
            return;
        }

        InstanceLabel.Text = $"{_instance.Name}  ·  {_instance.McVersion}  ·  {_instance.Loader}";
        _modsDir = Path.Combine(_im.GetGameDir(_instance.Id), "mods");
        var mods = ModInstaller.GetInstalledMods(_modsDir);
        ModCountLabel.Text = mods.Count(m => m.Enabled).ToString();

        if (mods.Count == 0)
        {
            InstalledModsPanel.Children.Add(MakeText("No mods installed yet."));
            return;
        }

        int delay = 0;
        foreach (var mod in mods)
        {
            var card = new Border
            {
                Background = CardBg, CornerRadius = new CornerRadius(10),
                Padding = new Thickness(14, 10, 14, 10), MinHeight = 48,
                RenderTransform = new TranslateTransform { Y = 20 },
                Opacity = 0
            };

            var grid = new Grid
            {
                ColumnDefinitions =
                {
                    new ColumnDefinition(),
                    new ColumnDefinition { Width = GridLength.Auto },
                    new ColumnDefinition { Width = GridLength.Auto }
                }
            };

            var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 1 };
            info.Children.Add(new TextBlock
            {
                Text = mod.FileName, FontSize = 13,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 200
            });
            info.Children.Add(new TextBlock { Text = mod.SizeFormatted, FontSize = 11, Foreground = Dim });

            var toggle = new ToggleSwitch
            {
                IsOn = mod.Enabled, VerticalAlignment = VerticalAlignment.Center,
                MinWidth = 0, Tag = mod.FilePath
            };
            toggle.Toggled += (s, _) =>
            {
                if (s is ToggleSwitch ts && ts.Tag is string path)
                {
                    ModInstaller.ToggleMod(path);
                    LoadInstance();
                }
            };
            Grid.SetColumn(toggle, 1);

            var delBtn = new Button
            {
                Content = new FontIcon { Glyph = "\uE74D", FontSize = 11 },
                Padding = new Thickness(5), MinWidth = 28, MinHeight = 28,
                CornerRadius = new CornerRadius(6), VerticalAlignment = VerticalAlignment.Center,
                Tag = mod.FilePath
            };
            delBtn.Click += (s, _) =>
            {
                if (s is Button b && b.Tag is string path)
                {
                    ModInstaller.RemoveMod(path);
                    LoadInstance();
                }
            };
            Grid.SetColumn(delBtn, 2);

            grid.Children.Add(info);
            grid.Children.Add(toggle);
            grid.Children.Add(delBtn);
            card.Child = grid;
            InstalledModsPanel.Children.Add(card);

            AnimateEntrance(card, delay);
            delay += 30;
        }
    }

    private async void ModSearch_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        _lastQuery = args.QueryText?.Trim() ?? "";
        await DoSearch();
    }

    private async void Filter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_lastQuery))
            await DoSearch();
    }

    private async Task DoSearch()
    {
        if (_instance == null) return;

        SearchResultsPanel.Children.Clear();
        SearchResultsPanel.Children.Add(new ProgressRing { IsActive = true, Width = 24, Height = 24, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 20, 0, 0) });
        ResultsInfo.Text = "Searching...";

        try
        {
            var loader = _instance.Loader != LoaderType.None ? _instance.Loader.ToString().ToLowerInvariant() : null;
            var type = (TypeFilter.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "mod";
            var sort = (SortFilter.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "relevance";
            var category = (CategoryFilter.SelectedItem as ComboBoxItem)?.Tag?.ToString();
            if (string.IsNullOrEmpty(category)) category = null;

            var result = await _modrinth.SearchAsync(
                _lastQuery, _instance.McVersion, loader,
                projectType: type, category: category,
                sortBy: sort, limit: 30);

            SearchResultsPanel.Children.Clear();
            ResultsInfo.Text = $"{result.TotalHits} results";

            int delay = 0;
            foreach (var project in result.Hits)
            {
                var card = new Border
                {
                    Background = CardBg, CornerRadius = new CornerRadius(10),
                    Padding = new Thickness(16), MinHeight = 72,
                    RenderTransform = new TranslateTransform { Y = 20 },
                    Opacity = 0
                };

                var grid = new Grid
                {
                    ColumnDefinitions =
                    {
                        new ColumnDefinition(),
                        new ColumnDefinition { Width = GridLength.Auto }
                    }
                };

                var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 4 };

                var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                titleRow.Children.Add(new TextBlock
                {
                    Text = project.Title, FontSize = 14,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
                });
                info.Children.Add(titleRow);

                info.Children.Add(new TextBlock
                {
                    Text = project.Description, FontSize = 12,
                    MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis,
                    Foreground = Subtle, TextWrapping = TextWrapping.Wrap
                });

                var metaRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
                metaRow.Children.Add(new TextBlock { Text = $"↓ {project.DownloadsFormatted}", FontSize = 11, Foreground = Dim });

                if (project.Categories.Count > 0)
                {
                    var cats = string.Join(", ", project.Categories.Take(3));
                    metaRow.Children.Add(new TextBlock { Text = cats, FontSize = 11, Foreground = Dim });
                }
                info.Children.Add(metaRow);

                var installBtn = new Button
                {
                    Style = (Style)Application.Current.Resources["AccentSmallButton"],
                    VerticalAlignment = VerticalAlignment.Center,
                    Tag = project.ProjectId, MinWidth = 76, MinHeight = 32,
                    Content = "Install"
                };
                installBtn.Click += InstallMod_Click;
                Grid.SetColumn(installBtn, 1);

                grid.Children.Add(info);
                grid.Children.Add(installBtn);
                card.Child = grid;
                SearchResultsPanel.Children.Add(card);

                AnimateEntrance(card, delay);
                delay += 40;
            }

            if (result.Hits.Count == 0)
            {
                SearchResultsPanel.Children.Add(MakeText("No results found."));
                ResultsInfo.Text = "0 results";
            }
        }
        catch (Exception ex)
        {
            SearchResultsPanel.Children.Clear();
            SearchResultsPanel.Children.Add(new TextBlock
            {
                Text = $"Error: {ex.Message}",
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xFF, 0x66, 0x66)),
                TextWrapping = TextWrapping.Wrap
            });
            ResultsInfo.Text = "Error";
        }
    }

    private async void InstallMod_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string projectId || _instance == null) return;

        btn.IsEnabled = false;
        btn.Content = new ProgressRing { IsActive = true, Width = 14, Height = 14 };

        try
        {
            var loader = _instance.Loader != LoaderType.None ? _instance.Loader.ToString().ToLowerInvariant() : null;
            var versions = await _modrinth.GetProjectVersionsAsync(projectId, _instance.McVersion, loader);
            var version = versions.FirstOrDefault();
            if (version == null) { btn.Content = "N/A"; return; }

            var installer = new ModInstaller();
            await installer.InstallModAsync(version, _modsDir, _instance.McVersion, loader);

            btn.Content = new FontIcon { Glyph = "\uE73E", FontSize = 14, Foreground = White };
            LoadInstance();
        }
        catch
        {
            btn.Content = "Error";
            btn.IsEnabled = true;
        }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_modsDir) && Directory.Exists(_modsDir))
            Process.Start(new ProcessStartInfo { FileName = _modsDir, UseShellExecute = true });
    }

    private void RefreshMods_Click(object sender, RoutedEventArgs e) => LoadInstance();

    private static void AnimateEntrance(UIElement el, int delayMs)
    {
        var storyboard = new Storyboard();

        var fadeIn = new DoubleAnimation
        {
            From = 0, To = 1,
            Duration = new Duration(TimeSpan.FromMilliseconds(300)),
            BeginTime = TimeSpan.FromMilliseconds(delayMs),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(fadeIn, el);
        Storyboard.SetTargetProperty(fadeIn, "Opacity");

        var slideUp = new DoubleAnimation
        {
            From = 20, To = 0,
            Duration = new Duration(TimeSpan.FromMilliseconds(300)),
            BeginTime = TimeSpan.FromMilliseconds(delayMs),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(slideUp, el.RenderTransform);
        Storyboard.SetTargetProperty(slideUp, "Y");

        storyboard.Children.Add(fadeIn);
        storyboard.Children.Add(slideUp);
        storyboard.Begin();
    }

    private static TextBlock MakeText(string text) => new()
    {
        Text = text,
        Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)),
        FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center,
        Margin = new Thickness(0, 20, 0, 0)
    };
}
