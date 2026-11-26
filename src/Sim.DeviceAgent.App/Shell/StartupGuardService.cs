using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Sim.DeviceAgent.App.Shell;

internal sealed class StartupGuardService : IDisposable
{
    private static readonly TimeSpan WatchdogCheckInterval = TimeSpan.FromSeconds(5);

    private readonly string _executablePath;
    private readonly string _configPath;
    private readonly string _settingsPath;
    private readonly EventWaitHandle _stopEvent;
    private readonly Timer _watchdogTimer;
    private readonly ILogger? _logger;
    private readonly object _gate = new();
    private AppUiSettings _settings;
    private bool _disposed;

    public StartupGuardService(string configPath, ILogger? logger = null)
    {
        _executablePath = Environment.ProcessPath
                          ?? throw new InvalidOperationException("无法解析当前可执行文件路径。");
        _configPath = Path.GetFullPath(configPath);
        _settingsPath = AppUiSettingsStore.ResolvePath(_configPath);
        _logger = logger;
        _settings = AppUiSettingsStore.Load(_settingsPath);
        _stopEvent = new EventWaitHandle(false, EventResetMode.ManualReset, Watchdog.StopEventName);
        _watchdogTimer = new Timer(_ => EnsureWatchdog(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public bool StartWithWindows => _settings.StartWithWindows;

    public void Start()
    {
        _stopEvent.Reset();

        try
        {
            ApplyRegistration(_settings.StartWithWindows);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Startup registration failed.");
            Debug.WriteLine($"Startup registration failed: {ex.Message}");
        }

        EnsureWatchdog();
        _watchdogTimer.Change(WatchdogCheckInterval, WatchdogCheckInterval);
    }

    public void SetStartWithWindows(bool enabled)
    {
        var settings = new AppUiSettings { StartWithWindows = enabled };
        AppUiSettingsStore.Save(_settingsPath, settings);
        _settings = settings;
        ApplyRegistration(enabled);

        if (enabled)
        {
            EnsureWatchdog();
        }
    }

    private void ApplyRegistration(bool enabled)
    {
        if (enabled)
        {
            StartupRegistration.Enable(_executablePath, _configPath);
        }
        else
        {
            StartupRegistration.Disable();
        }
    }

    private void EnsureWatchdog()
    {
        lock (_gate)
        {
            if (_disposed || StartupRegistration.IsWatchdogRunning())
            {
                return;
            }

            try
            {
                var startInfo = new ProcessStartInfo(_executablePath)
                {
                    UseShellExecute = false,
                    WorkingDirectory = Path.GetDirectoryName(_executablePath)
                };
                startInfo.ArgumentList.Add("--watchdog");
                startInfo.ArgumentList.Add("--config");
                startInfo.ArgumentList.Add(_configPath);
                using var process = Process.Start(startInfo);
            }
            catch (Exception ex)
            {
                // The timer retries; a missing guard must not stop the agent itself.
                _logger?.LogWarning(ex, "Could not start the watchdog process.");
                Debug.WriteLine($"Watchdog start failed: {ex.Message}");
            }
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _watchdogTimer.Dispose();
        }

        try
        {
            _stopEvent.Set();
        }
        catch (ObjectDisposedException)
        {
        }

        _stopEvent.Dispose();
    }

    public void Dispose() => Stop();
}
