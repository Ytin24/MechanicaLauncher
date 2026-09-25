using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using MechanicaLauncher.Core.Instances;
using MechanicaLauncher.Core.Mods;
using MechanicaLauncher.Core.Models;
using MechanicaLauncher.Helpers;

namespace MechanicaLauncher.Views;

public sealed partial class ModsPage : Page
{
    private static Core.Profiles.LauncherSettings S => App.Settings;
    private static Brush Brush(string key) => (Brush)Application.Current.Resources[key];
    private readonly InstanceManager _im = new();
    private readonly ModrinthClient _modrinth = new();
    private static readonly string[] ContentTypes = ["mod", "modpack", "shader", "resourcepack", "datapack"];
    private GameInstance? _instance;
    private string _selectedType = "mod";
    private string? _contentDir;
    private bool _installed;
    private bool _active;
    private bool _updating;
    private bool _searchComplete;
    private int _searchOffset;
    private CancellationTokenSource? _searchCancellation;
    private CancellationTokenSource? _debounce;
    private CancellationTokenSource? _installedCancellation;
    private readonly SemaphoreSlim _lookupGate = new(4);
    private string? SelectedMinecraftVersion => CompatibleOnly.IsChecked == true ? _instance?.McVersion : null;

    public ModsPage() => InitializeComponent();

    private void CheckMods_Click(object sender, RoutedEventArgs e)
    {
        if (_instance != null) Frame.Navigate(typeof(InstanceDetailsPage), new InstanceDetailsRequest(_instance.Id, "compatibility"));
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _updating = true;
        _active = true;
        PageTitle.Text = App.L("catalog.title");
        PageSubtitle.Text = App.L("catalog.subtitle");
        CatalogTab.Content = App.L("catalog.browse");
        InstalledTab.Content = App.L("catalog.installed");
        CheckModsButton.Content = App.L("feature.compatibility");
        ModSearchBox.PlaceholderText = App.L("mods.search");
        CompatibleOnly.Content = App.L("catalog.compatible");
        InstancePicker.PlaceholderText = App.L("catalog.select_instance");
        WorldPicker.PlaceholderText = App.L("catalog.select_world");
        AutomationProperties.SetName(InstancePicker, App.L("catalog.target"));
        AutomationProperties.SetName(WorldPicker, App.L("catalog.select_world"));
        AutomationProperties.SetName(SortFilter, App.L("mods.sort"));
        AutomationProperties.SetName(CategoryFilter, App.L("mods.category"));
        AutomationProperties.SetName(OpenFolderBtn, App.L("catalog.open_folder"));
        AutomationProperties.SetName(RefreshBtn, App.L("catalog.refresh"));
        ToolTipService.SetToolTip(OpenFolderBtn, App.L("catalog.open_folder"));
        ToolTipService.SetToolTip(RefreshBtn, App.L("catalog.refresh"));
        foreach (var item in SortFilter.Items.Cast<ComboBoxItem>()) item.Content = App.L("mods.sort." + item.Tag);
        var oldInstance = _instance;
        InstancePicker.Items.Clear();
        foreach (var instance in _im.GetAllInstances())
        {
            var item = new ComboBoxItem { Content = InstanceText(instance), Tag = instance };
            InstancePicker.Items.Add(item);
            if (instance.Id == S.SelectedInstanceId) InstancePicker.SelectedItem = item;
        }
        _instance = (InstancePicker.SelectedItem as ComboBoxItem)?.Tag as GameInstance;
        _updating = false;
        BuildTypeTabs();
        BuildCategories();
        RefreshWorlds();
        ShowTab();
        if (!_installed && (e.NavigationMode != NavigationMode.Back || !_searchComplete ||
            oldInstance?.Id != _instance?.Id || oldInstance?.McVersion != _instance?.McVersion || oldInstance?.Loader != _instance?.Loader))
            _ = SearchAsync();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _active = false;
        _searchCancellation?.Cancel();
        _debounce?.Cancel();
        _installedCancellation?.Cancel();
        base.OnNavigatedFrom(e);
    }

    internal static string InstanceText(GameInstance instance) =>
        $"{instance.Name} · {instance.McVersion} · {(instance.Loader == LoaderType.None ? "Vanilla" : instance.Loader)}";

    private void Header_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (InstancePicker == null) return;
        var compact = e.NewSize.Width < 680;
        Grid.SetRow(InstancePicker, compact ? 1 : 0);
        Grid.SetColumn(InstancePicker, compact ? 0 : 1);
        Grid.SetColumnSpan(InstancePicker, compact ? 2 : 1);
        InstancePicker.Margin = compact ? new Thickness(0, 8, 0, 0) : new Thickness(0);
    }

    private async void Instance_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_updating || !_active) return;
        _instance = (InstancePicker.SelectedItem as ComboBoxItem)?.Tag as GameInstance;
        S.SelectedInstanceId = _instance?.Id;
        S.Save();
        RefreshWorlds();
        ShowTab();
        if (!_installed) await SearchAsync();
    }

    private void BuildTypeTabs()
    {
        TypeTabs.Children.Clear();
        foreach (var type in ContentTypes)
        {
            var button = new Button
            {
                Content = App.L("mods.type." + type), Style = (Style)Application.Current.Resources["FilterChip"],
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Background = Brush(type == _selectedType ? "SelectionBrush" : "CardBrush"),
                Foreground = Brush(type == _selectedType ? "SelectionTextBrush" : "PrimaryBrush")
            };
            button.Click += async (_, _) =>
            {
                if (_selectedType == type) return;
                _selectedType = type;
                BuildTypeTabs();
                BuildCategories(reset: true);
                RefreshWorlds();
                ShowTab();
                if (!_installed) await SearchAsync();
            };
            TypeTabs.Children.Add(button);
        }
        LayoutTypeTabs();
    }

    private void TypeTabs_SizeChanged(object sender, SizeChangedEventArgs e) => LayoutTypeTabs();
    private void BuildCategories(bool reset = false)
    {
        _updating = true;
        var selected = reset ? "" : (CategoryFilter.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        CategoryFilter.Items.Clear();
        string[] categories = _selectedType switch
        {
            "shader" => ["vanilla-like", "realistic", "fantasy", "cartoon", "reflections", "shadows"],
            "resourcepack" => ["vanilla-like", "realistic", "simplistic", "themed", "16x", "32x", "64x", "128x"],
            "modpack" => ["adventure", "optimization", "technology", "magic", "quests", "lightweight", "multiplayer"],
            "datapack" => ["worldgen", "adventure", "mobs", "utility", "game-mechanics"],
            _ => ["optimization", "utility", "library", "decoration", "adventure", "technology", "magic", "storage", "worldgen"]
        };
        foreach (var category in new[] { "" }.Concat(categories))
        {
            var item = new ComboBoxItem { Tag = category, Content = App.L("mods.category." + category) };
            CategoryFilter.Items.Add(item);
            if (category == selected) CategoryFilter.SelectedItem = item;
        }
        if (CategoryFilter.SelectedIndex < 0) CategoryFilter.SelectedIndex = 0;
        _updating = false;
    }

    private void LayoutTypeTabs()
    {
        var columns = TypeTabs.ActualWidth >= 620 ? 5 : 3;
        TypeTabs.ColumnDefinitions.Clear();
        TypeTabs.RowDefinitions.Clear();
        for (var i = 0; i < columns; i++) TypeTabs.ColumnDefinitions.Add(new ColumnDefinition());
        for (var i = 0; i < (ContentTypes.Length + columns - 1) / columns; i++)
            TypeTabs.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (var i = 0; i < TypeTabs.Children.Count; i++)
        {
            Grid.SetRow((FrameworkElement)TypeTabs.Children[i], i / columns);
            Grid.SetColumn((FrameworkElement)TypeTabs.Children[i], i % columns);
        }
    }

    private async void Catalog_Click(object sender, RoutedEventArgs e)
    {
        if (!_installed) return;
        _installed = false;
        _installedCancellation?.Cancel();
        ShowTab();
        await SearchAsync();
    }

    private void Installed_Click(object sender, RoutedEventArgs e)
    {
        _installed = true;
        _searchCancellation?.Cancel();
        _debounce?.Cancel();
        ShowTab();
    }

    private void ShowTab()
    {
        CheckModsButton.Visibility = _selectedType == "mod" ? Visibility.Visible : Visibility.Collapsed;
        CheckModsButton.IsEnabled = _instance != null;
        BrowsePanel.Visibility = _installed ? Visibility.Collapsed : Visibility.Visible;
        InstalledPanel.Visibility = _installed ? Visibility.Visible : Visibility.Collapsed;
        CatalogTab.Background = Brush(_installed ? "CardBrush" : "SelectionBrush");
        CatalogTab.Foreground = Brush(_installed ? "PrimaryBrush" : "SelectionTextBrush");
        InstalledTab.Background = Brush(_installed ? "SelectionBrush" : "CardBrush");
        InstalledTab.Foreground = Brush(_installed ? "SelectionTextBrush" : "PrimaryBrush");
        CompatibleOnly.IsEnabled = _instance != null;
        CompatibleOnly.Content = _instance == null ? App.L("catalog.select_instance") :
            App.L("catalog.mc_filter", _instance.McVersion) +
            (_selectedType == "mod" && _instance.Loader != LoaderType.None ? " · " + _instance.Loader : "");
        if (_installed) LoadInstalled();
        OpenFolderBtn.Visibility = _installed && _contentDir != null ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RefreshWorlds()
    {
        _updating = true;
        var selected = (WorldPicker.SelectedItem as ComboBoxItem)?.Tag as string;
        WorldPicker.Items.Clear();
        WorldPicker.Visibility = _selectedType == "datapack" ? Visibility.Visible : Visibility.Collapsed;
        try
        {
            if (_instance != null)
            {
                var saves = Path.Combine(_im.GetGameDir(_instance.Id), "saves");
                if (Directory.Exists(saves))
                    foreach (var path in Directory.EnumerateDirectories(saves).Where(p => File.Exists(Path.Combine(p, "level.dat"))).Order())
                    {
                        var name = Path.GetFileName(path);
                        var item = new ComboBoxItem { Content = name, Tag = name };
                        WorldPicker.Items.Add(item);
                        if (name == selected) WorldPicker.SelectedItem = item;
                    }
            }
            if (WorldPicker.SelectedIndex < 0 && WorldPicker.Items.Count == 1) WorldPicker.SelectedIndex = 0;
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        finally { _updating = false; }
    }

    private void World_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_updating && _installed) LoadInstalled();
    }

    private void LoadInstalled()
    {
        _installedCancellation?.Cancel();
        _installedCancellation?.Dispose();
        _installedCancellation = new CancellationTokenSource();
        InstalledModsPanel.Children.Clear();
        _contentDir = null;
        OpenFolderBtn.Visibility = Visibility.Collapsed;
        if (_selectedType == "modpack") { InstalledInfo.Text = App.L("catalog.packs_installed"); return; }
        if (_instance == null) { InstalledInfo.Text = App.L("catalog.select_instance"); return; }
        var world = (WorldPicker.SelectedItem as ComboBoxItem)?.Tag as string;
        if (_selectedType == "datapack" && world == null)
        {
            InstalledInfo.Text = App.L(WorldPicker.Items.Count == 0 ? "catalog.no_worlds" : "catalog.select_world");
            return;
        }
        try
        {
            _contentDir = ModInstaller.GetContentDirectory(_im.GetGameDir(_instance.Id), _selectedType, world);
            var mods = ModInstaller.GetInstalledMods(_contentDir, _selectedType == "mod" ? ".jar" : ".zip");
            InstalledInfo.Text = mods.Count == 0 ? App.L("catalog.empty") : App.L("catalog.file_count", mods.Count);
            OpenFolderBtn.Visibility = Visibility.Visible;
            foreach (var mod in mods) AddInstalledCard(mod, _instance.Id);
        }
        catch (Exception ex) { InstalledInfo.Text = ex.Message; }
    }

    private void AddInstalledCard(InstalledMod mod, string instanceId)
    {
        var row = new Grid { ColumnSpacing = 12 };
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        var name = new TextBlock { Text = mod.FileName, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = Brush("PrimaryBrush"), TextTrimming = TextTrimming.CharacterEllipsis };
        text.Children.Add(name);
        text.Children.Add(new TextBlock { Text = $"{mod.SizeFormatted} · {App.L(mod.Enabled ? "catalog.enabled" : "catalog.disabled")}",
            FontSize = 12, Foreground = Brush("SubtleBrush") });
        ToolTipService.SetToolTip(text, mod.FileName);
        _ = ResolveInstalledNameAsync(mod.FilePath, name, _installedCancellation!.Token);
        row.Children.Add(text);
        var toggle = new ToggleSwitch { IsOn = mod.Enabled, OnContent = "", OffContent = "", MinWidth = 0, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(toggle, App.L("catalog.enable_file", mod.FileName));
        toggle.Toggled += (_, _) =>
        {
            if (App.IsInstanceBusy(instanceId)) { LoadInstalled(); InstalledInfo.Text = App.L("inst.busy"); return; }
            try { ModInstaller.ToggleMod(mod.FilePath); LoadInstalled(); }
            catch (Exception ex) { LoadInstalled(); InstalledInfo.Text = ex.Message; }
        };
        Grid.SetColumn(toggle, 1);
        row.Children.Add(toggle);
        var delete = new Button { Content = new FontIcon { Glyph = "\uE74D", FontSize = 13 },
            Padding = new Thickness(8), MinWidth = 36, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(delete, App.L("gen.delete") + " " + mod.FileName);
        ToolTipService.SetToolTip(delete, App.L("gen.delete"));
        delete.Click += async (_, _) =>
        {
            var dialog = new ContentDialog { Title = App.L("gen.delete"), Content = App.L("catalog.delete_file", mod.FileName),
                PrimaryButtonText = App.L("gen.delete"), CloseButtonText = App.L("inst.cancel"), XamlRoot = XamlRoot, RequestedTheme = ActualTheme };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            if (App.IsInstanceBusy(instanceId)) { InstalledInfo.Text = App.L("inst.busy"); return; }
            try { ModInstaller.RemoveMod(mod.FilePath); LoadInstalled(); }
            catch (Exception ex) { InstalledInfo.Text = ex.Message; }
        };
        Grid.SetColumn(delete, 2);
        row.Children.Add(delete);
        InstalledModsPanel.Children.Add(new Border { Style = (Style)Application.Current.Resources["CardPanelWide"], Child = row });
    }

    private async Task ResolveInstalledNameAsync(string path, TextBlock name, CancellationToken cancellationToken)
    {
        var entered = false;
        try
        {
            await _lookupGate.WaitAsync(cancellationToken);
            entered = true;
            byte[] hash;
            await using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                hash = await System.Security.Cryptography.SHA1.HashDataAsync(stream, cancellationToken);
            var project = await _modrinth.LookupProjectByHashAsync(Convert.ToHexString(hash).ToLowerInvariant(), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (project != null) name.Text = project.Title;
        }
        catch (Exception) { }
        finally { if (entered) _lookupGate.Release(); }
    }

    private async void ModSearch_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        _debounce?.Cancel();
        await SearchAsync();
    }

    private async void ModSearch_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput || !_active) return;
        _debounce?.Cancel();
        _searchCancellation?.Cancel();
        using var debounce = new CancellationTokenSource();
        _debounce = debounce;
        try { await Task.Delay(300, debounce.Token); await SearchAsync(); }
        catch (OperationCanceledException) { }
        finally { if (ReferenceEquals(_debounce, debounce)) _debounce = null; }
    }

    private async void Filter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_active && !_updating) await SearchAsync();
    }
    private async void Compatibility_Changed(object sender, RoutedEventArgs e)
    {
        if (_active && !_updating) await SearchAsync();
    }

    private async Task SearchAsync(bool append = false)
    {
        if (!_active || _installed) return;
        if (append && _searchCancellation != null) return;
        _searchCancellation?.Cancel();
        using var cancellation = new CancellationTokenSource();
        _searchCancellation = cancellation;
        _searchComplete = false;
        var type = _selectedType;
        var mc = SelectedMinecraftVersion;
        var compatible = mc != null;
        var loader = compatible && type == "mod" && _instance!.Loader != LoaderType.None ? _instance.Loader.ToString().ToLowerInvariant() : null;
        var offset = append ? _searchOffset : 0;
        if (!append) { SearchResultsPanel.Children.Clear(); ResultsScroll.ChangeView(null, 0, null); }
        var progress = new ProgressRing { IsActive = true, Width = 24, Height = 24, Margin = new Thickness(0, 18, 0, 18) };
        SearchResultsPanel.Children.Add(progress);
        ResultsInfo.Text = App.L("mods.searching");
        try
        {
            var result = await _modrinth.SearchAsync(ModSearchBox.Text?.Trim() ?? "", mc, loader, type,
                category: (CategoryFilter.SelectedItem as ComboBoxItem)?.Tag as string,
                offset: offset, limit: 20, sortBy: (SortFilter.SelectedItem as ComboBoxItem)?.Tag as string ?? "relevance", cancellationToken: cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(_searchCancellation, cancellation) || !_active) return;
            foreach (var project in result.Hits) AddProjectCard(project, type, mc);
            _searchOffset = offset + result.Hits.Count;
            _searchComplete = true;
            ResultsInfo.Text = result.TotalHits == 0 ? App.L("catalog.no_results") : App.L("mods.results", result.TotalHits);
            ResultsInfo.Text += " · " + (compatible ? "Minecraft " + mc + (loader != null ? " / " + loader : "") : App.L("catalog.all_versions"));
            if (type == "modpack") ResultsInfo.Text += " · " + App.L("catalog.pack_target");
            if (result.Hits.Count > 0 && _searchOffset < result.TotalHits)
            {
                var more = new Button { Content = App.L("mods.load_more", result.TotalHits - _searchOffset), HorizontalAlignment = HorizontalAlignment.Stretch };
                more.Click += async (_, _) => { SearchResultsPanel.Children.Remove(more); await SearchAsync(true); };
                SearchResultsPanel.Children.Add(more);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception)
        {
            if (!ReferenceEquals(_searchCancellation, cancellation) || !_active) return;
            ResultsInfo.Text = App.L("catalog.search_error");
            var retry = new Button { Content = App.L("catalog.retry"), HorizontalAlignment = HorizontalAlignment.Left };
            retry.Click += async (_, _) => { SearchResultsPanel.Children.Remove(retry); await SearchAsync(append); };
            SearchResultsPanel.Children.Add(retry);
        }
        finally
        {
            SearchResultsPanel.Children.Remove(progress);
            if (ReferenceEquals(_searchCancellation, cancellation)) _searchCancellation = null;
        }
    }

    private void AddProjectCard(ModrinthProject project, string type, string? mcVersion)
    {
        var row = new Grid { ColumnSpacing = 16 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var icon = new Grid { Width = 64, Height = 64, Background = Brush("CardHoverBrush"), CornerRadius = new CornerRadius(10), VerticalAlignment = VerticalAlignment.Top };
        icon.Children.Add(new FontIcon { Glyph = "\uE8A5", FontSize = 24, Foreground = Brush("SubtleBrush") });
        if (Uri.TryCreate(project.IconUrl, UriKind.Absolute, out var uri) && uri.Scheme == "https")
            icon.Children.Add(new Image { Source = new BitmapImage(uri) { DecodePixelWidth = 128 }, Stretch = Stretch.Uniform });
        row.Children.Add(icon);
        var info = new StackPanel { Spacing = 5 };
        info.Children.Add(new TextBlock { Text = project.Title, FontSize = 17, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = Brush("PrimaryBrush"), TextTrimming = TextTrimming.CharacterEllipsis });
        info.Children.Add(new TextBlock { Text = project.Description, FontSize = 13, Foreground = Brush("SubtleBrush"),
            TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis });
        info.Children.Add(new TextBlock { Text = $"{project.Author}  ·  {project.DownloadsFormatted} {App.L("mods.downloads")}",
            FontSize = 12, Foreground = Brush("DimBrush"), TextTrimming = TextTrimming.CharacterEllipsis });
        Grid.SetColumn(info, 1);
        row.Children.Add(info);
        var arrow = new FontIcon { Glyph = "\uE76C", FontSize = 13, Foreground = Brush("SubtleBrush") };
        Grid.SetColumn(arrow, 2);
        row.Children.Add(arrow);
        var button = new Button { Content = row, HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(16), CornerRadius = new CornerRadius(12),
            Background = Brush("CardBrush"), BorderBrush = Brush("CardBorderBrush"), BorderThickness = new Thickness(1) };
        AutomationProperties.SetName(button, project.Title + ". " + App.L("catalog.details"));
        button.Click += (_, _) => Frame.Navigate(typeof(ModProjectPage), new ModProjectRequest(project, type, _instance?.Id, mcVersion));
        SearchResultsPanel.Children.Add(button);
        AnimationHelper.SlideIn(button);
        AnimationHelper.AddButtonFeedback(button);
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_contentDir == null) return;
        try { Directory.CreateDirectory(_contentDir); Process.Start(new ProcessStartInfo(_contentDir) { UseShellExecute = true }); }
        catch (Exception ex) { InstalledInfo.Text = ex.Message; }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        RefreshWorlds();
        if (_installed) LoadInstalled(); else await SearchAsync();
    }
}
