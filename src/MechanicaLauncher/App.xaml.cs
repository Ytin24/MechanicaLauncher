using System.Collections.Concurrent;
using System.Diagnostics;
using MechanicaLauncher.Core.Localization;
using MechanicaLauncher.Core.Profiles;

namespace MechanicaLauncher;

public partial class App : Application
{
    public static Window MainWindow { get; private set; } = null!;
    public static LauncherSettings Settings { get; } = LauncherSettings.Load();
    public static ConcurrentDictionary<string, Process> RunningInstances { get; } = new();
    public static string L(string key) => Locale.Get(key);
    public static string L(string key, object arg) => string.Format(Locale.Get(key), arg);

    public App()
    {
        this.InitializeComponent();
        Locale.Init(Settings.Language);
    }

    protected override void OnLaunched(LaunchActivatedEventArgs e)
    {
        MainWindow = new MainWindow();
        MainWindow.Activate();
    }
}
