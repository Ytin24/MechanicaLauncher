namespace MechanicaLauncher;

public partial class App : Application
{
    public static Window MainWindow { get; private set; } = null!;

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
