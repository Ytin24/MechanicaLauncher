using System.Security.Cryptography;
using System.Text;
using Nitidus.Native;
using MechanicaLauncher.Core.Protocol;

namespace MechanicaLauncher.Desktop;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.FirstOrDefault() == TLauncherCleanupProcess.Command)
        {
            if (args.Length != 3) return 2;
            try { return TLauncherCleanupProcess.RunHelperAsync(args[1], args[2]).GetAwaiter().GetResult(); }
            catch { return 1; }
        }
        try
        {
            string mutexName = "MechanicaLauncher_SingleInstance";
            if (LauncherPaths.HasCustomDataDirectory)
                mutexName += "_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(LauncherPaths.DataDirectory.ToUpperInvariant())))[..16];
            using var mutex = new Mutex(true, mutexName, out bool primary);
            string pendingFile = Path.Combine(LauncherPaths.DataDirectory, "pending_connect.txt");
            Directory.CreateDirectory(LauncherPaths.DataDirectory);
            if (!primary)
            {
                File.WriteAllText(pendingFile, PendingCommand(args));
                return 0;
            }
            if (!LauncherPaths.HasCustomDataDirectory) ProtocolHandler.Register();
            var settings = LauncherSettings.Load();
            using var model = new LauncherModel(settings);
            model.PrepareStartupCommand(PendingCommand(args));
            using var presentation = new LauncherView(model);
            using var window = new ViewWindow(presentation.View)
            {
                Options = new()
                {
                    MinimumWidth = 840, MinimumHeight = 620, CustomTitleBarHeight = 40,
                    CaptionButtonsWidth = 138, DarkTitleBar = settings.Theme != "Light"
                }
            };
            using var lifetime = new CancellationTokenSource();
            var uiContext = new LauncherSynchronizationContext(window.Dispatcher, lifetime.Token);
            int uiThread = Environment.CurrentManagedThreadId;
            model.Dispatch = action =>
            {
                if (lifetime.IsCancellationRequested) return;
                if (Environment.CurrentManagedThreadId == uiThread) action();
                else uiContext.Post(_ => action(), null);
            };
            using var shell = new LauncherWindowController(window, model);
            window.Opened += () =>
            {
                SynchronizationContext.SetSynchronizationContext(uiContext);
                model.Sessions.Discord.Configure(settings, Locale.CurrentLanguage);
                model.Run(async () =>
                {
                    await model.ScanTLauncherAsync();
                    await model.ResumePendingCommand();
                });
                _ = Poll(model, pendingFile, lifetime.Token);
            };
            window.Closed += lifetime.Cancel;
            window.Run("Mechanica Launcher", width: 1180, height: 800);
            return 0;
        }
        catch (Exception error)
        {
            Directory.CreateDirectory(Path.Combine(LauncherPaths.DataDirectory, "logs"));
            File.AppendAllText(Path.Combine(LauncherPaths.DataDirectory, "logs", "launcher-errors.log"), $"{DateTimeOffset.Now:O}\n{error}\n");
            return 1;
        }
    }
    private static string PendingCommand(string[] args)
    {
        var commandLine = new[] { Environment.ProcessPath ?? "MechanicaLauncher.exe" }.Concat(args).ToArray();
        var file = args.FirstOrDefault(a => a.EndsWith(".mrpack", StringComparison.OrdinalIgnoreCase) && File.Exists(a));
        if (file != null) return "MRPACK|" + Path.GetFullPath(file);
        var evt = ProtocolHandler.ParseEvent(commandLine);
        if (evt != null) return "EVENT|" + evt.ConfigUrl;
        var connect = ProtocolHandler.ParseConnect(commandLine);
        return connect == null ? "SHOW" : $"{connect.Server}|{connect.Port}|{connect.Version}";
    }
    private static async Task Poll(LauncherModel model, string pendingFile, CancellationToken token)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            while (await timer.WaitForNextTickAsync(token))
            {
                model.Tick();
                try { await model.ResumePendingCommand(); }
                catch (Exception ex) when (!token.IsCancellationRequested) { model.Notice(ex.Message, true); }
                string text;
                try { text = await File.ReadAllTextAsync(pendingFile, token); File.Delete(pendingFile); }
                catch (FileNotFoundException) { continue; }
                catch (IOException) { continue; }
                try { await HandleCommand(text, model); }
                catch (Exception ex) when (!token.IsCancellationRequested) { model.Notice(ex.Message, true); }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { model.Notice(ex.Message, true); }
    }
    private static Task HandleCommand(string command, LauncherModel model) => model.HandleCommand(command);
}

internal sealed class LauncherSynchronizationContext(WindowDispatcher dispatcher, CancellationToken lifetime) : SynchronizationContext
{
    public override void Post(SendOrPostCallback callback, object? state)
    {
        if (lifetime.IsCancellationRequested || !dispatcher.IsRunning) return;
        try { dispatcher.Post(() => { if (!lifetime.IsCancellationRequested) callback(state); }); }
        catch (InvalidOperationException) when (lifetime.IsCancellationRequested || !dispatcher.IsRunning) { }
    }
    public override void Send(SendOrPostCallback callback, object? state)
    {
        if (lifetime.IsCancellationRequested || !dispatcher.IsRunning) return;
        dispatcher.Send(callback, state);
    }
    public override SynchronizationContext CreateCopy() => this;
}
