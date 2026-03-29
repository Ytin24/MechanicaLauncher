using Microsoft.UI.Windowing;
using MechanicaLauncher.Core.Protocol;
using WinRT.Interop;

namespace MechanicaLauncher;

public sealed partial class OverlayPopup : Window
{
    private ConnectRequest? _request;
    private static OverlayPopup? _current;

    public OverlayPopup()
    {
        this.InitializeComponent();
        this.ExtendsContentIntoTitleBar = true;

        var hwnd = WindowNative.GetWindowHandle(this);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
        var appWindow = AppWindow.GetFromWindowId(windowId);

        appWindow.Resize(new Windows.Graphics.SizeInt32(360, 160));

        if (appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = true;
            presenter.IsResizable = false;
            presenter.IsMinimizable = false;
            presenter.IsMaximizable = false;
            presenter.SetBorderAndTitleBar(false, false);
        }

        // Position over MC window or bottom-right of screen
        PositionOverMC(appWindow, windowId);

        _ = AutoCloseAsync();
        _ = KeepOnTopAsync();
    }

    public static void Show(ConnectRequest request)
    {
        try { _current?.Close(); } catch { }

        var popup = new OverlayPopup();
        popup._request = request;
        popup.TitleText.Text = $"Подключиться к {request.Server}?";
        popup.DescText.Text = $"MC {request.Version} · Port {request.Port}";
        popup.Activate();
        _current = popup;
    }

    private void Yes_Click(object sender, RoutedEventArgs e)
    {
        if (_request != null)
        {
            App.PendingConnect = _request;
            App.IsReconnecting = true;

            // Kill running MC
            foreach (var kv in App.RunningInstances)
            {
                try { kv.Value.Kill(); } catch { }
            }
            App.RunningInstances.Clear();

            // Show main window and handle connect
            App.ShowWindow();
            if (App.MainWindow is MainWindow mw)
                mw.DispatcherQueue.TryEnqueue(() => mw.HandlePendingConnect());
        }
        this.Close();
        _current = null;
    }

    private void No_Click(object sender, RoutedEventArgs e)
    {
        this.Close();
        _current = null;
    }

    private void PositionOverMC(AppWindow appWindow, Microsoft.UI.WindowId windowId)
    {
        // Try to find MC window position
        foreach (var kv in App.RunningInstances)
        {
            try
            {
                var proc = kv.Value;
                if (proc.HasExited || proc.MainWindowHandle == IntPtr.Zero) continue;

                var mcId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(proc.MainWindowHandle);
                var mcApp = AppWindow.GetFromWindowId(mcId);
                if (mcApp != null)
                {
                    var mcPos = mcApp.Position;
                    var mcSize = mcApp.Size;
                    appWindow.Move(new Windows.Graphics.PointInt32(
                        mcPos.X + mcSize.Width - 380,
                        mcPos.Y + mcSize.Height - 180));
                    return;
                }
            }
            catch { }
        }

        // Fallback: bottom-right of screen
        var area = DisplayArea.GetFromWindowId(windowId, DisplayAreaFallback.Primary);
        if (area != null)
        {
            var workArea = area.WorkArea;
            appWindow.Move(new Windows.Graphics.PointInt32(
                workArea.X + workArea.Width - 380,
                workArea.Y + workArea.Height - 180));
        }
    }

    private async Task KeepOnTopAsync()
    {
        for (int i = 0; i < 30; i++)
        {
            await Task.Delay(500);
            try { this.Activate(); } catch { return; }
        }
    }

    private async Task AutoCloseAsync()
    {
        await Task.Delay(15000);
        try { this.Close(); } catch { }
        _current = null;
    }
}
