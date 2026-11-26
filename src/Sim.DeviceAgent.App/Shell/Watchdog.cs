using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Sim.DeviceAgent.App.Logging;
using Sim.DeviceAgent.Configuration;

namespace Sim.DeviceAgent.App.Shell;

internal static class Watchdog
{
    internal const string StopEventName = @"Local\Sim.DeviceAgent.App.Stop";

    private const string WatchdogArgument = "--watchdog";
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan CleanExitGrace = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan StableRunThreshold = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan InitialRestartDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxRestartDelay = TimeSpan.FromSeconds(60);

    public static bool IsRequested(string[] args)
        => args.Any(arg => string.Equals(arg, WatchdogArgument, StringComparison.OrdinalIgnoreCase));

    public static void Run(string[] args)
    {
        try
        {
            RunCore(args);
        }
        catch
        {
            // The guard is best effort; crashing here would only add noise.
        }
    }

    private static void RunCore(string[] args)
    {
        using var mutex = new Mutex(initiallyOwned: true, StartupRegistration.WatchdogMutexName, out var isPrimary);
        if (!isPrimary)
        {
            return;
        }

        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return;
        }

        var configPath = AgentConfigLoader.ResolveConfigPath(args);
        using var logProvider = TryCreateLogProvider(configPath);
        var logger = logProvider?.CreateLogger("Sim.DeviceAgent.Watchdog");
        logger?.LogInformation("Watchdog started (config: {ConfigPath}).", configPath);
        using var stopEvent = new EventWaitHandle(false, EventResetMode.ManualReset, StopEventName);
        var restartDelay = InitialRestartDelay;
        DateTime? lastStartUtc = null;

        while (!stopEvent.WaitOne(TimeSpan.Zero))
        {
            if (AppIdentity.IsAgentRunning())
            {
                restartDelay = InitialRestartDelay;
                if (stopEvent.WaitOne(PollInterval))
                {
                    return;
                }

                continue;
            }

            // A tray exit signals the event; give it a moment before treating the
            // disappearance as a crash.
            if (stopEvent.WaitOne(CleanExitGrace))
            {
                return;
            }

            if (lastStartUtc is { } startedAt &&
                DateTime.UtcNow - startedAt < StableRunThreshold)
            {
                if (stopEvent.WaitOne(restartDelay))
                {
                    return;
                }

                restartDelay = TimeSpan.FromSeconds(
                    Math.Min(restartDelay.TotalSeconds * 2, MaxRestartDelay.TotalSeconds));
            }
            else
            {
                restartDelay = InitialRestartDelay;
            }

            try
            {
                logger?.LogInformation("Agent process is not running; starting it.");
                StartAgent(executablePath, configPath);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Could not start the agent process.");
                restartDelay = TimeSpan.FromSeconds(
                    Math.Min(restartDelay.TotalSeconds * 2, MaxRestartDelay.TotalSeconds));
            }

            lastStartUtc = DateTime.UtcNow;

            if (stopEvent.WaitOne(PollInterval))
            {
                return;
            }
        }
    }

    private static FileLoggerProvider? TryCreateLogProvider(string configPath)
    {
        try
        {
            var config = AgentConfigLoader.Load(configPath);
            var directory = string.IsNullOrWhiteSpace(config.Agent.StateDirectory)
                ? "data"
                : config.Agent.StateDirectory;
            if (!Path.IsPathRooted(directory))
            {
                directory = Path.Combine(AgentConfigLoader.ProgramDirectory, directory);
            }

            return new FileLoggerProvider(Path.Combine(directory, "logs"));
        }
        catch
        {
            return null;
        }
    }

    private static void StartAgent(string executablePath, string configPath)
    {
        var startInfo = new ProcessStartInfo(executablePath)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(executablePath)
        };
        startInfo.ArgumentList.Add("--autostart");
        startInfo.ArgumentList.Add("--config");
        startInfo.ArgumentList.Add(configPath);
        using var process = Process.Start(startInfo);
    }
}
