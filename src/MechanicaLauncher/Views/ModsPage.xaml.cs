using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using MechanicaLauncher.Core.Instances;
using MechanicaLauncher.Core.Mods;
using MechanicaLauncher.Core.Models;
using MechanicaLauncher.Helpers;

namespace MechanicaLauncher.Views;

public sealed partial class ModsPage : Page
{
    private static readonly SolidColorBrush CardBg = new(Windows.UI.Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush Dim = new(Windows.UI.Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush Subtle = new(Windows.UI.Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush White = new(Microsoft.UI.Colors.White);

    private static Core.Profiles.LauncherSettings S => App.Settings;
    private readonly InstanceManager _im = new();
    private readonly ModrinthClient _modrinth = new();
    private GameInstance? _instance;
    private string _modsDir = "";
    private string _lastQuery = "";
    private int _searchOffset;
    private string _selectedType = "mod";

    private static readonly (string Tag, string Label, string Glyph)[] ContentTypes =
    {
        ("mod", "Mods", "\uE8D2"),
        ("modpack", "Modpacks", "\uF133"),
        ("shader", "Shaders", "\uE706"),
        ("resourcepack", "Resource Packs", "\uEB9F"),
        ("datapack", "Datapacks", "\uE7B8"),
    };

    public ModsPage()
    {
        this.InitializeComponent();
        BuildTypeTabs();
    }

    private async Task ResolveModrinthInfoAsync(string jarPath, Grid iconGrid, TextBlock nameBlock, TextBlock subBlock)
    {
        string sha1;
        try
        {
            using var stream = File.OpenRead(jarPath);
            var hash = await System.Security.Cryptography.SHA1.HashDataAsync(stream);
            sha1 = Convert.ToHexString(hash).ToLowerInvariant();
        }
        catch { return; }

        ModrinthProjectInfo? proj;
        try { proj = await _modrinth.LookupProjectByHashAsync(sha1); }
        catch { return; }

        if (proj == null) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!string.IsNullOrEmpty(proj.Title)) nameBlock.Text = proj.Title;
            // Preserve filesize, append Modrinth description snippet.
            if (!string.IsNullOrEmpty(proj.Description))
            {
                var desc = proj.Description.Length > 80 ? proj.Description[..80] + "…" : proj.Description;
                subBlock.Text = desc;
            }
            if (!string.IsNullOrEmpty(proj.IconUrl))
            {
                iconGrid.Children.Clear();
                iconGrid.Children.Add(new Image
                {
                    Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(proj.IconUrl)),
                    Stretch = Microsoft.UI.Xaml.Media.Stretch.UniformToFill,
                });
            }
        });
    }

    private static Border BuildSkeletonCard()
    {
        var row = new StackPanel { Orientation = Orientation.Vertical, Spacing = 6 };
        var titleBar = new Border
        {
            Width = 180, Height = 14, CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        var subBar = new Border
        {
            Width = 300, Height = 10, CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x0D, 0xFF, 0xFF, 0xFF)),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        row.Children.Add(titleBar);
        row.Children.Add(subBar);
        return new Border
        {
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x0D, 0xFF, 0xFF, 0xFF)),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(16), MinHeight = 72,
            Child = row,
        };
    }

    private void BuildTypeTabs()
    {
        TypeTabs.Children.Clear();
        foreach (var (tag, label, glyph) in ContentTypes)
        {
            var active = tag == _selectedType;
            var btn = new Button
            {
                MinHeight = 32,
                Padding = new Thickness(12, 4, 12, 4),
                CornerRadius = new CornerRadius(16),
                BorderThickness = new Thickness(0),
                Background = new SolidColorBrush(active
                    ? Windows.UI.Color.FromArgb(0xFF, 0x4C, 0xAF, 0x50)
                    : Windows.UI.Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)),
                Foreground = new SolidColorBrush(active ? Microsoft.UI.Colors.White : Windows.UI.Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)),
                Tag = tag,
            };
            btn.Content = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 6,
                Children =
                {
                    new FontIcon { Glyph = glyph, FontSize = 13, VerticalAlignment = VerticalAlignment.Center },
                    new TextBlock { Text = label, FontSize = 12, VerticalAlignment = VerticalAlignment.Center }
                }
            };
            btn.Click += async (s, _) =>
            {
                var t = (string)((Button)s!).Tag!;
                if (t == _selectedType) return;
                _selectedType = t;
                BuildTypeTabs();
                await DoSearch();
            };
            TypeTabs.Children.Add(btn);
        }
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        PageTitle.Text = App.L("mods.title");
        InstalledLabel.Text = App.L("mods.installed");
        ModSearchBox.PlaceholderText = App.L("mods.search");

        var eu = App.EventConfig?.Ui;
        if (eu != null)
        {
            ModSearchBox.Visibility = eu.AllowModInstall ? Visibility.Visible : Visibility.Collapsed;
            OpenFolderBtn.Visibility = eu.AllowModInstall ? Visibility.Visible : Visibility.Collapsed;
        }

        LoadInstance();
    }

    private void LoadInstance()
    {
        _instance = S.SelectedInstanceId != null ? _im.GetInstance(S.SelectedInstanceId) : null;
        InstalledModsPanel.Children.Clear();

        if (_instance == null)
        {
            InstanceLabel.Text = App.L("mods.no_instance");
            ModCountLabel.Text = "0";
            InstalledModsPanel.Children.Add(MakeText(App.L("mods.no_instance")));
            return;
        }

        InstanceLabel.Text = $"{_instance.Name}  ·  {_instance.McVersion}  ·  {_instance.Loader}";
        _modsDir = Path.Combine(_im.GetGameDir(_instance.Id), "mods");
        var mods = ModInstaller.GetInstalledMods(_modsDir);
        ModCountLabel.Text = mods.Count(m => m.Enabled).ToString();

        // Kick off an empty search so the Modrinth side isn't an empty placeholder — shows top-ranked
        // mods/modpacks/shaders for the currently selected type on page open.
        _ = DoDefaultBrowseAsync();

        if (mods.Count == 0)
        {
            InstalledModsPanel.Children.Add(MakeText(App.L("mods.no_mods")));
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
                    new ColumnDefinition { Width = GridLength.Auto },
                    new ColumnDefinition(),
                    new ColumnDefinition { Width = GridLength.Auto },
                    new ColumnDefinition { Width = GridLength.Auto }
                }
            };

            // Icon placeholder — resolved async via Modrinth hash lookup.
            var iconBorder = new Border
            {
                Width = 36, Height = 36, CornerRadius = new CornerRadius(6),
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)),
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0),
            };
            var iconGrid = new Grid();
            iconGrid.Children.Add(new FontIcon { Glyph = "\uE8A5", FontSize = 16, Foreground = Dim,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
            iconBorder.Child = iconGrid;
            Grid.SetColumn(iconBorder, 0);

            var nameBlock = new TextBlock
            {
                Text = mod.FileName, FontSize = 14,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            var subBlock = new TextBlock
            {
                Text = mod.SizeFormatted, FontSize = 11, Foreground = Dim,
                TextTrimming = TextTrimming.CharacterEllipsis,
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            var info = new StackPanel
            {
                Orientation = Orientation.Vertical,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Spacing = 2,
            };
            info.Children.Add(nameBlock);
            info.Children.Add(subBlock);
            Grid.SetColumn(info, 1);

            _ = ResolveModrinthInfoAsync(mod.FilePath, iconGrid, nameBlock, subBlock);

            var toggle = new ToggleSwitch
            {
                IsOn = mod.Enabled, VerticalAlignment = VerticalAlignment.Center,
                MinWidth = 0, Tag = mod.FilePath,
                OnContent = "", OffContent = "",
            };
            toggle.Toggled += (s, _) =>
            {
                if (s is ToggleSwitch ts && ts.Tag is string path)
                {
                    ModInstaller.ToggleMod(path);
                    LoadInstance();
                }
            };
            Grid.SetColumn(toggle, 2);

            var delBtn = new Button
            {
                Content = new FontIcon { Glyph = "\uE74D", FontSize = 13 },
                Padding = new Thickness(6), MinWidth = 36, MinHeight = 36,
                CornerRadius = new CornerRadius(6), VerticalAlignment = VerticalAlignment.Center,
                Tag = mod.FilePath
            };
            delBtn.Click += async (s, _) =>
            {
                if (s is Button b && b.Tag is string path)
                {
                    var dialog = new ContentDialog
                    {
                        Title = App.L("gen.delete"),
                        Content = App.L("mods.delete_confirm"),
                        PrimaryButtonText = App.L("gen.delete"),
                        CloseButtonText = App.L("inst.cancel"),
                        XamlRoot = this.XamlRoot
                    };
                    if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
                    ModInstaller.RemoveMod(path);
                    LoadInstance();
                }
            };
            Grid.SetColumn(delBtn, 3);

            grid.Children.Add(iconBorder);
            grid.Children.Add(info);
            grid.Children.Add(toggle);
            grid.Children.Add(delBtn);
            card.Child = grid;
            InstalledModsPanel.Children.Add(card);
            AnimationHelper.SlideIn(card, delay);
            AnimationHelper.AddCardHover(card);
            delay += 30;
        }
    }

    private async void ModSearch_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        _lastQuery = args.QueryText?.Trim() ?? "";
        await DoSearch();
    }

    private CancellationTokenSource? _searchDebounceCts;

    private async void ModSearch_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        // Only fire on real user typing — programmatic text changes come through as SuggestionChosen.
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;

        var query = sender.Text?.Trim() ?? "";
        if (query == _lastQuery) return;

        // 300 ms debounce so we don't hammer Modrinth while the user is still typing.
        _searchDebounceCts?.Cancel();
        _searchDebounceCts = new CancellationTokenSource();
        var ct = _searchDebounceCts.Token;
        try { await Task.Delay(300, ct); }
        catch (TaskCanceledException) { return; }
        if (ct.IsCancellationRequested) return;

        _lastQuery = query;
        await DoSearch();
    }

    private async void Filter_Changed(object sender, SelectionChangedEventArgs e)
    {
        // Always re-search on filter change — even with empty query, so the user sees top results
        // for the chosen category/sort without having to type anything.
        await DoSearch();
    }

    private async Task DoDefaultBrowseAsync()
    {
        if (_instance == null) return;
        _lastQuery = "";
        await DoSearch();
    }

    private async Task DoSearch(bool append = false)
    {
        if (_instance == null) return;

        if (!append)
        {
            _searchOffset = 0;
            SearchResultsPanel.Children.Clear();
        }
        else
        {
            var loadMoreBtn = SearchResultsPanel.Children.LastOrDefault();
            if (loadMoreBtn is Button) SearchResultsPanel.Children.Remove(loadMoreBtn);
        }

        if (!append)
        {
            for (int i = 0; i < 5; i++)
                SearchResultsPanel.Children.Add(BuildSkeletonCard());
        }
        else
        {
            SearchResultsPanel.Children.Add(new ProgressRing { IsActive = true, Width = 22, Height = 22, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 12, 0, 0) });
        }
        ResultsInfo.Text = "Searching...";

        try
        {
            var type = _selectedType;
            var loader = (type == "mod" || type == "modpack")
                ? (_instance.Loader != LoaderType.None ? _instance.Loader.ToString().ToLowerInvariant() : null)
                : null;
            var sort = (SortFilter.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "relevance";
            var category = (CategoryFilter.SelectedItem as ComboBoxItem)?.Tag?.ToString();
            if (string.IsNullOrEmpty(category)) category = null;

            var result = await _modrinth.SearchAsync(
                _lastQuery, _instance.McVersion, loader,
                projectType: type, category: category,
                sortBy: sort, offset: _searchOffset, limit: 50);

            // Clear skeletons / spinner — the append path leaves existing cards.
            if (!append) SearchResultsPanel.Children.Clear();
            else SearchResultsPanel.Children.RemoveAt(SearchResultsPanel.Children.Count - 1);
            _searchOffset += result.Hits.Count;
            ResultsInfo.Text = App.L("mods.results", result.TotalHits);

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
                    Content = App.L("mods.install")
                };
                installBtn.Click += InstallMod_Click;
                Grid.SetColumn(installBtn, 1);

                grid.Children.Add(info);
                grid.Children.Add(installBtn);
                card.Child = grid;
                SearchResultsPanel.Children.Add(card);
                AnimationHelper.SlideIn(card, delay);
                AnimationHelper.AddCardHover(card);
                delay += 40;
            }

            if (result.Hits.Count == 0 && _searchOffset == 0)
            {
                SearchResultsPanel.Children.Add(MakeText(App.L("mods.no_results")));
                ResultsInfo.Text = "0 results";
            }

            if (_searchOffset < result.TotalHits)
            {
                var loadMore = new Button
                {
                    Content = App.L("mods.load_more", result.TotalHits - _searchOffset),
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Center,
                    MinHeight = 40, CornerRadius = new CornerRadius(8),
                    Margin = new Thickness(0, 4, 0, 0)
                };
                loadMore.Click += async (_, _) => await DoSearch(append: true);
                SearchResultsPanel.Children.Add(loadMore);
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
            var gameDir = Path.GetDirectoryName(_modsDir)!;
            await installer.InstallModAsync(version, _modsDir, _instance.McVersion, loader, gameDir);

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


    private static TextBlock MakeText(string text) => new()
    {
        Text = text,
        Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)),
        FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center,
        Margin = new Thickness(0, 20, 0, 0)
    };
}
