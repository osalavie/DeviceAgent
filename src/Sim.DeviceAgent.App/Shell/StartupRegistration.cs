using Microsoft.Win32;

namespace Sim.DeviceAgent.App.Shell;

internal static class StartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "Sim.DeviceAgent";

    internal const string WatchdogMutexName = @"Local\Sim.DeviceAgent.App.Watchdog";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(RunValueName) is string value && !string.IsNullOrWhiteSpace(value);
        }
        catch
        {
            return false;
        }
    }

    public static void Enable(string executablePath, string configPath)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
        key.SetValue(RunValueName, BuildCommand(executablePath, configPath), RegistryValueKind.String);
    }

    public static void Disable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        key?.DeleteValue(RunValueName, throwOnMissingValue: false);
    }

    public static bool IsWatchdogRunning()
    {
        try
        {
            if (!Mutex.TryOpenExisting(WatchdogMutexName, out var mutex))
            {
                return false;
            }

            mutex.Dispose();
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
        catch
        {
            return false;
        }
    }

    internal static string BuildCommand(string executablePath, string configPath)
        => $"\"{executablePath}\" --watchdog --config \"{configPath}\"";
}
