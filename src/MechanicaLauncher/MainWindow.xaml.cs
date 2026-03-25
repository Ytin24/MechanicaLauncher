using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Media;
using MechanicaLauncher.Views;
using WinRT.Interop;

namespace MechanicaLauncher;

public sealed partial class MainWindow : Window
{
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
        ContentFrame.Navigate(typeof(HomePage));
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
            ContentFrame.Navigate(pageType, null, new Microsoft.UI.Xaml.Media.Animation.SlideNavigationTransitionInfo
            {
                Effect = Microsoft.UI.Xaml.Media.Animation.SlideNavigationTransitionEffect.FromRight
            });
        }
    }
}
