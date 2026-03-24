using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Controls;
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
        appWindow.Resize(new Windows.Graphics.SizeInt32(1100, 700));
        appWindow.Title = "Mechanica Launcher";

        if (Content is FrameworkElement root)
            root.RequestedTheme = ElementTheme.Dark;

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
                "Versions" => typeof(VersionsPage),
                "Mods" => typeof(ModsPage),
                "Account" => typeof(AccountPage),
                "Settings" => typeof(SettingsPage),
                _ => typeof(HomePage)
            };
            ContentFrame.Navigate(pageType);
        }
    }
}
