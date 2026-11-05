using Microsoft.UI.Xaml;

namespace WinQemu.App;

public partial class App : Application
{
    public static MainWindow? CurrentMainWindow { get; private set; }
    public static string[] LaunchArguments { get; private set; } = [];

    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        LaunchArguments = Environment.GetCommandLineArgs().Skip(1).ToArray();
        CurrentMainWindow = new MainWindow();
        CurrentMainWindow.Activate();
    }

    private static void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        // Prevent raw unhandled exceptions from crashing the GUI while VMs are running (§58, §76)
        e.Handled = true;
    }
}
