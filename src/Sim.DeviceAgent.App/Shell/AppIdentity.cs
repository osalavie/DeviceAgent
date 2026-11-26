namespace Sim.DeviceAgent.App.Shell;

internal static class AppIdentity
{
    public const string InstanceName = "Sim.DeviceAgent.App";

    public static bool IsAgentRunning()
    {
        try
        {
            if (Mutex.TryOpenExisting(@"Local\" + InstanceName, out var mutex))
            {
                mutex.Dispose();
                return true;
            }
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
        catch
        {
            // Treat an unreadable mutex as "unknown" and let the watchdog retry next tick.
        }

        return false;
    }
}
