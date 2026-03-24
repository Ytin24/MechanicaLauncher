using System.Collections.Concurrent;
using System.Diagnostics;

namespace MechanicaLauncher;

public partial class App : Application
{
    public static Window MainWindow { get; private set; } = null!;
    public static ConcurrentDictionary<string, Process> RunningInstances { get; } = new();

    public App()
    {
        this.InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs e)
    {
        MainWindow = new MainWindow();
        MainWindow.Activate();
    }
}
