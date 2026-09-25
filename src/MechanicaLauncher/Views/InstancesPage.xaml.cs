using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using MechanicaLauncher.Core.Game;
using MechanicaLauncher.Core.Instances;
using MechanicaLauncher.Helpers;

namespace MechanicaLauncher.Views;

public sealed partial class InstancesPage : Page
{
    private static Core.Profiles.LauncherSettings S => App.Settings;
    private readonly InstanceManager _im = new();
    private string _searchQuery = "";
    private string _sortMode = "recent";
    private readonly HashSet<LoaderType> _loaderFilter = new();
    private bool _isLoaded;

    public InstancesPage()
    {
        this.InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        PageTitle.Text = App.L("inst.title");
        NewInstanceText.Text = App.L("inst.new");
        ImportMrpackText.Text = App.L("inst.import");
        CompactCards.Content = App.L("feature.compact");
        CompactCards.IsChecked = S.CompactInstances;
        SearchBox.PlaceholderText = App.L("inst.search");
        foreach (var item in SortBox.Items.OfType<ComboBoxItem>())
            item.Content = App.L("inst.sort_" + item.Tag);
        InstanceManager.InstancesChanged += OnInstancesChanged;
        _isLoaded = true;
        LoadInstances();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        InstanceManager.InstancesChanged -= OnInstancesChanged;
    }

    private void OnInstancesChanged() =>
        DispatcherQueue.TryEnqueue(LoadInstances);

    private void Compact_Changed(object sender, RoutedEventArgs e)
    {
        if (!_isLoaded) return;
        S.CompactInstances = CompactCards.IsChecked == true;
        S.Save();
        LoadInstances();
    }

    private void LoadInstances()
    {
        InstancesList.Children.Clear();
        var allInstances = _im.GetAllInstances();
        RebuildFilterChips(allInstances);
        var instances = ApplyFilters(allInstances);

        if (instances.Count == 0)
        {
            InstancesList.Children.Add(new TextBlock
            {
                Text = allInstances.Count == 0 ? App.L("inst.no_instances") : App.L("inst.no_matches"),
                Foreground = (Brush)Application.Current.Resources["DimBrush"],
                HorizontalAlignment = HorizontalAlignment.Center,
                FontSize = 14,
                Margin = new Thickness(0, 40, 0, 0)
            });
            return;
        }

        int delay = 0;
        foreach (var inst in instances)
        {
            var card = CreateCard(inst);
            InstancesList.Children.Add(card);
            AnimationHelper.SlideIn(card, delay);
            AnimationHelper.AddCardHover(card);
            delay += 50;
        }
    }

    private void SearchBox_Changed(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (!_isLoaded) return;
        _searchQuery = sender.Text?.Trim() ?? "";
        LoadInstances();
    }

    private void Sort_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_isLoaded) return;
        _sortMode = (SortBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "recent";
        LoadInstances();
    }

    private List<GameInstance> ApplyFilters(List<GameInstance> all)
    {
        IEnumerable<GameInstance> q = all;
        if (!string.IsNullOrEmpty(_searchQuery))
            q = q.Where(i => i.Name.Contains(_searchQuery, StringComparison.OrdinalIgnoreCase)
                          || i.McVersion.Contains(_searchQuery, StringComparison.OrdinalIgnoreCase));
        if (_loaderFilter.Count > 0)
            q = q.Where(i => _loaderFilter.Contains(i.Loader));

        q = _sortMode switch
        {
            "name"   => q.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase),
            "loader" => q.OrderBy(i => i.Loader).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase),
            "mcver"  => q.OrderByDescending(i => i.McVersion, StringComparer.OrdinalIgnoreCase),
            _        => q.OrderByDescending(i => i.LastPlayed ?? i.CreatedAt),
        };
        return q.ToList();
    }

    private void RebuildFilterChips(List<GameInstance> all)
    {
        FilterChips.Children.Clear();
        var loaders = all.Select(i => i.Loader).Distinct().OrderBy(l => l).ToList();
        if (loaders.Count <= 1) return;
        foreach (var loader in loaders)
            FilterChips.Children.Add(BuildChip(loader));
    }

    private Button BuildChip(LoaderType loader)
    {
        var active = _loaderFilter.Contains(loader);
        var btn = new Button
        {
            Content = loader == LoaderType.None ? "Vanilla" : loader.ToString(),
            Style = (Style)Application.Current.Resources["FilterChip"],
            Background = (Brush)Application.Current.Resources[active ? "SelectionBrush" : "CardBrush"],
            Foreground = (Brush)Application.Current.Resources[active ? "SelectionTextBrush" : "PrimaryBrush"],
        };
        btn.Click += (_, _) =>
        {
            if (!_loaderFilter.Add(loader)) _loaderFilter.Remove(loader);
            LoadInstances();
        };
        return btn;
    }

    private async void ImportMrpack_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        picker.FileTypeFilter.Add(".mrpack");
        picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.Downloads;

        var file = await picker.PickSingleFileAsync();
        if (file == null) return;

        if (App.MainWindow is MainWindow mw)
            await mw.ImportMrpackAsync(file.Path);
    }

    private Border CreateCard(GameInstance inst)
    {
        var card = new Border
        {
            Background = (Brush)Application.Current.Resources["CardBrush"],
            BorderBrush = (Brush)Application.Current.Resources["CardBorderBrush"], BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(S.CompactInstances ? 12 : 20),
            MinHeight = 72,
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

        var iconColor = inst.Loader switch
        {
            LoaderType.Fabric => Windows.UI.Color.FromArgb(0xFF, 0x4C, 0xAF, 0x50),
            LoaderType.Quilt => Windows.UI.Color.FromArgb(0xFF, 0xAB, 0x47, 0xBC),
            LoaderType.Forge => Windows.UI.Color.FromArgb(0xFF, 0xFF, 0x98, 0x00),
            LoaderType.NeoForge => Windows.UI.Color.FromArgb(0xFF, 0xE6, 0x5C, 0x00),
            _ => Windows.UI.Color.FromArgb(0xFF, 0x78, 0x90, 0x9C),
        };
        var iconBorder = new Border
        {
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x1A, iconColor.R, iconColor.G, iconColor.B)),
            CornerRadius = new CornerRadius(10),
            Width = 48, Height = 48,
            VerticalAlignment = VerticalAlignment.Center
        };
        var customIconPath = _im.GetIconAbsolutePath(inst);
        if (customIconPath != null)
        {
            iconBorder.Child = new Image
            {
                Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(customIconPath)),
                Stretch = Microsoft.UI.Xaml.Media.Stretch.UniformToFill,
                Width = 48, Height = 48,
            };
            iconBorder.Background = (Brush)Application.Current.Resources["CardHoverBrush"];
        }
        else
        {
            iconBorder.Child = new FontIcon
            {
                Glyph = "\uE74C", FontSize = 20,
                Foreground = new SolidColorBrush(iconColor),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
        }

        var info = new StackPanel { Margin = new Thickness(16, 0, 16, 0), VerticalAlignment = VerticalAlignment.Center, Spacing = 4 };
        var titleRow = new Grid
        {
            ColumnSpacing = 8,
            ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto }, new ColumnDefinition { Width = GridLength.Auto } }
        };
        var name = new TextBlock { Text = inst.Name, FontSize = 17, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis };
        ToolTipService.SetToolTip(name, inst.Name);
        titleRow.Children.Add(name);

        if (inst.Loader != LoaderType.None)
        {
            var badge = new Border
            {
                Background = (Brush)Application.Current.Resources["SelectionBrush"],
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8, 3, 8, 3),
                VerticalAlignment = VerticalAlignment.Center
            };
            badge.Child = new TextBlock
            {
                Text = inst.Loader.ToString(),
                FontSize = 11, Foreground = (Brush)Application.Current.Resources["SelectionTextBrush"],
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
            };
            Grid.SetColumn(badge, 1);
            titleRow.Children.Add(badge);
        }

        info.Children.Add(titleRow);

        var isRunning = App.RunningInstances.TryGetValue(inst.Id, out var proc) && !proc.HasExited;

        if (isRunning)
        {
            var runBadge = new Border
            {
                Background = (Brush)Application.Current.Resources["SelectionBrush"],
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8, 3, 8, 3),
                VerticalAlignment = VerticalAlignment.Center
            };
            runBadge.Child = new TextBlock { Text = App.L("home.running"), FontSize = 11, Foreground = (Brush)Application.Current.Resources["SelectionTextBrush"], FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
            Grid.SetColumn(runBadge, 2);
            titleRow.Children.Add(runBadge);
        }

        var sub = $"{inst.McVersion}";
        if (inst.LastPlayed.HasValue)
            sub += $"  ·  Last played {inst.LastPlayed.Value:MMM dd}";
        info.Children.Add(new TextBlock { Text = sub, Foreground = (Brush)Application.Current.Resources["SubtleBrush"], FontSize = 12 });

        Grid.SetColumn(info, 1);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };

        var selectBtn = new Button
        {
            Content = S.SelectedInstanceId == inst.Id ? App.L("inst.selected") : App.L("inst.select"),
            FontSize = 13, Padding = new Thickness(16, 6, 16, 6),
            MinWidth = 72, MinHeight = 32, CornerRadius = new CornerRadius(6),
            Tag = inst.Id
        };
        if (S.SelectedInstanceId == inst.Id)
        {
            selectBtn.Background = (Brush)Application.Current.Resources["SelectionBrush"];
            selectBtn.Foreground = (Brush)Application.Current.Resources["SelectionTextBrush"];
        }
        selectBtn.Click += SelectInstance_Click;

        var deleteBtn = new Button
        {
            Content = new FontIcon { Glyph = "\uE74D", FontSize = 14 },
            FontSize = 13, Padding = new Thickness(8, 6, 8, 6),
            MinHeight = 32, CornerRadius = new CornerRadius(6),
            Tag = inst.Id
        };
        deleteBtn.Click += DeleteInstance_Click;

        var editBtn = new Button
        {
            Content = new FontIcon { Glyph = "\uE713", FontSize = 14 },
            FontSize = 13, Padding = new Thickness(8, 6, 8, 6),
            MinHeight = 32, CornerRadius = new CornerRadius(6),
            Tag = inst.Id
        };
        editBtn.Click += EditInstance_Click;
        ToolTipService.SetToolTip(editBtn, App.L("feature.details"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(editBtn, App.L("feature.details"));

        var moreBtn = new Button
        {
            Content = new FontIcon { Glyph = "\uE712", FontSize = 14 },
            FontSize = 13, Padding = new Thickness(8, 6, 8, 6),
            MinHeight = 32, CornerRadius = new CornerRadius(6),
            Tag = inst.Id,
        };
        ToolTipService.SetToolTip(moreBtn, "More actions");
        var moreFlyout = BuildInstanceContextFlyout(inst);
        moreBtn.Flyout = moreFlyout;

        buttons.Children.Add(selectBtn);
        buttons.Children.Add(editBtn);
        buttons.Children.Add(moreBtn);
        buttons.Children.Add(deleteBtn);
        Grid.SetColumn(buttons, 2);

        grid.Children.Add(iconBorder);
        grid.Children.Add(info);
        grid.Children.Add(buttons);
        var body = new StackPanel { Spacing = 14 };
        var cover = _im.GetCoverAbsolutePath(inst);
        if (cover != null && !S.CompactInstances)
            body.Children.Add(new Border { CornerRadius = new CornerRadius(6), Height = 120, Child = new Image
            {
                Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage { DecodePixelWidth = 1200, UriSource = new Uri(cover) },
                Stretch = Stretch.UniformToFill
            } });
        body.Children.Add(grid);
        card.Child = body;
        if (InstanceMedia.IsAccent(inst.AccentColor)) card.BorderBrush = InstanceDetailsPage.AccentBrush(inst.AccentColor);
        // Right-click on the card pops the same menu as the ⋯ button — standard desktop idiom.
        card.ContextFlyout = BuildInstanceContextFlyout(inst);
        return card;
    }

    private void SelectInstance_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string id)
        {
            S.SelectedInstanceId = id;
            S.Save();
            LoadInstances();
        }
    }

    private MenuFlyout BuildInstanceContextFlyout(GameInstance inst)
    {
        var flyout = new MenuFlyout { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.Bottom };

        foreach (var (tab, glyph) in new[] { ("settings", "\uE713"), ("compatibility", "\uE73E"), ("screenshots", "\uEB9F"), ("crashes", "\uE7BA"), ("appearance", "\uE790") })
        {
            var item = new MenuFlyoutItem { Text = App.L("feature." + tab), Icon = new FontIcon { Glyph = glyph } };
            item.Click += (_, _) => Frame.Navigate(typeof(InstanceDetailsPage), new InstanceDetailsRequest(inst.Id, tab));
            flyout.Items.Add(item);
        }
        flyout.Items.Add(new MenuFlyoutSeparator());

        var play = new MenuFlyoutItem { Text = "Play", Icon = new FontIcon { Glyph = "\uE768" } };
        play.Click += (_, _) =>
        {
            S.SelectedInstanceId = inst.Id;
            S.Save();
            if (App.MainWindow is MainWindow mw)
                mw.DispatcherQueue.TryEnqueue(() => mw.NavigateToTag("Home"));
        };

        var openDir = new MenuFlyoutItem { Text = "Open .minecraft folder", Icon = new FontIcon { Glyph = "\uE838" } };
        openDir.Click += (_, _) =>
        {
            var dir = _im.GetGameDir(inst.Id);
            if (Directory.Exists(dir))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = dir, UseShellExecute = true });
        };

        var openMods = new MenuFlyoutItem { Text = "Open mods folder", Icon = new FontIcon { Glyph = "\uEA86" } };
        openMods.Click += (_, _) =>
        {
            var dir = Path.Combine(_im.GetGameDir(inst.Id), "mods");
            Directory.CreateDirectory(dir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = dir, UseShellExecute = true });
        };

        var dup = new MenuFlyoutItem { Text = "Duplicate", Icon = new FontIcon { Glyph = "\uE8C8" } };
        dup.Click += (_, _) =>
        {
            try { _im.DuplicateInstance(inst.Id); }
            catch (Exception ex)
            {
                _ = new ContentDialog
                {
                    Title = "Duplicate failed",
                    Content = new TextBlock { Text = ex.Message, TextWrapping = TextWrapping.Wrap, MaxWidth = 500 },
                    CloseButtonText = "OK",
                    XamlRoot = this.XamlRoot,
                }.ShowAsync();
            }
        };

        var export = new MenuFlyoutItem { Text = "Export as .mrpack...", Icon = new FontIcon { Glyph = "\uEDE1" } };
        export.Click += (_, _) => _ = DoExportAsync(inst);

        flyout.Items.Add(play);
        flyout.Items.Add(new MenuFlyoutSeparator());
        flyout.Items.Add(openDir);
        flyout.Items.Add(openMods);
        flyout.Items.Add(new MenuFlyoutSeparator());
        flyout.Items.Add(dup);
        flyout.Items.Add(export);
        return flyout;
    }

    private async void ExportInstance_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string id) return;
        var inst = _im.GetInstance(id);
        if (inst == null) return;
        await DoExportAsync(inst);
    }

    private async Task DoExportAsync(GameInstance inst)
    {
        var picker = new Windows.Storage.Pickers.FileSavePicker();
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        picker.FileTypeChoices.Add("Modrinth modpack", [".mrpack"]);
        picker.SuggestedFileName = $"{inst.Name}-{DateTime.UtcNow:yyyyMMdd}";

        var file = await picker.PickSaveFileAsync();
        if (file == null) return;

        var dialog = new ContentDialog
        {
            Title = "Exporting modpack",
            Content = new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    new TextBlock { Text = $"Packing {inst.Name}..." },
                    new ProgressBar { IsIndeterminate = true }
                }
            },
            XamlRoot = this.XamlRoot,
        };
        _ = dialog.ShowAsync();

        try
        {
            await Task.Run(() => Core.Mods.ModpackInstaller.ExportAsync(inst, _im, file.Path));
            dialog.Hide();
            await new ContentDialog
            {
                Title = "Exported",
                Content = new TextBlock { Text = $"Saved to:\n{file.Path}", TextWrapping = TextWrapping.Wrap, MaxWidth = 500 },
                CloseButtonText = "OK",
                XamlRoot = this.XamlRoot,
            }.ShowAsync();
        }
        catch (Exception ex)
        {
            dialog.Hide();
            await new ContentDialog
            {
                Title = "Export failed",
                Content = new TextBlock { Text = ex.Message, TextWrapping = TextWrapping.Wrap, MaxWidth = 500 },
                CloseButtonText = "OK",
                XamlRoot = this.XamlRoot,
            }.ShowAsync();
        }
    }

    private async void DeleteInstance_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string id)
        {
            if (App.IsInstanceBusy(id))
            {
                await new ContentDialog
                {
                    Title = App.L("gen.error"),
                    Content = App.L("inst.busy"),
                    CloseButtonText = "OK",
                    XamlRoot = this.XamlRoot
                }.ShowAsync();
                return;
            }

            var dialog = new ContentDialog
            {
                Title = App.L("inst.delete"),
                Content = App.L("inst.delete_confirm"),
                PrimaryButtonText = App.L("gen.delete"),
                CloseButtonText = App.L("inst.cancel"),
                XamlRoot = this.XamlRoot
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                if (App.IsInstanceBusy(id)) return;
                try
                {
                    _im.DeleteInstance(id);
                }
                catch
                {
                    await new ContentDialog
                    {
                        Title = App.L("gen.error"),
                        Content = App.L("gen.file_error"),
                        CloseButtonText = "OK",
                        XamlRoot = this.XamlRoot
                    }.ShowAsync();
                    return;
                }
                if (S.SelectedInstanceId == id)
                {
                    S.SelectedInstanceId = null;
                    S.Save();
                }
                LoadInstances();
            }
        }
    }

    private void EditInstance_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string id })
            Frame.Navigate(typeof(InstanceDetailsPage), new InstanceDetailsRequest(id));
    }

    private async void NewInstance_Click(object sender, RoutedEventArgs e)
    {
        var nameBox = new TextBox { PlaceholderText = App.L("inst.name"), MinHeight = 36 };
        var versionBox = new ComboBox { PlaceholderText = App.L("home.loading"), MinWidth = 280, MinHeight = 36, IsEnabled = false };
        var loaderBox = new ComboBox { MinWidth = 280, MinHeight = 36 };
        var errorText = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = 360, Visibility = Visibility.Collapsed };
        var retryButton = new Button { Content = App.L("gen.retry"), Visibility = Visibility.Collapsed };
        loaderBox.Items.Add(new ComboBoxItem { Content = App.L("inst.none"), Tag = "None" });
        loaderBox.Items.Add(new ComboBoxItem { Content = "Fabric", Tag = "Fabric" });
        loaderBox.Items.Add(new ComboBoxItem { Content = "Quilt", Tag = "Quilt" });
        loaderBox.Items.Add(new ComboBoxItem { Content = "Forge", Tag = "Forge" });
        loaderBox.Items.Add(new ComboBoxItem { Content = "NeoForge", Tag = "NeoForge" });
        loaderBox.SelectedIndex = 0;

        var content = new StackPanel
        {
            Spacing = 12, MinWidth = 320,
            Children = {
                new TextBlock { Text = App.L("inst.name") }, nameBox,
                new TextBlock { Text = App.L("inst.mc_version") }, versionBox,
                new TextBlock { Text = App.L("inst.loader") }, loaderBox,
                errorText, retryButton
            }
        };

        var dialog = new ContentDialog
        {
            Title = App.L("inst.new"),
            RequestedTheme = ActualTheme,
            Content = content,
            PrimaryButtonText = App.L("inst.create"),
            IsPrimaryButtonEnabled = false,
            CloseButtonText = App.L("inst.cancel"),
            XamlRoot = this.XamlRoot
        };

        using var cancellation = new CancellationTokenSource();
        var cancellationToken = cancellation.Token;
        void UpdateCanCreate() => dialog.IsPrimaryButtonEnabled =
            !string.IsNullOrWhiteSpace(nameBox.Text) && versionBox.IsEnabled && versionBox.SelectedItem != null;
        nameBox.TextChanged += (_, _) => UpdateCanCreate();
        versionBox.SelectionChanged += (_, _) => UpdateCanCreate();
        dialog.PrimaryButtonClick += (_, args) =>
        {
            UpdateCanCreate();
            args.Cancel = !dialog.IsPrimaryButtonEnabled;
        };
        dialog.Closed += (_, _) => cancellation.Cancel();

        async Task LoadVersionsAsync()
        {
            errorText.Visibility = Visibility.Collapsed;
            retryButton.Visibility = Visibility.Collapsed;
            versionBox.IsEnabled = false;
            versionBox.PlaceholderText = App.L("home.loading");
            UpdateCanCreate();
            try
            {
                var vm = new VersionManager(_im.SharedDir);
                var manifest = await vm.GetManifestAsync(cancellationToken);
                var versions = manifest.Versions
                    .Where(v => v.Type == "release" || (App.Settings.ShowSnapshots && v.Type == "snapshot"))
                    .ToList();
                cancellationToken.ThrowIfCancellationRequested();
                if (versions.Count == 0) throw new InvalidDataException(App.L("inst.no_versions"));
                versionBox.Items.Clear();
                foreach (var v in versions)
                    versionBox.Items.Add(new ComboBoxItem { Content = v.Id, Tag = v.Id });
                versionBox.SelectedIndex = 0;
                versionBox.PlaceholderText = App.L("inst.select_version");
                versionBox.IsEnabled = true;
                UpdateCanCreate();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            catch (Exception ex)
            {
                versionBox.PlaceholderText = App.L("inst.select_version");
                errorText.Text = App.L("inst.versions_failed") + "\n" + ex.Message;
                errorText.Visibility = Visibility.Visible;
                retryButton.Visibility = Visibility.Visible;
            }
        }
        dialog.Opened += async (_, _) => await LoadVersionsAsync();
        retryButton.Click += async (_, _) => await LoadVersionsAsync();

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var name = nameBox.Text?.Trim();
        var mcVersion = (versionBox.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        var loaderStr = (loaderBox.SelectedItem as ComboBoxItem)?.Tag?.ToString();

        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(mcVersion)) return;

        var loader = loaderStr switch
        {
            "Fabric" => LoaderType.Fabric,
            "Quilt" => LoaderType.Quilt,
            "Forge" => LoaderType.Forge,
            "NeoForge" => LoaderType.NeoForge,
            _ => LoaderType.None
        };
        string? loaderVersion = null;

        Exception? fetchError = null;
        try
        {
            if (loader == LoaderType.Fabric)
            {
                var versions = await new FabricInstaller(_im.SharedDir, "").GetLoaderVersionsAsync(mcVersion);
                loaderVersion = versions.FirstOrDefault(v => v.Stable)?.Version ?? versions.FirstOrDefault()?.Version;
            }
            else if (loader == LoaderType.Quilt)
                loaderVersion = (await new QuiltInstaller(_im.SharedDir, "").GetLoaderVersionsAsync(mcVersion)).FirstOrDefault();
            else if (loader == LoaderType.Forge)
                loaderVersion = (await new ForgeInstaller(_im.SharedDir, "").GetVersionsAsync(mcVersion)).FirstOrDefault();
            else if (loader == LoaderType.NeoForge)
                loaderVersion = (await new NeoForgeInstaller(_im.SharedDir, "").GetVersionsAsync(mcVersion)).FirstOrDefault();
        }
        catch (Exception ex) { fetchError = ex; }

        if (loader != LoaderType.None && string.IsNullOrEmpty(loaderVersion))
        {
            await new ContentDialog
            {
                Title = $"{loader} not available for {mcVersion}",
                Content = new TextBlock
                {
                    Text = fetchError != null
                        ? $"Failed to fetch loader versions:\n{fetchError.Message}"
                        : $"No {loader} releases found for Minecraft {mcVersion}. Pick another version or loader.",
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 400
                },
                CloseButtonText = "OK",
                XamlRoot = this.XamlRoot
            }.ShowAsync();
            return;
        }

        // Create only — the loader installer (downloads mappings, runs NeoForge processors, etc.) runs
        // on first Play so creation stays instant.
        var instance = _im.CreateInstance(name, mcVersion, loader, loaderVersion);
        S.SelectedInstanceId = instance.Id;
        S.Save();
        LoadInstances();
    }
}
