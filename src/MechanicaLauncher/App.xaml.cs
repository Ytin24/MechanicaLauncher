using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using Microsoft.UI.Windowing;
using MechanicaLauncher.Core.Discord;
using MechanicaLauncher.Core.Localization;
using MechanicaLauncher.Core.Profiles;
using MechanicaLauncher.Core.Protocol;
using WinRT.Interop;

namespace MechanicaLauncher;

public partial class App : Application
{
    private static Mutex? _mutex;

    public static Window MainWindow { get; private set; } = null!;
    public static LauncherSettings Settings { get; } = LauncherSettings.Load();
    public static ConcurrentDictionary<string, Process> RunningInstances { get; } = new();
    public static DiscordPresence Discord { get; } = new();
    public static Core.Updates.UpdateInfo? LatestUpdate { get; set; }
    public static ConnectRequest? PendingConnect { get; set; }
    public static bool IsReconnecting { get; set; }
    public static bool IsHidden { get; private set; }

    public static string L(string key) => Locale.Get(key);
    public static string L(string key, object arg) => string.Format(Locale.Get(key), arg);

    public App()
    {
        this.InitializeComponent();
        Locale.Init(Settings.Language);
    }

    protected override void OnLaunched(LaunchActivatedEventArgs e)
    {
        _mutex = new Mutex(true, "MechanicaLauncher_SingleInstance", out var isNew);
        if (!isNew)
        {
            var args = Environment.GetCommandLineArgs();
            var connect = ProtocolHandler.ParseArgs(args);
            var pendingFile = GetPendingFile();
            if (connect != null)
                File.WriteAllText(pendingFile, $"{connect.Server}|{connect.Port}|{connect.Version}");
            else
                File.WriteAllText(pendingFile, "SHOW");
            Environment.Exit(0);
            return;
        }

        ProtocolHandler.Register();
        if (Settings.DiscordRpc) Discord.Init();

        var cmdArgs = Environment.GetCommandLineArgs();
        PendingConnect = ProtocolHandler.ParseArgs(cmdArgs);

        MainWindow = new MainWindow();
        MainWindow.Activate();

        _ = CheckUpdatesAsync();
        _ = PollPendingAsync();
    }

    public static void HideWindow()
    {
        try
        {
            var appWindow = GetAppWindow();
            appWindow?.Hide();
            IsHidden = true;
        }
        catch { }
    }

    public static void ShowWindow()
    {
        try
        {
            var appWindow = GetAppWindow();
            appWindow?.Show();
            IsHidden = false;
        }
        catch { }
    }

    public static bool HasRunningInstances() =>
        RunningInstances.Any(kv => !kv.Value.HasExited);

    private static AppWindow? GetAppWindow()
    {
        var hwnd = WindowNative.GetWindowHandle(MainWindow);
        var id = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
        return AppWindow.GetFromWindowId(id);
    }

    private static string GetPendingFile() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MechanicaLauncher", "pending_connect.txt");

    private static async Task CheckUpdatesAsync()
    {
        try { LatestUpdate = await Core.Updates.UpdateChecker.CheckAsync(); }
        catch { }
    }

    private static async Task PollPendingAsync()
    {
        var pendingFile = GetPendingFile();
        while (true)
        {
            await Task.Delay(1000);
            try
            {
                if (!File.Exists(pendingFile)) continue;
                var content = await File.ReadAllTextAsync(pendingFile);
                File.Delete(pendingFile);

                if (content.Trim() == "SHOW")
                {
                    ShowWindow();
                    continue;
                }

                var parts = content.Split('|');
                if (parts.Length >= 3)
                {
                    var request = new ConnectRequest(
                        parts[0],
                        int.TryParse(parts[1], out var p) ? p : 25565,
                        parts[2]);

                    if (HasRunningInstances())
                    {
                        MainWindow.DispatcherQueue.TryEnqueue(() =>
                            OverlayPopup.Show(request));
                    }
                    else
                    {
                        PendingConnect = request;
                        ShowWindow();
                        if (MainWindow is MainWindow mw)
                            mw.DispatcherQueue.TryEnqueue(() => mw.HandlePendingConnect());
                    }
                }
            }
            catch { }
        }
    }
}
