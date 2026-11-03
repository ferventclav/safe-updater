using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using SafeUpdater.Core;

namespace SafeUpdater;

public static class Program
{
    [DllImport("Microsoft.ui.xaml.dll")]
    private static extern void XamlCheckProcessRequirements();

    [STAThread]
    private static int Main(string[] args)
    {
        // The elevated helper: no window, no XAML, just the job file.
        if (args.Length == 2 && args[0] == "--worker")
        {
            return Worker.Run(args[1]);
        }

        XamlCheckProcessRequirements();
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(p =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });
        return 0;
    }
}
