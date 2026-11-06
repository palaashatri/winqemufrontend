using Microsoft.UI.Xaml;
using WinQemu.Core.Lifecycle;

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
        try
        {
            var logPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WinQemu",
                "logs",
                "ui-unhandled.log");
            var details = e.Exception?.ToString() ?? e.Message;
            using var logger = new LogRotator(logPath, maxFileSizeMiB: 2, maxRotatedFiles: 3);
            logger.AppendLine("UI-ERROR", $"Unhandled UI exception: {details}");
            CurrentMainWindow?.ReportUnhandledUiError(e.Exception?.Message ?? e.Message);
        }
        catch
        {
        }

        // Prevent unhandled UI exceptions from terminating the GUI while VMs are running (§58, §76)
        e.Handled = true;
    }
}
