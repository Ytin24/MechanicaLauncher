using System.ComponentModel;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Nitidus.Native;

namespace MechanicaLauncher.Desktop;

internal sealed class LauncherWindowController : NativeWindow, IDisposable
{
    private const int WmSize = 0x5, WmEndSession = 0x16, WmSetIcon = 0x80, WmKeyDown = 0x100, WmSysCommand = 0x112;
    private const int WmImeStartComposition = 0x10d, WmImeEndComposition = 0x10e;
    private const int ScMinimize = 0xf020;
    private readonly ViewWindow window;
    private readonly LauncherModel model;
    private bool trayReady, quitting, confirmingExit, restoreMaximized, restoreAfterGame, hiding, disposed;
    private Icon? smallIcon, largeIcon;
    private bool composing;

    public LauncherWindowController(ViewWindow window, LauncherModel model)
    {
        this.window = window; this.model = model;
        model.CloseRequested = window.Close;
        model.ExitRequested = () => model.Run(Exit);
        model.MinimizeRequested = Minimize;
        model.MaximizeRequested = () => { if (window.State == WindowState.Maximized) window.Show(); else window.Maximize(); };
        model.ShowRequested = Show;
        model.AttentionRequested = Show;
        window.Opened += Opened;
        window.Closing += Closing;
        window.TrayActivated += Show;
        window.TrayContextMenuRequested += TrayMenu;
        model.Sessions.GameStarted += GameStarted;
        model.Sessions.GameExited += GameExited;
    }

    private void Opened()
    {
        Program.TraceStartup("Window controller opened");
        using var process = Process.GetCurrentProcess();
        AssignHandle(process.MainWindowHandle);
        Program.TraceStartup("Window handle assigned");
        string path = Path.Combine(AppContext.BaseDirectory, "Assets", "Mechanica.ico");
        if (File.Exists(path))
        {
            smallIcon = new(path, 16, 16); largeIcon = new(path, 32, 32);
            SendMessageW(Handle, WmSetIcon, 0, smallIcon.Handle);
            SendMessageW(Handle, WmSetIcon, 1, largeIcon.Handle);
        }
        Program.TraceStartup("Registering tray icon");
        try { window.SetTrayIcon("Mechanica Launcher", File.Exists(path) ? path : null); trayReady = true; }
        catch (InvalidOperationException)
        {
            model.Notice(model.T("Трей Windows недоступен. Окно будет сворачиваться на панель задач.", "The Windows tray is unavailable. The window will minimize to the taskbar."));
        }
        Program.TraceStartup("Window controller ready");
    }

    private void Closing(object? sender, CancelEventArgs e)
    {
        if (quitting) return;
        if (model.CloseToTray || model.Sessions.RunningCount > 0 || model.Sessions.Downloads.HasPending)
        {
            e.Cancel = true; HideToTray();
        }
    }

    private void RememberState()
    {
        if (window.State is WindowState.Normal or WindowState.Maximized)
            restoreMaximized = window.State == WindowState.Maximized;
    }

    private void HideToTray(bool forGame = false)
    {
        RememberState();
        restoreAfterGame = forGame;
        hiding = true;
        try { if (trayReady) window.Hide(); else window.Minimize(); }
        finally { hiding = false; }
    }

    private void Minimize()
    {
        RememberState(); restoreAfterGame = false;
        if (model.MinimizeToTray) HideToTray(); else window.Minimize();
    }

    private void Show()
    {
        restoreAfterGame = false;
        if (window.State != WindowState.Maximized)
        {
            if (restoreMaximized && window.State is WindowState.Hidden or WindowState.Minimized) window.Maximize();
            else window.Show();
        }
        if (Handle != 0) SetForegroundWindow(Handle);
    }

    private void GameStarted() => model.Dispatch(() =>
    {
        if (model.CloseOnLaunch && window.State is WindowState.Normal or WindowState.Maximized) HideToTray(forGame: true);
    });

    private void GameExited() => model.Dispatch(() =>
    {
        if (restoreAfterGame && model.Sessions.RunningCount == 0 && window.State is WindowState.Hidden or WindowState.Minimized) Show();
    });

    private void TrayMenu()
    {
        int command = window.ShowTrayMenu(
            new(1, model.T("Открыть Mechanica", "Open Mechanica")),
            new(2, model.T("Загрузки", "Downloads") + (model.DownloadBadge == "0" ? "" : " · " + model.DownloadBadge), !model.TLauncherBlocked),
            new(3, model.T("Настройки", "Settings"), model.ShowSettingsPage && !model.TLauncherBlocked),
            TrayMenuItem.Separator,
            new(4, model.T("Выход", "Quit")));
        switch (command)
        {
            case 1: Show(); break;
            case 2: Show(); model.Downloads(); break;
            case 3: Show(); model.Preferences(); break;
            case 4: model.Run(Exit); break;
        }
    }

    private async Task Exit()
    {
        if (quitting || confirmingExit) return;
        if (model.DialogBusy)
        {
            Show(); model.Notice(model.T("Дождись завершения текущего действия перед выходом.", "Wait for the current action to finish before quitting.")); return;
        }
        if (model.DialogOpen) model.CloseDialog();
        confirmingExit = true;
        try
        {
            if (model.Sessions.RunningCount > 0 || model.Sessions.Downloads.HasPending)
            {
                Show();
                if (!await model.Confirm(model.T("Выйти из лаунчера?", "Quit launcher?"),
                    model.T("Незавершённые загрузки будут отменены. Запущенный Minecraft останется открыт.", "Pending downloads will be cancelled. Running Minecraft will stay open."), model.T("Выйти", "Quit"))) return;
            }
            quitting = true; model.Sessions.Cancel(); model.Sessions.Downloads.CancelAll(); window.Close();
        }
        finally { confirmingExit = false; }
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == WmImeStartComposition) composing = true;
        if (message.Msg == WmImeEndComposition) composing = false;
        if (message.Msg == WmKeyDown && message.WParam == 27 && model.DialogOpen && !composing)
        {
            model.CloseDialog(); message.Result = 0; return;
        }
        if (message.Msg == WmEndSession && message.WParam != 0)
        {
            quitting = true; model.Sessions.Cancel(); model.Sessions.Downloads.CancelAll(); window.Close();
        }
        if (!quitting && message.Msg == WmSysCommand && ((long)message.WParam & 0xfff0) == ScMinimize && model.MinimizeToTray && trayReady)
        {
            HideToTray(); message.Result = 0; return;
        }
        base.WndProc(ref message);
        if (message.Msg != WmSize || hiding) return;
        if (message.WParam is 0 or 2) restoreMaximized = message.WParam == 2;
        if (message.WParam == 1 && !quitting)
        {
            restoreAfterGame = false;
            if (model.MinimizeToTray && trayReady) HideToTray();
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        window.Opened -= Opened; window.Closing -= Closing;
        window.TrayActivated -= Show; window.TrayContextMenuRequested -= TrayMenu;
        model.Sessions.GameStarted -= GameStarted; model.Sessions.GameExited -= GameExited;
        model.CloseRequested = null; model.ExitRequested = null; model.MinimizeRequested = null; model.MaximizeRequested = null; model.ShowRequested = null; model.AttentionRequested = null;
        if (Handle != 0) ReleaseHandle();
        smallIcon?.Dispose(); largeIcon?.Dispose();
    }

    [DllImport("user32.dll")]
    private static extern nint SendMessageW(nint window, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint window);
}
