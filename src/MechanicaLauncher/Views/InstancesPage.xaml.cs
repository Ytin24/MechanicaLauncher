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
                Text = allInstances.Count == 0 ? App.L("inst.no_instances") : "No instances match the current filters.",
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)),
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
            MinHeight = 28,
            Padding = new Thickness(12, 4, 12, 4),
            CornerRadius = new CornerRadius(14),
            FontSize = 12,
            Background = new SolidColorBrush(active
                ? Windows.UI.Color.FromArgb(0xFF, 0x4C, 0xAF, 0x50)
                : Windows.UI.Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF)),
            Foreground = new SolidColorBrush(active ? Microsoft.UI.Colors.White : Windows.UI.Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)),
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
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF)),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(20),
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
            iconBorder.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
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

        var info = new StackPanel { Margin = new Thickness(16, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Spacing = 4 };
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        titleRow.Children.Add(new TextBlock { Text = inst.Name, FontSize = 17, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });

        if (inst.Loader != LoaderType.None)
        {
            var badge = new Border
            {
                Background = new SolidColorBrush(iconColor),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8, 3, 8, 3),
                VerticalAlignment = VerticalAlignment.Center
            };
            badge.Child = new TextBlock
            {
                Text = inst.Loader.ToString(),
                FontSize = 10, Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)),
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
            };
            titleRow.Children.Add(badge);
        }

        info.Children.Add(titleRow);

        var isRunning = App.RunningInstances.TryGetValue(inst.Id, out var proc) && !proc.HasExited;

        if (isRunning)
        {
            var runBadge = new Border
            {
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x21, 0x96, 0xF3)),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8, 3, 8, 3),
                VerticalAlignment = VerticalAlignment.Center
            };
            runBadge.Child = new TextBlock { Text = "Running", FontSize = 10, Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
            titleRow.Children.Add(runBadge);
        }

        var sub = $"{inst.McVersion}";
        if (inst.LastPlayed.HasValue)
            sub += $"  ·  Last played {inst.LastPlayed.Value:MMM dd}";
        info.Children.Add(new TextBlock { Text = sub, Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)), FontSize = 12 });

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
            selectBtn.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x4C, 0xAF, 0x50));
            selectBtn.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF));
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
            Content = new FontIcon { Glyph = "\uE70F", FontSize = 14 },
            FontSize = 13, Padding = new Thickness(8, 6, 8, 6),
            MinHeight = 32, CornerRadius = new CornerRadius(6),
            Tag = inst.Id
        };
        editBtn.Click += EditInstance_Click;

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
        card.Child = grid;
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
            if (App.RunningInstances.TryGetValue(id, out var proc) && !proc.HasExited)
            {
                await new ContentDialog
                {
                    Title = App.L("gen.error"),
                    Content = App.L("inst.running_cannot_delete"),
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

    private async void EditInstance_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string id) return;
        var inst = _im.GetInstance(id);
        if (inst == null) return;

        var nameBox = new TextBox { Text = inst.Name, MinHeight = 36 };
        var minMemSlider = new Slider { Minimum = 512, Maximum = 16384, Value = inst.MinMemoryMb, StepFrequency = 512, SnapsTo = Microsoft.UI.Xaml.Controls.Primitives.SliderSnapsTo.StepValues };
        var maxMemSlider = new Slider { Minimum = 512, Maximum = 16384, Value = inst.MaxMemoryMb, StepFrequency = 512, SnapsTo = Microsoft.UI.Xaml.Controls.Primitives.SliderSnapsTo.StepValues };
        var minMemLabel = new TextBlock { Text = $"Min Memory: {inst.MinMemoryMb} MB", FontSize = 13 };
        var maxMemLabel = new TextBlock { Text = $"Max Memory: {inst.MaxMemoryMb} MB", FontSize = 13 };
        var jvmBox = new TextBox { Text = inst.JvmArgs, PlaceholderText = "-XX:+UseG1GC", MinHeight = 36 };
        var widthBox = new TextBox { Text = inst.WindowWidth.ToString(), MinHeight = 36, MinWidth = 80 };
        var heightBox = new TextBox { Text = inst.WindowHeight.ToString(), MinHeight = 36, MinWidth = 80 };

        minMemSlider.ValueChanged += (_, args) => minMemLabel.Text = $"Min Memory: {(int)args.NewValue} MB";
        maxMemSlider.ValueChanged += (_, args) => maxMemLabel.Text = $"Max Memory: {(int)args.NewValue} MB";

        var iconPreview = new Border
        {
            Width = 56, Height = 56, CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)),
        };
        void RefreshIconPreview()
        {
            var p = _im.GetIconAbsolutePath(inst);
            iconPreview.Child = p != null
                ? (UIElement)new Image
                {
                    Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(p)) { CreateOptions = Microsoft.UI.Xaml.Media.Imaging.BitmapCreateOptions.IgnoreImageCache },
                    Stretch = Microsoft.UI.Xaml.Media.Stretch.UniformToFill,
                }
                : new FontIcon { Glyph = "\uE74C", FontSize = 24, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        }
        RefreshIconPreview();

        var pickIconBtn = new Button { Content = "Change icon...", MinHeight = 34 };
        var clearIconBtn = new Button { Content = "Remove", MinHeight = 34 };
        pickIconBtn.Click += async (_, _) =>
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            foreach (var e2 in new[] { ".png", ".jpg", ".jpeg", ".webp" }) picker.FileTypeFilter.Add(e2);
            var f = await picker.PickSingleFileAsync();
            if (f == null) return;
            try { _im.SetIconFromFile(inst, f.Path); RefreshIconPreview(); }
            catch { }
        };
        clearIconBtn.Click += (_, _) =>
        {
            inst.IconPath = null;
            _im.SaveInstance(inst);
            RefreshIconPreview();
        };

        var iconRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
        iconRow.Children.Add(iconPreview);
        iconRow.Children.Add(new StackPanel
        {
            Spacing = 6, VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock { Text = "Icon", FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { pickIconBtn, clearIconBtn } }
            }
        });

        var content = new StackPanel
        {
            Spacing = 10, MinWidth = 360,
            Children =
            {
                new TextBlock { Text = App.L("inst.name") }, nameBox,
                new TextBlock { Text = $"Version: {inst.McVersion}  ·  {inst.Loader}{(inst.LoaderVersion != null ? $" {inst.LoaderVersion}" : "")}", Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)), FontSize = 13 },
                iconRow,
                minMemLabel, minMemSlider,
                maxMemLabel, maxMemSlider,
                new TextBlock { Text = "JVM Arguments" }, jvmBox,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children =
                {
                    new StackPanel { Spacing = 4, Children = { new TextBlock { Text = "Width", FontSize = 12 }, widthBox } },
                    new TextBlock { Text = "×", VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 10) },
                    new StackPanel { Spacing = 4, Children = { new TextBlock { Text = "Height", FontSize = 12 }, heightBox } }
                }}
            }
        };

        var dialog = new ContentDialog
        {
            Title = App.L("inst.edit"),
            Content = new ScrollViewer { Content = content, MaxHeight = 500 },
            PrimaryButtonText = App.L("inst.save"),
            CloseButtonText = App.L("inst.cancel"),
            XamlRoot = this.XamlRoot
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        inst.Name = nameBox.Text?.Trim() ?? inst.Name;
        inst.MinMemoryMb = (int)minMemSlider.Value;
        inst.MaxMemoryMb = (int)maxMemSlider.Value;
        inst.JvmArgs = jvmBox.Text ?? "";
        if (int.TryParse(widthBox.Text, out var w)) inst.WindowWidth = w;
        if (int.TryParse(heightBox.Text, out var h)) inst.WindowHeight = h;

        _im.SaveInstance(inst);
        LoadInstances();
    }

    private async void NewInstance_Click(object sender, RoutedEventArgs e)
    {
        var nameBox = new TextBox { PlaceholderText = App.L("inst.name"), MinHeight = 36 };
        var versionBox = new ComboBox { PlaceholderText = "Loading...", MinWidth = 280, MinHeight = 36, IsEnabled = false };
        var loaderBox = new ComboBox { MinWidth = 280, MinHeight = 36 };
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
                new TextBlock { Text = App.L("inst.loader") }, loaderBox
            }
        };

        var dialog = new ContentDialog
        {
            Title = App.L("inst.new"),
            Content = content,
            PrimaryButtonText = App.L("inst.create"),
            CloseButtonText = App.L("inst.cancel"),
            XamlRoot = this.XamlRoot
        };

        _ = Task.Run(async () =>
        {
            try
            {
                var vm = new VersionManager(_im.SharedDir);
                var manifest = await vm.GetManifestAsync();
                var versions = manifest.Versions
                    .Where(v => v.Type == "release" || (App.Settings.ShowSnapshots && v.Type == "snapshot"))
                    .ToList();

                DispatcherQueue.TryEnqueue(() =>
                {
                    versionBox.Items.Clear();
                    foreach (var v in versions)
                        versionBox.Items.Add(new ComboBoxItem { Content = v.Id, Tag = v.Id });
                    if (versionBox.Items.Count > 0) versionBox.SelectedIndex = 0;
                    versionBox.PlaceholderText = "Select version";
                    versionBox.IsEnabled = true;
                });
            }
            catch { }
        });

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
