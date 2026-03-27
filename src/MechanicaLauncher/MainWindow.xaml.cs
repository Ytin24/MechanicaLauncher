using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using MechanicaLauncher.Core.Instances;
using MechanicaLauncher.Core.Security;
using MechanicaLauncher.Views;
using System.Numerics;
using WinRT.Interop;

namespace MechanicaLauncher;

public sealed partial class MainWindow : Window
{
    private TLauncherScanResult? _scanResult;

    public MainWindow()
    {
        this.InitializeComponent();
        this.SystemBackdrop = new MicaBackdrop { Kind = MicaKind.BaseAlt };
        this.ExtendsContentIntoTitleBar = true;

        var hwnd = WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
        var appWindow = AppWindow.GetFromWindowId(windowId);
        appWindow.Resize(new Windows.Graphics.SizeInt32(1100, 720));
        appWindow.Title = "Mechanica Launcher";

        var presenter = appWindow.Presenter as OverlappedPresenter;
        if (presenter != null)
        {
            presenter.IsMinimizable = true;
            presenter.IsMaximizable = true;
            presenter.IsResizable = true;
        }

        this.SizeChanged += (_, _) =>
        {
            var size = appWindow.Size;
            if (size.Width < 800 || size.Height < 500)
                appWindow.Resize(new Windows.Graphics.SizeInt32(
                    Math.Max(size.Width, 800),
                    Math.Max(size.Height, 500)));
        };

        if (Content is FrameworkElement root)
        {
            root.RequestedTheme = App.Settings.Theme switch
            {
                "Light" => ElementTheme.Light,
                "Dark" => ElementTheme.Dark,
                _ => ElementTheme.Default
            };
        }

        ContentFrame.CacheSize = 5;

        _scanResult = TLauncherDetector.Scan();
        if (_scanResult.IsDetected)
        {
            ShowTLauncherLockscreen();
        }
        else
        {
            ContentFrame.Navigate(typeof(HomePage));
        }
    }

    private void ShowTLauncherLockscreen()
    {
        TLauncherOverlay.Visibility = Visibility.Visible;
        NavView.IsEnabled = false;

        TLTitle.Text = App.L("tl.title");
        TLDesc.Text = App.L("tl.desc");
        TLThreatCount.Text = App.L("tl.threats", _scanResult!.ThreatCount);
        TLAcceptBtn.Content = App.L("tl.accept");
        TLDeclineBtn.Content = App.L("tl.decline");

        StartTitleDistortion();
        StartTitleColorShimmer();
        StartButtonTimer();
    }

    private void StartTitleDistortion()
    {
        var sb = new Storyboard();
        var anim = new DoubleAnimationUsingKeyFrames();
        anim.KeyFrames.Add(new EasingDoubleKeyFrame { KeyTime = TimeSpan.FromMilliseconds(0), Value = 0 });
        anim.KeyFrames.Add(new EasingDoubleKeyFrame { KeyTime = TimeSpan.FromMilliseconds(400), Value = 12,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } });
        anim.KeyFrames.Add(new EasingDoubleKeyFrame { KeyTime = TimeSpan.FromMilliseconds(800), Value = 0,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } });
        anim.KeyFrames.Add(new EasingDoubleKeyFrame { KeyTime = TimeSpan.FromMilliseconds(1200), Value = -12,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } });
        anim.KeyFrames.Add(new EasingDoubleKeyFrame { KeyTime = TimeSpan.FromMilliseconds(1600), Value = 0,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } });
        anim.RepeatBehavior = RepeatBehavior.Forever;
        Storyboard.SetTarget(anim, TLTitleRotation);
        Storyboard.SetTargetProperty(anim, "Angle");
        sb.Children.Add(anim);
        sb.Begin();
    }

    private void StartTitleColorShimmer()
    {
        var sb = new Storyboard();
        var colorAnim = new ColorAnimationUsingKeyFrames();
        colorAnim.KeyFrames.Add(new LinearColorKeyFrame { KeyTime = TimeSpan.FromMilliseconds(0), Value = Windows.UI.Color.FromArgb(0xFF, 0xFF, 0x44, 0x44) });
        colorAnim.KeyFrames.Add(new LinearColorKeyFrame { KeyTime = TimeSpan.FromMilliseconds(1000), Value = Windows.UI.Color.FromArgb(0xFF, 0xFF, 0x88, 0x00) });
        colorAnim.KeyFrames.Add(new LinearColorKeyFrame { KeyTime = TimeSpan.FromMilliseconds(2000), Value = Windows.UI.Color.FromArgb(0xFF, 0xFF, 0x44, 0x44) });
        colorAnim.RepeatBehavior = RepeatBehavior.Forever;
        Storyboard.SetTarget(colorAnim, TLTitle);
        Storyboard.SetTargetProperty(colorAnim, "(TextBlock.Foreground).(SolidColorBrush.Color)");
        TLTitle.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xFF, 0x44, 0x44));
        sb.Children.Add(colorAnim);
        sb.Begin();
    }

    private async void StartButtonTimer()
    {
        for (int i = 20; i > 0; i--)
        {
            TLTimer.Text = App.L("tl.timer", i);
            await Task.Delay(1000);
        }
        TLTimer.Text = "";
        TLAcceptBtn.IsEnabled = true;
        TLDeclineBtn.IsEnabled = true;
    }

    private async void TLAccept_Click(object sender, RoutedEventArgs e)
    {
        TLAcceptBtn.Visibility = Visibility.Collapsed;
        TLDeclineBtn.Visibility = Visibility.Collapsed;
        TLTimer.Visibility = Visibility.Collapsed;
        TLProgress.Visibility = Visibility.Visible;
        TLProgressBar.IsIndeterminate = false;
        TLProgressBar.Value = 0;

        void UpdateUI(string key, double progress)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                TLProgressText.Text = App.L(key);
                TLProgressBar.Value = progress;
            });
        }

        var im = new InstanceManager();

        if (_scanResult?.MinecraftDir != null)
        {
            UpdateUI("tl.scanning", 5);
            await Task.Delay(300);

            var migrator = new TLauncherMigrator(im);
            migrator.StatusChanged += s =>
                DispatcherQueue.TryEnqueue(() => TLProgressText.Text = s);

            UpdateUI("tl.migrating", 10);
            await Task.Run(() => migrator.MigrateAsync(_scanResult.MinecraftDir));
            UpdateUI("tl.migration_done", 60);
            await Task.Delay(300);
        }

        UpdateUI("tl.removing", 65);
        TLauncherCleaner.StatusChanged += s =>
            DispatcherQueue.TryEnqueue(() => TLProgressText.Text = s);
        await Task.Run(TLauncherCleaner.Clean);

        UpdateUI("tl.registry", 85);
        await Task.Delay(300);

        UpdateUI("tl.almost", 95);
        await Task.Delay(300);

        UpdateUI("tl.done", 100);
        await Task.Delay(1500);

        TLauncherOverlay.Visibility = Visibility.Collapsed;
        NavView.IsEnabled = true;
        ContentFrame.Navigate(typeof(HomePage));
    }

    private void TLDecline_Click(object sender, RoutedEventArgs e)
    {
        App.MainWindow.Close();
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItemContainer is NavigationViewItem item)
        {
            var tag = item.Tag?.ToString();
            var pageType = tag switch
            {
                "Home" => typeof(HomePage),
                "Instances" => typeof(InstancesPage),
                "Mods" => typeof(ModsPage),
                "Account" => typeof(AccountPage),
                "Settings" => typeof(SettingsPage),
                _ => typeof(HomePage)
            };
            ContentFrame.Navigate(pageType, null, new SlideNavigationTransitionInfo
            {
                Effect = SlideNavigationTransitionEffect.FromRight
            });
        }
    }
}
