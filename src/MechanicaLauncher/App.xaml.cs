using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.UI.Windowing;
using MechanicaLauncher.Core.Discord;
using MechanicaLauncher.Core.Localization;
using MechanicaLauncher.Core.Profiles;
using MechanicaLauncher.Core.IO;
using MechanicaLauncher.Core.Protocol;
using WinRT.Interop;

namespace MechanicaLauncher;

public partial class App : Application
{
    private static Mutex? _mutex;

    public static Window MainWindow { get; private set; } = null!;
    public static LauncherSettings Settings { get; } = LauncherSettings.Load();
    public static ConcurrentDictionary<string, Process> RunningInstances { get; } = new();
    public static SemaphoreSlim LaunchPreparationGate { get; } = new(1, 1);
    public static DownloadQueue Downloads { get; } = new();
    public static CancellationTokenSource? PreparationCancellation { get; set; }
    public static string? PreparingInstanceId { get; set; }
    public static bool IsInstanceBusy(string id) => PreparingInstanceId == id ||
        (RunningInstances.TryGetValue(id, out var process) && !process.HasExited);
    public static event Action? RunningInstancesChanged;
    public static void NotifyRunningChanged() => RunningInstancesChanged?.Invoke();
    public static DiscordPresence Discord { get; } = new();
    public static Core.Updates.UpdateInfo? LatestUpdate { get; set; }
    public static ConnectRequest? PendingConnect { get; set; }
    public static Core.Protocol.EventRequest? PendingEvent { get; set; }
    public static string? PendingMrpack { get; set; }
    public static Core.Config.EventConfigManager EventManager { get; } = new();
    public static Core.Config.EventConfig? EventConfig => EventManager.Active;
    public static bool IsEventMode => EventManager.IsEventMode;
    public static bool IsReconnecting { get; set; }
    public static bool IsHidden { get; private set; }

    public static string L(string key) => Locale.Get(key);
    public static string L(string key, object arg) => string.Format(Locale.Get(key), arg);

    public App()
    {
        UnhandledException += (_, args) => WriteCrashLog(args.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, args) => WriteCrashLog(args.ExceptionObject as Exception);
        this.InitializeComponent();
        Locale.SystemLanguagesProvider = () => Windows.System.UserProfile.GlobalizationPreferences.Languages;
        Locale.Init(Settings.Language);
    }

    private static void WriteCrashLog(Exception? exception)
    {
        try
        {
            var directory = Path.Combine(LauncherPaths.DataDirectory, "logs");
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, "launcher-errors.log"), $"{DateTimeOffset.Now:O}\n{exception}\n");
        }
        catch (Exception ex) { Debug.WriteLine(ex); }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs e)
    {
        var mutexName = "MechanicaLauncher_SingleInstance";
        if (LauncherPaths.HasCustomDataDirectory)
            mutexName += "_" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(LauncherPaths.DataDirectory.ToUpperInvariant())))[..16];
        _mutex = new Mutex(true, mutexName, out var isNew);
        if (!isNew)
        {
            var args = Environment.GetCommandLineArgs();
            var connect = ProtocolHandler.ParseConnect(args);
            var eventReq = ProtocolHandler.ParseEvent(args);
            var mrpack = ParseMrpack(args);
            var pendingFile = GetPendingFile();
            if (mrpack != null)
                File.WriteAllText(pendingFile, $"MRPACK|{mrpack}");
            else if (eventReq != null)
                File.WriteAllText(pendingFile, $"EVENT|{eventReq.ConfigUrl}");
            else if (connect != null)
                File.WriteAllText(pendingFile, $"{connect.Server}|{connect.Port}|{connect.Version}");
            else
                File.WriteAllText(pendingFile, "SHOW");
            Environment.Exit(0);
            return;
        }

        if (!LauncherPaths.HasCustomDataDirectory) ProtocolHandler.Register();
        Discord.Configure(Settings, Locale.CurrentLanguage);

        var cmdArgs = Environment.GetCommandLineArgs();
        PendingConnect = ProtocolHandler.ParseConnect(cmdArgs);
        PendingEvent = ProtocolHandler.ParseEvent(cmdArgs);
        PendingMrpack = ParseMrpack(cmdArgs);

        // Restore cached event config
        if (PendingEvent == null && !string.IsNullOrEmpty(Settings.ActiveEventUrl))
        {
            _ = LoadEventAsync(Settings.ActiveEventUrl);
        }

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
        // May be called from the game-exit watcher thread; AppWindow operations require UI thread.
        var dq = MainWindow?.DispatcherQueue;
        if (dq == null || dq.HasThreadAccess) { DoShow(); return; }
        dq.TryEnqueue(DoShow);
    }

    private static void DoShow()
    {
        try
        {
            var hwnd = WindowNative.GetWindowHandle(MainWindow);
            var appWindow = GetAppWindow();
            appWindow?.Show();
            NativeShowWindow(hwnd, SW_RESTORE);
            SetForegroundWindow(hwnd);
            (MainWindow as MainWindow)?.Activate();
            IsHidden = false;
        }
        catch { }
    }

    private const int SW_RESTORE = 9;

    [DllImport("user32.dll", EntryPoint = "ShowWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool NativeShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    public static bool HasRunningInstances() =>
        RunningInstances.Any(kv => !kv.Value.HasExited);

    public static async Task LoadEventAsync(string url)
    {
        try
        {
            await EventManager.LoadFromUrlAsync(url);
            Settings.ActiveEventUrl = url;
            Settings.Save();
            ApplyEventBranding();
        }
        catch { }
    }

    public static void ClearEvent()
    {
        EventManager.Clear();
        Settings.ActiveEventUrl = null;
        Settings.Save();
        ResetBranding();
    }

    public static void ApplyEventBranding()
    {
        var branding = EventConfig?.Branding;
        if (branding == null) return;

        try
        {
            if (!string.IsNullOrEmpty(branding.AccentColor))
            {
                var color = ParseColor(branding.AccentColor);
                Current.Resources["AccentBrush"] = new Microsoft.UI.Xaml.Media.SolidColorBrush(color);
            }
            if (!string.IsNullOrEmpty(branding.AccentColorHover))
            {
                var color = ParseColor(branding.AccentColorHover);
                Current.Resources["AccentHoverBrush"] = new Microsoft.UI.Xaml.Media.SolidColorBrush(color);
            }
        }
        catch { }
    }

    public static void ResetBranding()
    {
        try
        {
            Current.Resources["AccentBrush"] = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                Windows.UI.Color.FromArgb(0xFF, 0x4C, 0xAF, 0x50));
            Current.Resources["AccentHoverBrush"] = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                Windows.UI.Color.FromArgb(0xFF, 0x81, 0xC7, 0x84));
        }
        catch { }
    }

    private static Windows.UI.Color ParseColor(string hex)
    {
        hex = hex.TrimStart('#');
        if (hex.Length == 6)
            return Windows.UI.Color.FromArgb(0xFF,
                byte.Parse(hex[..2], System.Globalization.NumberStyles.HexNumber),
                byte.Parse(hex[2..4], System.Globalization.NumberStyles.HexNumber),
                byte.Parse(hex[4..6], System.Globalization.NumberStyles.HexNumber));
        return Windows.UI.Color.FromArgb(0xFF, 0x4C, 0xAF, 0x50);
    }

    private static AppWindow? GetAppWindow()
    {
        var hwnd = WindowNative.GetWindowHandle(MainWindow);
        var id = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
        return AppWindow.GetFromWindowId(id);
    }

    private static string GetPendingFile()
    {
        Directory.CreateDirectory(LauncherPaths.DataDirectory);
        return Path.Combine(LauncherPaths.DataDirectory, "pending_connect.txt");
    }

    // Windows passes the double-clicked file as a plain positional argument; accept only local .mrpack paths that exist.
    private static string? ParseMrpack(string[] args)
    {
        for (int i = 1; i < args.Length; i++)
        {
            var a = args[i];
            if (a.EndsWith(".mrpack", StringComparison.OrdinalIgnoreCase) && File.Exists(a))
                return Path.GetFullPath(a);
        }
        return null;
    }

    private static async Task CheckUpdatesAsync()
    {
        try { LatestUpdate = await Core.Updates.UpdateChecker.CheckAsync(); }
        catch (Exception ex) { Debug.WriteLine($"Update check failed: {ex.Message}"); }
    }

    private static async Task PollPendingAsync()
    {
        var pendingFile = GetPendingFile();
        while (true)
        {
            await Task.Delay(1000);
            try
            {
                string content;
                try
                {
                    content = await File.ReadAllTextAsync(pendingFile);
                    File.Delete(pendingFile);
                }
                catch (FileNotFoundException) { continue; }
                catch (DirectoryNotFoundException) { continue; }

                if (content.Trim() == "SHOW")
                {
                    ShowWindow();
                    continue;
                }

                if (content.StartsWith("MRPACK|"))
                {
                    var path = content["MRPACK|".Length..].Trim();
                    ShowWindow();
                    MainWindow?.DispatcherQueue?.TryEnqueue(async () =>
                    {
                        if (MainWindow is MainWindow mw)
                            await mw.ImportMrpackAsync(path);
                    });
                    continue;
                }

                if (content.StartsWith("EVENT|"))
                {
                    var url = content["EVENT|".Length..].Trim();
                    ShowWindow();
                    MainWindow?.DispatcherQueue?.TryEnqueue(async () =>
                    {
                        await LoadEventAsync(url);
                        if (MainWindow is MainWindow mw)
                        {
                            mw.ApplyEventNavigation();
                            await mw.HandlePendingEventPublicAsync();
                        }
                    });
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
                        MainWindow?.DispatcherQueue?.TryEnqueue(() =>
                            OverlayPopup.Show(request));
                    }
                    else
                    {
                        PendingConnect = request;
                        ShowWindow();
                        if (MainWindow is MainWindow mw)
                            mw.DispatcherQueue?.TryEnqueue(() => mw.HandlePendingConnect());
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"PollPendingAsync error: {ex.Message}");
            }
        }
    }
}
