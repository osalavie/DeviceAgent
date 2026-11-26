using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Sim.DeviceAgent.App.Shell;

namespace Sim.DeviceAgent.App;

public static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (Watchdog.IsRequested(args))
        {
            Watchdog.Run(args);
            return;
        }

        global::WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(p =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });
    }
}
