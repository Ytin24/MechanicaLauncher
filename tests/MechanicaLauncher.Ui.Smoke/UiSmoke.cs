using MechanicaLauncher.Core.Game;
using MechanicaLauncher.Core.Instances;
using MechanicaLauncher.Core.IO;
using MechanicaLauncher.Core.Servers;
using MechanicaLauncher.Views;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using System.Threading;

namespace MechanicaLauncher;

internal static partial class UiSmoke
{
    [STAThread]
    public static void Main()
    {
        if (!LauncherPaths.HasCustomDataDirectory) throw new InvalidOperationException("UI smoke requires an isolated data directory.");
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(_ =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            new SmokeApp();
        });
    }

    private sealed partial class SmokeApp : App
    {
        private readonly string _log = Path.Combine(LauncherPaths.DataDirectory, "ui-smoke.log");
        protected override async void OnLaunched(LaunchActivatedEventArgs args)
        {
            try
            {
                UnhandledException += (_, e) => { File.AppendAllText(_log, "FAIL " + e.Exception + "\n"); Environment.Exit(1); };
                var manager = new InstanceManager();
                var instance = manager.CreateInstance("Тестовая сборка — длинное название", "1.21.1", LoaderType.Fabric, "0.16.0");
                var gameDir = manager.GetGameDir(instance.Id);
                var screenshots = Path.Combine(gameDir, "screenshots");
                Directory.CreateDirectory(screenshots);
                var cover = Path.Combine(screenshots, "2026-09-22_скриншот.png");
                File.Copy(Environment.GetEnvironmentVariable("MECHANICA_SMOKE_IMAGE") ?? throw new InvalidOperationException("Test image is missing."), cover);
                manager.SaveAppearance(instance, cover, "#507EAA");
                manager.SetIconFromFile(instance, cover);
                CrashAnalyzer.SaveReport(gameDir, CrashAnalyzer.Analyze("java.lang.OutOfMemoryError: Java heap space", -1));
                new FavoriteServers().Save([new("test", "Тестовый сервер с длинным названием", "localhost", 25565, instance.Id)]);
                Settings.SelectedInstanceId = instance.Id;
                Settings.Save();
                Downloads.Enqueue("Успешная установка → тестовая сборка", instance.Id, _ => Task.CompletedTask);
                Downloads.Enqueue("Установка с ошибкой", instance.Id, _ => throw new IOException("Тестовая ошибка сети"));
                base.OnLaunched(args);
                await Task.Delay(500);
                var discordSession = Discord.BeginPreparation(instance);
                Discord.GameStarted(discordSession, 8);
                Discord.ProcessLogLine(discordSession, "[12:00:00] [Render thread/INFO]: Sound engine started");
                var root = (FrameworkElement)MainWindow.Content;
                var nav = ((Grid)root).Children.OfType<NavigationView>().Single();
                var frame = (Frame)nav.Content;
                foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
                foreach (var width in new[] { 800, 1100 })
                {
                    root.RequestedTheme = theme;
                    MainWindow.AppWindow.Resize(new Windows.Graphics.SizeInt32(width, 720));
                    foreach (var (type, parameter) in new (Type, object?)[]
                    {
                        (typeof(HomePage), null), (typeof(InstancesPage), null),
                        (typeof(DownloadsPage), null), (typeof(ServersPage), null), (typeof(SettingsPage), null),
                        (typeof(InstanceDetailsPage), new InstanceDetailsRequest(instance.Id)),
                        (typeof(InstanceDetailsPage), new InstanceDetailsRequest(instance.Id, "compatibility")),
                        (typeof(InstanceDetailsPage), new InstanceDetailsRequest(instance.Id, "screenshots")),
                        (typeof(InstanceDetailsPage), new InstanceDetailsRequest(instance.Id, "crashes")),
                        (typeof(InstanceDetailsPage), new InstanceDetailsRequest(instance.Id, "appearance"))
                    })
                    {
                        if (type == typeof(SettingsPage)) Settings.DiscordRpc = true;
                        Require(frame.Navigate(type, parameter), "Navigation failed");
                        await Task.Delay(220);
                        var page = (Page)frame.Content;
                        Require(page.IsLoaded && page.ActualWidth > 0 && page.ActualHeight > 0, "Page not laid out");
                        foreach (var text in Descendants(page).OfType<TextBlock>().Where(t => t.IsLoaded && t.Visibility == Visibility.Visible && t.Text.Length > 0))
                            Require(!text.Text.StartsWith("feature.") && !text.Text.StartsWith("compat.") && !text.Text.StartsWith("servers.") && !text.Text.StartsWith("discord.") && !text.Text.StartsWith("instance.") && !text.Text.StartsWith("appearance."), "Missing localization: " + text.Text);
                        var tabs = page.FindName("Tabs") as StackPanel;
                        if (tabs != null) Require(tabs.Children.Sum(c => ((FrameworkElement)c).ActualWidth) + tabs.Spacing * (tabs.Children.Count - 1) <= tabs.ActualWidth + 1, "Instance tabs overflow");
                        if (page is InstanceDetailsPage && parameter is InstanceDetailsRequest { Tab: "settings" })
                        {
                            Require(((Grid)page.FindName("SettingsPanel")).Visibility == Visibility.Visible, "Default instance page is not game settings");
                            Require(!((Expander)page.FindName("AdvancedSettings")).IsExpanded, "Advanced settings should start collapsed");
                            var scroll = (ScrollViewer)page.FindName("SettingsScroll");
                            var mods = (Button)page.FindName("InstanceModsButton");
                            Require(mods.TransformToVisual(scroll).TransformPoint(new Windows.Foundation.Point(0, mods.ActualHeight)).Y <= scroll.ActualHeight + 1, "Basic game settings need scrolling");
                            await Capture(root, $"instance-settings-{theme}-{width}.png");
                            if (theme == ElementTheme.Light && width == 800) await CheckInstanceSettings(page, manager, instance.Id);
                        }
                        if (page is InstanceDetailsPage && parameter is InstanceDetailsRequest { Tab: "screenshots" })
                        {
                            var gallery = (GridView)page.FindName("Screenshots");
                            Require(gallery.Items.Count == 1, "Gallery is empty");
                            Require(Descendants(gallery).OfType<Image>().Any(i => i.Source is Microsoft.UI.Xaml.Media.Imaging.BitmapImage { PixelWidth: > 0 }), "Gallery image was not decoded");
                        }
                        var background = ((SolidColorBrush)Current.Resources["CardBrush"]).Color;
                        if (page is SettingsPage)
                        {
                            var preview = (Border)page.FindName("DiscordPreview");
                            Require(preview.Visibility == Visibility.Visible, "Discord preview hidden");
                            Require(((TextBlock)page.FindName("DiscordDetailsText")).Text == "Главное меню", "Discord preview did not reflect game state");
                            Require(((TextBlock)page.FindName("DiscordStateText")).Text.Contains("1.21.1"), "Discord preview missing version");
                            Require(((TextBlock)page.FindName("DiscordTimeText")).Visibility == Visibility.Visible, "Discord preview missing timer");
                            foreach (var text in Descendants(preview).OfType<TextBlock>().Where(t => t.IsLoaded && t.Text.Length > 0))
                                Require(text.Foreground is SolidColorBrush brush && Contrast(brush.Color, background) >= 4.5, "Low Discord preview contrast");
                            ((ToggleSwitch)page.FindName("DiscordRpcToggle")).IsOn = false;
                            Require(preview.Visibility == Visibility.Collapsed && !Settings.DiscordRpc, "Discord off toggle did not apply");
                            var language = (ComboBox)page.FindName("LangSelector");
                            language.SelectedItem = language.Items.OfType<ComboBoxItem>().Single(i => i.Tag?.ToString() == "en");
                            Require(((TextBlock)page.FindName("DiscordStatusText")).Text == "Discord status is off", "Discord English localization missing");
                            language.SelectedItem = language.Items.OfType<ComboBoxItem>().Single(i => i.Tag?.ToString() == "ru");
                        }
                        if (page is DownloadsPage or ServersPage or InstanceDetailsPage)
                        {
                            foreach (var text in Descendants(page).OfType<TextBlock>().Where(t => t.IsLoaded && t.ActualWidth > 0 && t.Text.Length > 0 && !InsideButton(t)))
                            {
                                if (text.Foreground is not SolidColorBrush brush) continue;
                                Require(Contrast(brush.Color, background) >= 4.5,
                                    $"Low text contrast: {theme} {type.Name} {text.Text[..Math.Min(40, text.Text.Length)]} color={brush.Color} card={background}");
                            }
                        }
                        File.AppendAllText(_log, $"PASS {theme} {width} {type.Name} {(parameter as InstanceDetailsRequest)?.Tab}\n");
                    }
                }
                await CheckCatalogFilters(frame, instance);
                Settings.Language = "en";
                Core.Localization.Locale.Init("en");
                MainWindow.AppWindow.Resize(new Windows.Graphics.SizeInt32(800, 720));
                Require(frame.Navigate(typeof(InstanceDetailsPage), new InstanceDetailsRequest(instance.Id)), "English settings navigation failed");
                await Task.Delay(220);
                var english = (Page)frame.Content;
                Require(((TextBlock)english.FindName("MemoryTitle")).Text == "Game memory", "English settings localization missing");
                var englishTabs = (StackPanel)english.FindName("Tabs");
                Require(englishTabs.Children.Sum(c => ((FrameworkElement)c).ActualWidth) + englishTabs.Spacing * (englishTabs.Children.Count - 1) <= englishTabs.ActualWidth + 1, "English instance tabs overflow");
                File.AppendAllText(_log, "PASS English instance settings\n");
                File.AppendAllText(_log, "PASS all page components\n");
                Discord.EndSession(discordSession);
                MainWindow.Close();
            }
            catch (Exception ex) { File.AppendAllText(_log, "FAIL " + ex + "\n"); Environment.Exit(1); }
        }
        private static async Task CheckInstanceSettings(Page page, InstanceManager manager, string id)
        {
            var save = (Button)page.FindName("SaveSettingsButton");
            var notice = (InfoBar)page.FindName("Notice");
            var name = (TextBox)page.FindName("InstanceNameBox");
            var max = (NumberBox)page.FindName("MaxMemoryBox");
            var min = (TextBox)page.FindName("MinMemoryBox");
            var width = (NumberBox)page.FindName("WindowWidthBox");
            var height = (NumberBox)page.FindName("WindowHeightBox");
            var automatic = (ToggleSwitch)page.FindName("AutomaticJavaToggle");
            var java = (TextBox)page.FindName("JavaPathBox");
            var args = (TextBox)page.FindName("JvmArgsBox");
            var advanced = (Expander)page.FindName("AdvancedSettings");
            var file = Path.Combine(manager.GetInstanceDir(id), "instance.json");
            var original = File.ReadAllText(file);
            await Invoke(save);
            Require(notice.IsOpen && notice.Severity == InfoBarSeverity.Success, "Saving with untouched collapsed advanced settings failed");
            Require(File.ReadAllText(file) == original, "Saving untouched settings changed their values");
            async Task Reject(Action change, Action restore, string label)
            {
                notice.IsOpen = false;
                change();
                await Invoke(save);
                Require(notice.IsOpen && notice.Severity == InfoBarSeverity.Error, label + " was accepted");
                Require(File.ReadAllText(file) == original, label + " changed the saved instance");
                restore();
            }
            var oldName = name.Text;
            await Reject(() => name.Text = "   ", () => name.Text = oldName, "Empty name");
            await Reject(() => max.Value = double.NaN, () => max.Value = 4096, "Empty memory");
            await Reject(() => max.Value = -1, () => max.Value = 4096, "Negative memory");
            await Reject(() => { max.Focus(FocusState.Programmatic); max.Text = "не число"; }, () => max.Value = 4096, "Invalid memory text");
            await Reject(() => min.Text = "8192", () => min.Text = "2048", "Initial memory above the limit");
            await Reject(() => width.Value = 0, () => width.Value = 1600, "Zero window width");
            await Reject(() => height.Value = 900.5, () => height.Value = 900, "Fractional window height");
            await Reject(() => { automatic.IsOn = false; java.Text = "C:\\missing-java\\java.exe"; }, () => automatic.IsOn = true, "Missing Java");
            PreparingInstanceId = id;
            try { await Reject(() => { }, () => { }, "Busy instance"); }
            finally { PreparingInstanceId = null; }
            max.Value = 1024;
            Require(min.Text == "1024", "Lowering the limit left an invalid hidden minimum");
            max.Value = 6144;
            name.Text = "  Моя личная сборка  ";
            args.Text = "  -XX:+UseG1GC  ";
            var tabs = (StackPanel)page.FindName("Tabs");
            await Invoke(tabs.Children.OfType<Button>().Single(b => (string)b.Tag == "appearance"));
            var colors = Descendants(page).OfType<ComboBox>().Single();
            colors.SelectedIndex = 2;
            var selectedColor = colors.SelectedIndex;
            await Invoke(Descendants(page).OfType<Button>().Single(b => b.Content as string == L("appearance.icon_clear")));
            await Invoke(tabs.Children.OfType<Button>().Single(b => (string)b.Tag == "settings"));
            Require(max.Value == 6144 && name.Text == "  Моя личная сборка  ", "Changing tabs lost unsaved game settings");
            await Invoke(tabs.Children.OfType<Button>().Single(b => (string)b.Tag == "appearance"));
            Require(colors.SelectedIndex == selectedColor, "Changing tabs lost unsaved appearance");
            await Invoke(tabs.Children.OfType<Button>().Single(b => (string)b.Tag == "settings"));
            Require(File.ReadAllText(file) == original, "Editing a draft saved it before Save");
            var latest = manager.GetInstance(id)!;
            latest.LastPlayed = new DateTime(2026, 9, 22, 11, 30, 0, DateTimeKind.Utc);
            latest.AccentColor = "#A17748";
            latest.LoaderVersion = "0.16.1";
            manager.SaveInstance(latest);
            await Invoke(save);
            Require(notice.IsOpen && notice.Severity == InfoBarSeverity.Success, "Valid game settings were not saved: " + notice.Message);
            var saved = manager.GetInstance(id)!;
            Require(saved.Name == "Моя личная сборка" && saved.MaxMemoryMb == 6144 && saved.MinMemoryMb == 1024, "Memory or name did not persist");
            Require(saved.WindowWidth == 1600 && saved.WindowHeight == 900 && saved.JvmArgs == "-XX:+UseG1GC" && saved.JavaPath == null, "Launch settings did not persist");
            Require(saved.LastPlayed == latest.LastPlayed && saved.AccentColor == latest.AccentColor && saved.LoaderVersion == latest.LoaderVersion && saved.CoverPath == latest.CoverPath && saved.IconPath == latest.IconPath, "Saving settings overwrote newer unrelated data");
            var javaFile = Path.Combine(LauncherPaths.DataDirectory, "javaw.exe");
            File.WriteAllText(javaFile, "UI test path; never executed");
            automatic.IsOn = false;
            java.Text = javaFile;
            await Invoke(save);
            Require(manager.GetInstance(id)!.JavaPath == javaFile, "Custom Java path did not persist");
            automatic.IsOn = true;
            await Invoke(save);
            Require(manager.GetInstance(id)!.JavaPath == null, "Automatic Java did not clear the custom path");
            await Invoke(tabs.Children.OfType<Button>().Single(b => (string)b.Tag == "appearance"));
            await Invoke(Descendants((StackPanel)page.FindName("TextContent")).OfType<Button>().Single(b => b.Content as string == L("feature.save")));
            saved = manager.GetInstance(id)!;
            Require(saved.IconPath == null && saved.AccentColor == "#507EAA" && saved.MaxMemoryMb == 6144 && saved.Name == "Моя личная сборка", "Appearance save did not apply the icon draft or overwrote game settings");
            await Invoke(tabs.Children.OfType<Button>().Single(b => (string)b.Tag == "settings"));
            advanced.IsExpanded = false;
            ((ScrollViewer)page.FindName("SettingsScroll")).ChangeView(null, 0, null, true);
        }
        private static async Task Invoke(Button button)
        {
            var clicked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            RoutedEventHandler handler = (_, _) => clicked.TrySetResult();
            button.Click += handler;
            try
            {
                var peer = new Microsoft.UI.Xaml.Automation.Peers.ButtonAutomationPeer(button);
                ((Microsoft.UI.Xaml.Automation.Provider.IInvokeProvider)peer.GetPattern(Microsoft.UI.Xaml.Automation.Peers.PatternInterface.Invoke)).Invoke();
                await clicked.Task.WaitAsync(TimeSpan.FromSeconds(3));
                ((FrameworkElement)MainWindow.Content).UpdateLayout();
                await Task.Delay(80);
            }
            finally { button.Click -= handler; }
        }
        private static async Task Capture(FrameworkElement element, string name)
        {
            var bitmap = new Microsoft.UI.Xaml.Media.Imaging.RenderTargetBitmap();
            await bitmap.RenderAsync(element);
            var pixels = await bitmap.GetPixelsAsync();
            using var reader = Windows.Storage.Streams.DataReader.FromBuffer(pixels);
            var bytes = new byte[pixels.Length];
            reader.ReadBytes(bytes);
            var folder = await Windows.Storage.StorageFolder.GetFolderFromPathAsync(LauncherPaths.DataDirectory);
            var file = await folder.CreateFileAsync(name, Windows.Storage.CreationCollisionOption.ReplaceExisting);
            using var stream = await file.OpenAsync(Windows.Storage.FileAccessMode.ReadWrite);
            var encoder = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, stream);
            encoder.SetPixelData(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied,
                (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, bytes);
            await encoder.FlushAsync();
        }
        private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                yield return child;
                foreach (var descendant in Descendants(child)) yield return descendant;
            }
        }
        private static bool InsideButton(DependencyObject element)
        {
            for (var current = VisualTreeHelper.GetParent(element); current != null; current = VisualTreeHelper.GetParent(current))
                if (current is Microsoft.UI.Xaml.Controls.Primitives.ButtonBase or ComboBox or InfoBar) return true;
            return false;
        }
        private static double Contrast(Windows.UI.Color foreground, Windows.UI.Color background)
        {
            static double Channel(double value) { value /= 255; return value <= .04045 ? value / 12.92 : Math.Pow((value + .055) / 1.055, 2.4); }
            static double Luma(double r, double g, double b) => .2126 * Channel(r) + .7152 * Channel(g) + .0722 * Channel(b);
            var alpha = foreground.A / 255d;
            var a = Luma(foreground.R * alpha + background.R * (1 - alpha), foreground.G * alpha + background.G * (1 - alpha), foreground.B * alpha + background.B * (1 - alpha));
            var b = Luma(background.R, background.G, background.B);
            return (Math.Max(a, b) + .05) / (Math.Min(a, b) + .05);
        }
        private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    }
}
