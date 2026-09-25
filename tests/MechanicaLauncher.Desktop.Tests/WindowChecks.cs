using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MechanicaLauncher.Core.Instances;
using MechanicaLauncher.Core.Models;
using MechanicaLauncher.Core.Mods;
using MechanicaLauncher.Core.Profiles;
using MechanicaLauncher.Desktop;
using Nitidus.Native;

internal static partial class Program
{
    private static int WindowChecks(bool endSession)
    {
        var settings = new LauncherSettings { Username = "TrayTest", Language = "ru", DiscordRpc = false };
        using var model = new LauncherModel(settings);
        using var view = new LauncherView(model);
        using var window = new ViewWindow(view.View) { Options = new() { MinimumWidth = 840, MinimumHeight = 620, CustomTitleBarHeight = 40, CaptionButtonsWidth = 138 } };
        using var lifetime = new CancellationTokenSource();
        var context = new LauncherSynchronizationContext(window.Dispatcher, lifetime.Token);
        model.Dispatch = action => context.Post(_ => action(), null);
        using var shell = new LauncherWindowController(window, model);
        Exception? failure = null;
        bool finished = false;
        GameInstance? game = null;
        async Task Until(Func<bool> condition)
        {
            var wait = Stopwatch.StartNew();
            while (!condition())
            {
                if (wait.Elapsed.TotalSeconds > 8) throw new TimeoutException("Native window state did not settle.");
                await Task.Delay(15);
            }
        }
        async Task Exercise()
        {
            try
            {
                await Task.Delay(80);
                Check(shell.Handle != 0 && window.State == WindowState.Normal, "native window attaches to Windows integration");
                Check(SendMessage(shell.Handle, 0x7f, 0, 0) != 0 && SendMessage(shell.Handle, 0x7f, 1, 0) != 0, "small and large Windows icons are installed");
                model.BeginDialog("Закрыть диалог", "Проверка Esc", "", null);
                PostMessage(shell.Handle, 0x100, 27, 0);
                await Until(() => !model.DialogOpen);
                Check(window.State == WindowState.Normal, "Escape closes the modal and keeps the launcher open");
                var modalWork = new TaskCompletionSource();
                model.BeginDialog("Сохранение", "Проверка занятого диалога", "Сохранить", () => modalWork.Task);
                model.AcceptDialog(); await Until(() => model.DialogBusy);
                SendMessage(shell.Handle, 0x100, 27, 0);
                Check(model.DialogOpen && model.DialogBusy, "Escape cannot dismiss a running modal action");
                modalWork.SetResult(); await Until(() => !model.DialogOpen);
                model.Close(); await Until(() => window.State == WindowState.Hidden);
                Check(window.IsRunning, "close button hides the actual window without exiting");
                if (endSession)
                {
                    finished = true; PostMessage(shell.Handle, 0x16, 1, 0); return;
                }
                PostMessage(shell.Handle, 0x8015, 0, (1 << 16) | 0x400);
                await Until(() => window.State == WindowState.Normal);
                Check(IsWindowVisible(shell.Handle), "tray activation restores the native window");

                model.Maximize(); await Until(() => window.State == WindowState.Maximized);
                model.Close(); await Until(() => window.State == WindowState.Hidden);
                await model.HandleCommand("SHOW");
                Check(window.State == WindowState.Maximized, "single-instance SHOW preserves maximized state");
                PostMessage(shell.Handle, 0x112, 0xf060, 0);
                await Until(() => window.State == WindowState.Hidden);
                Check(window.IsRunning, "system close used by Alt+F4 hides to tray");
                model.ShowRequested!();

                model.Minimize(); await Until(() => window.State == WindowState.Minimized);
                Check(IsWindowVisible(shell.Handle), "default minimize stays on the taskbar");
                model.ShowRequested!(); Check(window.State == WindowState.Maximized, "taskbar minimize restores maximized state");
                model.MinimizeToTray = true;
                model.Minimize(); await Until(() => window.State == WindowState.Hidden);
                model.ShowRequested!();
                PostMessage(shell.Handle, 0x112, 0xf020, 0);
                await Until(() => window.State == WindowState.Hidden);
                Check(window.IsRunning, "system minimize honors the tray setting");
                model.ShowRequested!();
                ShowWindow(shell.Handle, 6); await Until(() => window.State == WindowState.Hidden);
                model.ShowRequested!();
                Check(window.State == WindowState.Maximized, "Win32 minimize path also restores the previous window state");
                model.MinimizeToTray = false; model.Maximize();
                await Until(() => window.State == WindowState.Normal);
                model.Close(); await Until(() => window.State == WindowState.Hidden); model.ShowRequested!();
                Check(window.State == WindowState.Normal, "restored normal window does not retain an old maximized flag");

                game = WindowGame(model);
                model.CloseOnLaunch = true;
                await model.Sessions.LaunchAsync(game, null, null, _ => Task.FromResult(true));
                await Until(() => window.State == WindowState.Hidden);
                model.Sessions.Stop(game.Id);
                await Until(() => model.Sessions.RunningCount == 0 && window.State == WindowState.Normal);
                Check(window.IsRunning, "automatic game hide returns after game exit");
                model.CloseOnLaunch = false;
                await model.Sessions.LaunchAsync(game, null, null, _ => Task.FromResult(true));
                model.Close(); await Until(() => window.State == WindowState.Hidden);
                model.Sessions.Stop(game.Id); await Until(() => model.Sessions.RunningCount == 0); await Task.Delay(80);
                Check(window.State == WindowState.Hidden, "manual tray hide does not steal focus when the game exits");
                model.ShowRequested!();

                var job = model.Sessions.Enqueue("Tray download", null, token => Task.Delay(Timeout.Infinite, token));
                model.CloseToTray = false;
                model.Close(); await Until(() => window.State == WindowState.Hidden);
                Check(model.Sessions.Downloads.HasPending, "closing during download preserves work even with tray preference off");
                model.ShowChoices("Unfinished selection", [new() { Title = "Choice" }]);
                model.Exit(); await Until(() => model.DialogOpen);
                Check(model.DialogTitle == "Выйти из лаунчера?", "explicit quit replaces an idle picker with the exit confirmation");
                Check(window.State == WindowState.Normal, "explicit quit restores the window for confirmation");
                model.Exit(); Check(model.DialogOpen, "repeated quit does not replace the pending confirmation");
                model.CloseDialog(); await Task.Delay(30);
                Check(window.IsRunning && model.Sessions.Downloads.HasPending, "cancelled quit leaves downloads and window alive");
                model.Sessions.Downloads.CancelAll(); await job.Completion;

                await model.Sessions.LaunchAsync(game, null, null, _ => Task.FromResult(true));
                model.CloseToTray = true;
                model.Exit(); await Until(() => model.DialogOpen);
                finished = true; model.AcceptDialog();
            }
            catch (Exception ex)
            {
                failure = ex; shell.Dispose(); window.Close();
            }
        }
        window.Opened += () =>
        {
            SynchronizationContext.SetSynchronizationContext(context);
            _ = Exercise();
        };
        window.Closed += lifetime.Cancel;
        var watchdog = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(40), lifetime.Token);
                context.Post(_ => { failure = new TimeoutException("Native window test timed out."); shell.Dispose(); window.Close(); }, null);
            }
            catch (OperationCanceledException) { }
        });
        try
        {
            window.Run("Mechanica tray integration test", width: 1000, height: 760);
            if (failure != null) throw failure;
            Check(finished, endSession ? "Windows session end closes a hidden launcher" : "native window reaches explicit quit");
            if (!endSession) Check(game != null && model.Sessions.IsRunning(game.Id), "explicit launcher quit leaves Minecraft running");
            Console.WriteLine($"PASS {checks} Windows integration checks");
            Console.WriteLine(output);
            return 0;
        }
        finally
        {
            lifetime.Cancel();
            if (game != null && model.Sessions.IsRunning(game.Id)) model.Sessions.Stop(game.Id);
            model.Sessions.Downloads.CancelAll();
        }
    }

    private static GameInstance WindowGame(LauncherModel model)
    {
        string stub = Path.Combine(AppContext.BaseDirectory, "stub");
        File.WriteAllText(Path.Combine(stub, "release"), "JAVA_VERSION=\"21\"\n");
        var game = model.Instances.CreateInstance("Window fixture", "window-fixture");
        game.JavaPath = Path.Combine(stub, "bin", "java.exe"); game.JvmArgs = "--fixture-wait";
        model.Instances.SaveInstance(game);
        string version = Path.Combine(model.Instances.SharedDir, "versions", game.McVersion);
        Directory.CreateDirectory(version);
        File.WriteAllText(Path.Combine(version, game.McVersion + ".json"), JsonSerializer.Serialize(new VersionMeta
        {
            Id = game.McVersion, MainClass = "MechanicaFixture", MinecraftArguments = "",
            JavaVersion = new() { MajorVersion = 21, Component = "fixture" }
        }));
        string localVersion = Path.Combine(model.Instances.GetGameDir(game.Id), "versions", game.McVersion);
        Directory.CreateDirectory(localVersion); File.WriteAllBytes(Path.Combine(localVersion, game.McVersion + ".jar"), []);
        return game;
    }

    [DllImport("user32.dll", EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern nint SendMessage(nint window, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint window, int command);
}
