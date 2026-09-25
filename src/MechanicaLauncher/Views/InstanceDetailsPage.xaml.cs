using System.Diagnostics;
using MechanicaLauncher.Core.Game;
using MechanicaLauncher.Core.Instances;
using MechanicaLauncher.Core.Mods;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace MechanicaLauncher.Views;

internal sealed record InstanceDetailsRequest(string InstanceId, string Tab = "settings");

public sealed partial class InstanceDetailsPage : Page
{
    public sealed record ScreenshotItem(GameScreenshot File, BitmapImage Thumbnail);
    private readonly InstanceManager _instances = new();
    private GameInstance? _instance;
    private readonly CancellationTokenSource _lifetime = new();
    private IReadOnlyList<GameScreenshot> _screenshots = [];
    private int _shown;
    private string? _cover;
    private string? _accent;
    private string? _icon;
    private StackPanel? _appearanceContent;
    private bool _loadingSettings;
    private bool _checking;
    private static Brush Brush(string key) => (Brush)Application.Current.Resources[key];
    private string GameDir => _instances.GetGameDir(_instance!.Id);

    public InstanceDetailsPage() { InitializeComponent(); }
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        var request = e.Parameter as InstanceDetailsRequest;
        _instance = request == null ? null : _instances.GetInstance(request.InstanceId);
        ToolTipService.SetToolTip(BackButton, App.L("feature.back"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(BackButton, App.L("feature.back"));
        FolderButton.Content = App.L("feature.open_folder");
        RefreshGalleryButton.Content = App.L("feature.refresh");
        MoreScreenshots.Content = App.L("gallery.more");
        if (_instance == null) { ShowError(App.L("feature.missing")); return; }
        TitleText.Text = _instance.Name;
        MetaText.Text = $"Minecraft {_instance.McVersion} · {(_instance.Loader == LoaderType.None ? "Vanilla" : _instance.Loader + " " + _instance.LoaderVersion)}";
        InitializeSettings();
        foreach (var tab in new[] { "settings", "compatibility", "screenshots", "crashes", "appearance" })
        {
            var button = Button(App.L(tab == "crashes" ? "feature.crashes_tab" : "feature." + tab), () => SelectTab(tab));
            button.Style = (Style)Application.Current.Resources["FilterChip"];
            button.Tag = tab;
            Tabs.Children.Add(button);
        }
        SelectTab(request!.Tab);
    }
    protected override void OnNavigatedFrom(NavigationEventArgs e) { _lifetime.Cancel(); base.OnNavigatedFrom(e); }
    private void Back_Click(object sender, RoutedEventArgs e) { if (Frame.CanGoBack) Frame.GoBack(); else Frame.Navigate(typeof(InstancesPage)); }
    private void Folder_Click(object sender, RoutedEventArgs e) { if (_instance != null) OpenPath(GameDir); }

    private void SelectTab(string tab)
    {
        if (_instance == null) return;
        foreach (var button in Tabs.Children.OfType<Button>())
        {
            var selected = (string)button.Tag == tab;
            button.Background = Brush(selected ? "SelectionBrush" : "CardBrush");
            button.Foreground = Brush(selected ? "SelectionTextBrush" : "PrimaryBrush");
        }
        Notice.IsOpen = false;
        TextContent.Children.Clear();
        TextScroll.ChangeView(null, 0, null);
        Gallery.Visibility = tab == "screenshots" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPanel.Visibility = tab == "settings" ? Visibility.Visible : Visibility.Collapsed;
        TextScroll.Visibility = tab is "screenshots" or "settings" ? Visibility.Collapsed : Visibility.Visible;
        switch (tab)
        {
            case "compatibility": BuildCompatibility(); break;
            case "screenshots": LoadScreenshots(); break;
            case "crashes": BuildCrashes(); break;
            case "appearance": BuildAppearance(); break;
        }
    }
    private void InitializeSettings()
    {
        MemoryTitle.Text = App.L("instance.memory");
        MemoryHint.Text = App.L("instance.memory_hint");
        MaxMemoryBox.Header = App.L("instance.memory_limit");
        MinMemoryBox.Header = App.L("instance.memory_initial");
        WindowTitle.Text = App.L("instance.window");
        WindowWidthBox.Header = App.L("instance.width");
        WindowHeightBox.Header = App.L("instance.height");
        ModsTitle.Text = App.L("instance.mods");
        ModsHint.Text = App.L("instance.mods_hint");
        InstanceNameBox.Header = App.L("inst.name");
        AdvancedSettings.Header = App.L("instance.advanced");
        AutomaticJavaToggle.Header = App.L("instance.java_auto");
        AutomaticJavaToggle.OnContent = App.L("instance.java_automatic");
        AutomaticJavaToggle.OffContent = App.L("instance.java_manual");
        JavaHint.Text = App.L("instance.java_hint");
        JavaPathBox.Header = App.L("instance.java_path");
        PickJavaButton.Content = App.L("instance.browse");
        JvmArgsBox.Header = App.L("instance.jvm");
        SaveSettingsButton.Content = App.L("feature.save");
        SettingsSaveHint.Text = App.L("instance.save_hint");
        LoadSettings();
    }
    private void LoadSettings()
    {
        _loadingSettings = true;
        try
        {
            InstanceNameBox.Text = _instance!.Name;
            MaxMemoryBox.Value = _instance.MaxMemoryMb;
            MinMemoryBox.Text = _instance.MinMemoryMb.ToString();
            WindowWidthBox.Value = _instance.WindowWidth;
            WindowHeightBox.Value = _instance.WindowHeight;
            JavaPathBox.Text = _instance.JavaPath ?? "";
            AutomaticJavaToggle.IsOn = string.IsNullOrWhiteSpace(_instance.JavaPath);
            JavaPathPanel.Visibility = AutomaticJavaToggle.IsOn ? Visibility.Collapsed : Visibility.Visible;
            JvmArgsBox.Text = _instance.JvmArgs;
        }
        finally { _loadingSettings = false; }
    }
    private void MaxMemory_Changed(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (!_loadingSettings && MinMemoryBox != null && args.NewValue > 0 && args.NewValue <= int.MaxValue &&
            args.NewValue == Math.Truncate(args.NewValue) && int.TryParse(MinMemoryBox.Text, out var minimum) && minimum > args.NewValue)
            MinMemoryBox.Text = ((int)args.NewValue).ToString();
    }
    private void AutomaticJava_Toggled(object sender, RoutedEventArgs e)
    {
        if (JavaPathPanel != null) JavaPathPanel.Visibility = AutomaticJavaToggle.IsOn ? Visibility.Collapsed : Visibility.Visible;
    }
    private async void PickJava_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add(".exe");
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow));
            var file = await picker.PickSingleFileAsync();
            if (file != null && !_lifetime.IsCancellationRequested) JavaPathBox.Text = file.Path;
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }
    private void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        if (_instance == null) return;
        if (App.IsInstanceBusy(_instance.Id)) { ShowError(App.L("feature.busy")); return; }
        SaveSettingsButton.Focus(FocusState.Programmatic);
        void Invalid(string key, Control control, bool advanced = false)
        {
            ShowError(App.L(key));
            if (advanced) AdvancedSettings.IsExpanded = true;
            control.Focus(FocusState.Programmatic);
            control.StartBringIntoView();
        }
        static bool ReadInteger(string text, out int result) => int.TryParse(text.Trim(), out result) && result > 0;
        var name = InstanceNameBox.Text.Trim();
        if (name.Length == 0) { Invalid("instance.name_required", InstanceNameBox); return; }
        if (!ReadInteger(MaxMemoryBox.Text, out var maxMemory)) { Invalid("instance.memory_invalid", MaxMemoryBox); return; }
        if (!ReadInteger(MinMemoryBox.Text, out var minMemory)) { Invalid("instance.memory_invalid", MinMemoryBox, true); return; }
        if (minMemory > maxMemory) { Invalid("instance.memory_order", MinMemoryBox, true); return; }
        if (!ReadInteger(WindowWidthBox.Text, out var width)) { Invalid("instance.window_invalid", WindowWidthBox); return; }
        if (!ReadInteger(WindowHeightBox.Text, out var height)) { Invalid("instance.window_invalid", WindowHeightBox); return; }
        var java = AutomaticJavaToggle.IsOn ? null : JavaPathBox.Text.Trim().Trim('"');
        if (java != null && (!Path.IsPathFullyQualified(java) || !File.Exists(java) ||
            !(Path.GetFileName(java).Equals("java.exe", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(java).Equals("javaw.exe", StringComparison.OrdinalIgnoreCase))))
        { Invalid("instance.java_invalid", JavaPathBox, true); return; }
        try
        {
            var current = _instances.GetInstance(_instance.Id) ?? throw new InvalidOperationException(App.L("feature.missing"));
            current.Name = name;
            current.MaxMemoryMb = maxMemory;
            current.MinMemoryMb = minMemory;
            current.WindowWidth = width;
            current.WindowHeight = height;
            current.JavaPath = java;
            current.JvmArgs = JvmArgsBox.Text.Trim();
            _instances.SaveInstance(current);
            _instance = current;
            TitleText.Text = current.Name;
            LoadSettings();
            Notice.Severity = InfoBarSeverity.Success;
            Notice.Message = App.L("instance.saved");
            Notice.IsOpen = true;
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }
    private void InstanceMods_Click(object sender, RoutedEventArgs e)
    {
        if (_instance == null) return;
        var frame = Frame;
        App.Settings.SelectedInstanceId = _instance.Id;
        App.Settings.Save();
        if (App.MainWindow is MainWindow main) main.NavigateToTag("Mods");
        if (frame.Content is not ModsPage) frame.Navigate(typeof(ModsPage));
    }
    private void BuildCompatibility()
    {
        TextContent.Children.Add(Text(App.L("compat.hint"), true));
        var results = new StackPanel { Spacing = 10 };
        var check = Button(App.L("compat.check"), () => { });
        check.Click += async (_, _) =>
        {
            if (_checking || _instance == null) return;
            if (App.IsInstanceBusy(_instance.Id)) { ShowError(App.L("feature.busy")); return; }
            _checking = true;
            check.IsEnabled = false;
            results.Children.Clear();
            var busy = new ProgressBar { IsIndeterminate = true, Height = 4 };
            results.Children.Add(busy);
            try
            {
                var report = await new ModCompatibilityChecker().CheckAsync(_instance, GameDir, true, _lifetime.Token);
                if (_lifetime.IsCancellationRequested) return;
                results.Children.Clear();
                results.Children.Add(Text(string.Format(App.L("compat.summary"), report.EnabledFiles, report.IdentifiedFiles, report.DisabledFiles), true));
                if (report.IdentifiedFiles < report.EnabledFiles) results.Children.Add(Text(App.L("compat.unknown"), true));
                if (report.Issues.Count == 0) results.Children.Add(Text(App.L("compat.clean")));
                foreach (var issue in report.Issues)
                {
                    var panel = new StackPanel { Spacing = 6 };
                    panel.Children.Add(Text(App.L("compat." + issue.Code)));
                    if (issue.Detail.Length > 0) panel.Children.Add(Text(issue.Detail, true));
                    var info = new InfoBar { IsOpen = true, IsClosable = false,
                        Severity = issue.IsError ? InfoBarSeverity.Error : InfoBarSeverity.Warning, Content = panel };
                    results.Children.Add(info);
                }
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
            catch (Exception ex) { results.Children.Clear(); ShowError(ex.Message); }
            finally { _checking = false; check.IsEnabled = true; results.Children.Remove(busy); }
        };
        TextContent.Children.Add(check);
        TextContent.Children.Add(results);
    }
    private void LoadScreenshots()
    {
        try
        {
            _screenshots = InstanceMedia.GetScreenshots(GameDir);
            _shown = 0;
            Screenshots.Items.Clear();
            AddScreenshots();
            GalleryCount.Text = _screenshots.Count == 0 ? App.L("gallery.empty") : _screenshots.Count.ToString();
            GalleryCount.TextWrapping = TextWrapping.Wrap;
            GalleryCount.MaxWidth = 500;
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }
    private void AddScreenshots()
    {
        foreach (var item in _screenshots.Skip(_shown).Take(60))
        {
            Screenshots.Items.Add(new ScreenshotItem(item, new BitmapImage { DecodePixelWidth = 460, UriSource = new Uri(item.Path) }));
            _shown++;
        }
        MoreScreenshots.Visibility = _shown < _screenshots.Count ? Visibility.Visible : Visibility.Collapsed;
    }
    private void RefreshGallery_Click(object sender, RoutedEventArgs e) => LoadScreenshots();
    private void MoreScreenshots_Click(object sender, RoutedEventArgs e) => AddScreenshots();
    private void Thumbnail_Failed(object sender, ExceptionRoutedEventArgs e)
    {
        if (sender is Image image) ToolTipService.SetToolTip(image, App.L("gallery.unreadable"));
    }
    private async void Screenshot_Click(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not ScreenshotItem clicked) return;
        var selected = clicked.File;
        int index = _screenshots.ToList().IndexOf(selected);
        var preview = new Image { Height = 330, Stretch = Stretch.Uniform };
        var details = Text("");
        var error = Text("");
        preview.ImageFailed += (_, _) => error.Text = App.L("gallery.unreadable");
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(Button(App.L("feature.open"), () => OpenPath(_screenshots[index].Path)));
        var save = Button(App.L("gallery.save_copy"), () => { });
        save.Click += async (_, _) =>
        {
            try
            {
                var item = _screenshots[index];
                var picker = new FileSavePicker { SuggestedFileName = Path.GetFileNameWithoutExtension(item.Name) };
                picker.FileTypeChoices.Add("Image", [Path.GetExtension(item.Path)]);
                InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow));
                var file = await picker.PickSaveFileAsync();
                if (file != null && !file.Path.Equals(item.Path, StringComparison.OrdinalIgnoreCase))
                {
                    var source = await StorageFile.GetFileFromPathAsync(item.Path);
                    await source.CopyAndReplaceAsync(file);
                }
            }
            catch (Exception ex) { error.Text = App.L("feature.error", ex.Message); }
        };
        actions.Children.Add(save);
        var content = new StackPanel { Spacing = 10, MinWidth = 440 };
        content.Children.Add(preview); content.Children.Add(details); content.Children.Add(actions); content.Children.Add(error);
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Content = content,
            PrimaryButtonText = App.L("gallery.next"), SecondaryButtonText = App.L("gallery.previous"), CloseButtonText = App.L("feature.back") };
        void Update()
        {
            var item = _screenshots[index];
            preview.Source = new BitmapImage { DecodePixelWidth = 1200, UriSource = new Uri(item.Path) };
            details.Text = $"{item.Name}\n{item.Time:g} · {DownloadsPage.Size(item.Size)}";
            error.Text = "";
            dialog.Title = $"{index + 1} / {_screenshots.Count}";
            dialog.IsPrimaryButtonEnabled = index < _screenshots.Count - 1;
            dialog.IsSecondaryButtonEnabled = index > 0;
        }
        dialog.PrimaryButtonClick += (_, args) => { args.Cancel = true; index++; Update(); };
        dialog.SecondaryButtonClick += (_, args) => { args.Cancel = true; index--; Update(); };
        Update();
        await dialog.ShowAsync();
    }
    private void BuildCrashes()
    {
        var reports = CrashAnalyzer.GetReports(GameDir);
        if (reports.Count == 0) TextContent.Children.Add(Text(App.L("crash.empty"), true));
        foreach (var report in reports)
        {
            var panel = new StackPanel { Spacing = 10 };
            panel.Children.Add(Text($"{report.Time.LocalDateTime:g} · {App.L("crash.title", report.ExitCode)}", true));
            panel.Children.Add(Text(App.L("crash." + report.Reason)));
            panel.Children.Add(Text(App.L("crash." + report.Reason + ".help"), true));
            if (report.Evidence.Length > 0) panel.Children.Add(Text(report.Evidence));
            panel.Children.Add(Button(App.L("feature.copy"), () =>
            {
                var data = new DataPackage();
                data.SetText($"{_instance!.Name} · Minecraft {_instance.McVersion} · {_instance.Loader}\n{report.Time:O} · {report.ExitCode}\n" +
                    App.L("crash." + report.Reason) + "\n" + App.L("crash." + report.Reason + ".help") + "\n\n" + report.LogTail);
                Clipboard.SetContent(data);
            }));
            panel.Children.Add(new Expander { Header = App.L("home.game_output"), HorizontalAlignment = HorizontalAlignment.Stretch,
                Content = new ScrollViewer { MaxHeight = 280, Content = Text(report.LogTail.Length > 0 ? report.LogTail : App.L("crash.unknown")) } });
            TextContent.Children.Add(Card(panel));
        }
    }
    private void BuildAppearance()
    {
        if (_appearanceContent != null) { TextContent.Children.Add(_appearanceContent); return; }
        var content = _appearanceContent = new StackPanel { Spacing = 12 };
        TextContent.Children.Add(content);
        _cover = _instances.GetCoverAbsolutePath(_instance!);
        _accent = _instance!.AccentColor;
        _icon = _instances.GetIconAbsolutePath(_instance);
        content.Children.Add(Text(App.L("appearance.hint"), true));
        var iconPreview = new Border { Name = "AppearanceIconPreview", Width = 56, Height = 56, CornerRadius = new CornerRadius(8), Background = Brush("CardBrush") };
        void UpdateIcon() => iconPreview.Child = _icon == null
            ? new FontIcon { Glyph = "\uE74C", FontSize = 24 }
            : new Image { Source = new BitmapImage { DecodePixelWidth = 112, UriSource = new Uri(_icon) }, Stretch = Stretch.UniformToFill };
        UpdateIcon();
        var pickIcon = Button(App.L("appearance.icon_pick"), () => { });
        pickIcon.Click += async (_, _) =>
        {
            try
            {
                var picker = new FileOpenPicker();
                foreach (var ext in new[] { ".png", ".jpg", ".jpeg", ".webp" }) picker.FileTypeFilter.Add(ext);
                InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow));
                var file = await picker.PickSingleFileAsync();
                if (file == null || _lifetime.IsCancellationRequested) return;
                if (new FileInfo(file.Path).Length > 20 * 1024 * 1024) throw new InvalidDataException(App.L("appearance.image_size"));
                using var stream = await file.OpenReadAsync();
                var bitmap = new BitmapImage { DecodePixelWidth = 112 };
                await bitmap.SetSourceAsync(stream);
                _icon = file.Path;
                UpdateIcon();
            }
            catch (Exception ex) { ShowError(ex.Message); }
        };
        content.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children =
        {
            iconPreview, new StackPanel { Spacing = 6, Children =
            {
                Text(App.L("appearance.icon")), new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8,
                    Children = { pickIcon, Button(App.L("appearance.icon_clear"), () => { _icon = null; UpdateIcon(); }) } }
            } }
        } });
        content.Children.Add(Text(App.L("appearance.cover")));
        var image = new Image { Height = 180, Stretch = Stretch.UniformToFill };
        var preview = new Border { Height = 184, CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(2), Child = image, Background = Brush("CardBrush") };
        void Update()
        {
            image.Source = _cover == null ? null : new BitmapImage { DecodePixelWidth = 1200, UriSource = new Uri(_cover) };
            preview.BorderBrush = AccentBrush(_accent);
        }
        Update();
        content.Children.Add(preview);
        var pick = Button(App.L("appearance.pick"), () => { });
        pick.Click += async (_, _) =>
        {
            try
            {
                var picker = new FileOpenPicker();
                foreach (var ext in new[] { ".png", ".jpg", ".jpeg" }) picker.FileTypeFilter.Add(ext);
                InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow));
                var file = await picker.PickSingleFileAsync();
                if (file == null || _lifetime.IsCancellationRequested) return;
                if (new FileInfo(file.Path).Length > 20 * 1024 * 1024) throw new InvalidDataException(App.L("appearance.image_size"));
                using var stream = await file.OpenReadAsync();
                var bitmap = new BitmapImage { DecodePixelWidth = 1200 };
                await bitmap.SetSourceAsync(stream);
                _cover = file.Path;
                Update();
            }
            catch (Exception ex) { ShowError(ex.Message); }
        };
        content.Children.Add(pick);
        var colors = new ComboBox { Header = App.L("appearance.accent"), MinWidth = 220 };
        colors.Items.Add(new ComboBoxItem { Content = App.L("appearance.reset"), Tag = "" });
        foreach (var color in new[] { "#548963", "#507EAA", "#A17748", "#8B6FAD", "#B56C78", "#468B89" })
        {
            var swatch = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            swatch.Children.Add(new Border { Background = AccentBrush(color), Width = 18, Height = 18, CornerRadius = new CornerRadius(9) });
            swatch.Children.Add(Text(color));
            colors.Items.Add(new ComboBoxItem { Content = swatch, Tag = color });
        }
        colors.SelectedItem = colors.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == _accent) ?? colors.Items[0];
        colors.SelectionChanged += (_, _) => { _accent = (colors.SelectedItem as ComboBoxItem)?.Tag as string; if (_accent == "") _accent = null; Update(); };
        content.Children.Add(colors);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var save = Button(App.L("feature.save"), () =>
        {
            try
            {
                if (App.IsInstanceBusy(_instance.Id)) { ShowError(App.L("feature.busy")); return; }
                var current = _instances.GetInstance(_instance.Id) ?? throw new InvalidOperationException(App.L("feature.missing"));
                if (_icon == null) current.IconPath = null;
                else if (!string.Equals(_icon, _instances.GetIconAbsolutePath(current), StringComparison.OrdinalIgnoreCase))
                    _instances.SetIconFromFile(current, _icon, save: false);
                _instances.SaveAppearance(current, _cover, _accent);
                _instance = current;
                _cover = _instances.GetCoverAbsolutePath(current);
                _icon = _instances.GetIconAbsolutePath(current);
                Notice.Severity = InfoBarSeverity.Success; Notice.Message = App.L("appearance.saved"); Notice.IsOpen = true;
            }
            catch (Exception ex) { ShowError(ex.Message); }
        });
        save.Style = (Style)Application.Current.Resources["AccentSmallButton"];
        actions.Children.Add(save);
        actions.Children.Add(Button(App.L("appearance.reset"), () => { _cover = null; _accent = null; _icon = null; colors.SelectedIndex = 0; Update(); UpdateIcon(); }));
        content.Children.Add(actions);
    }
    internal static Brush AccentBrush(string? value) => InstanceMedia.IsAccent(value)
        ? new SolidColorBrush(Windows.UI.Color.FromArgb(255, Convert.ToByte(value![1..3], 16), Convert.ToByte(value[3..5], 16), Convert.ToByte(value[5..7], 16)))
        : Brush("CardBorderBrush");
    private static TextBlock Text(string value, bool subtle = false) => new() { Text = value, TextWrapping = TextWrapping.Wrap,
        IsTextSelectionEnabled = true, Foreground = Brush(subtle ? "SubtleBrush" : "PrimaryBrush") };
    private static Border Card(UIElement child) => new() { Style = (Style)Application.Current.Resources["CardPanelWide"], Child = child };
    private static Button Button(string title, Action action)
    {
        var button = new Button { Content = title, Style = (Style)Application.Current.Resources["SmallButton"] };
        button.Click += (_, _) => action();
        return button;
    }
    private void OpenPath(string path)
    {
        try { if (File.Exists(path) || Directory.Exists(path)) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { ShowError(ex.Message); }
    }
    private void ShowError(string message) { Notice.Severity = InfoBarSeverity.Error; Notice.Message = message; Notice.IsOpen = true; }
}
