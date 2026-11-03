using Microsoft.UI.Xaml;
using SafeUpdater.Core;

namespace SafeUpdater;

public partial class App : Application
{
    private MainWindow? _window;

    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            RunLog.WriteError(e.Exception.ToString());
            e.SetObserved();
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        // Log it, tell the user, keep the window.
        RunLog.WriteError(e.Exception.ToString());
        if (_window is not null)
        {
            e.Handled = true;
            _window.ShowError("Something went wrong", e.Exception.Message + " Details are in error.log in the log folder.");
        }
    }
}
