using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Sim.DeviceAgent.App.Logging;
using Sim.DeviceAgent.App.Shell;
using Sim.DeviceAgent.App.Tray;
using Sim.DeviceAgent.Configuration;
using Sim.DeviceAgent.Hosting;

namespace Sim.DeviceAgent.App;

public partial class App : Application
{
    private MainWindow? _window;
    private AgentRuntimeHost? _host;
    private TrayIcon? _tray;
    private SingleInstance? _singleInstance;
    private StartupGuardService? _startupGuard;
    private Microsoft.UI.Dispatching.DispatcherQueue _dispatcherQueue = null!;
    private string _configPath = string.Empty;
    private bool _exiting;
    private FileLoggerProvider? _fileLog;
    private ILogger? _appLogger;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _dispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        var commandLine = Environment.GetCommandLineArgs().Skip(1).ToArray();
        var autostart = commandLine.Any(
            arg => string.Equals(arg, "--autostart", StringComparison.OrdinalIgnoreCase));

        _singleInstance = new SingleInstance(AppIdentity.InstanceName);
        if (!_singleInstance.IsPrimary)
        {
            _singleInstance.SignalShow();
            Exit();
            return;
        }

        _singleInstance.StartServer(() => _dispatcherQueue.TryEnqueue(ShowMainWindow));

        _configPath = AgentConfigLoader.ResolveConfigPath(commandLine);
        var createdDefault = EnsureConfigExists(out var createConfigError);
        var config = LoadConfig(out var configError);

        if (createConfigError is not null)
        {
            configError = configError is null
                ? createConfigError
                : $"{createConfigError}{Environment.NewLine}{configError}";
        }

        _fileLog = new FileLoggerProvider(ResolveLogDirectory(config?.Agent.StateDirectory));
        _appLogger = _fileLog.CreateLogger("Sim.DeviceAgent.App");

        try
        {
            _startupGuard = new StartupGuardService(
                _configPath,
                _fileLog.CreateLogger("Sim.DeviceAgent.App.StartupGuard"));
            _startupGuard.Start();
        }
        catch (Exception ex)
        {
            var guardError = $"启动守护初始化失败：{ex.Message}";
            configError = configError is null ? guardError : $"{configError}{Environment.NewLine}{guardError}";
            _appLogger.LogWarning(ex, "Startup guard initialization failed.");
        }

        if (configError is not null)
        {
            _appLogger.LogError("Configuration error: {Error}", configError);
        }

        if (config is not null)
        {
            try
            {
                _host = AgentRuntimeHost.Create(
                    config,
                    _configPath,
                    logging => logging.AddDebug().AddProvider(_fileLog));
                _appLogger.LogInformation(
                    "Application starting (agent {AgentId}, config {ConfigPath}).",
                    config.Agent.Id,
                    _configPath);
            }
            catch (Exception ex)
            {
                configError = ex.Message;
                _appLogger.LogError(ex, "Failed to create the agent runtime.");
            }
        }

        var startupNotice = createdDefault
            ? $"未找到配置文件，已自动创建默认配置：{_configPath}"
            : null;
        _window = new MainWindow(_host, _configPath, configError, startupNotice, _startupGuard);

        var trayEnabled = config?.Agent.Tray.Enabled ?? true;
        if (trayEnabled)
        {
            try
            {
                _tray = new TrayIcon(
                    config?.Agent.Id ?? "Device Agent",
                    onShow: ShowMainWindow,
                    onOpenConfig: OpenConfigFile,
                    onReload: () => _ = ReloadAsync(),
                    onExit: () => _ = ExitAsync());
                _tray.Initialize();
            }
            catch (Exception ex)
            {
                _appLogger.LogWarning(ex, "Tray initialization failed; the agent keeps running.");
                _tray = null;
            }
        }

        if (!autostart || !trayEnabled || _tray is null)
        {
            ShowMainWindow();
        }

        if (_host is not null)
        {
            _ = StartHostAsync();
        }
    }

    private bool EnsureConfigExists(out string? error)
    {
        error = null;
        if (File.Exists(_configPath))
        {
            return false;
        }

        try
        {
            var fullPath = Path.GetFullPath(_configPath);
            var baseDirectory = Path.GetDirectoryName(fullPath);
            if (string.IsNullOrWhiteSpace(baseDirectory))
            {
                baseDirectory = AgentConfigLoader.ProgramDirectory;
            }

            var defaults = AgentConfigDefaults.Create(Environment.MachineName, baseDirectory);
            AgentConfigWriter.Save(fullPath, defaults);
            return true;
        }
        catch (Exception ex)
        {
            error = $"默认配置文件创建失败：{ex.Message}";
            return false;
        }
    }

    private AgentConfig? LoadConfig(out string? error)
    {
        error = null;
        try
        {
            var config = AgentConfigLoader.Load(_configPath);
            var errors = AgentConfigValidator.Validate(config);
            if (errors.Count > 0)
            {
                error = string.Join(Environment.NewLine, errors);
                return null;
            }

            return config;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    private async Task StartHostAsync()
    {
        try
        {
            await _host!.StartAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _window?.ShowError($"运行时启动失败：{ex.Message}");
        }
    }

    private void ShowMainWindow()
    {
        _window?.ShowFromTray();
    }

    private void OpenConfigFile()
    {
        try
        {
            Process.Start(new ProcessStartInfo(_configPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _tray?.ShowBalloon("Device Agent", $"无法打开配置文件：{ex.Message}");
        }
    }

    private async Task ReloadAsync()
    {
        if (_host is null)
        {
            _tray?.ShowBalloon("Device Agent", "当前没有可用的运行配置。");
            return;
        }

        try
        {
            var result = await _host.Reloader.ReloadAsync(CancellationToken.None, force: true);
            var message = result.Success ? "配置已重新加载。" : $"配置重载失败：{result.Message}";
            _dispatcherQueue.TryEnqueue(() => _tray?.ShowBalloon("Device Agent", message));
        }
        catch (Exception ex)
        {
            _dispatcherQueue.TryEnqueue(() => _tray?.ShowBalloon("Device Agent", $"配置重载失败：{ex.Message}"));
        }
    }

    private async Task ExitAsync()
    {
        if (_exiting)
        {
            return;
        }

        _exiting = true;
        _appLogger?.LogInformation("Exit requested from the tray; closing the window immediately.");
        _startupGuard?.Stop();
        _startupGuard = null;
        _tray?.Dispose();
        _tray = null;

        // 托盘退出时主窗口立即关闭，运行时最多再收尾 1 秒。
        _window?.AllowClose();
        _window?.Close();

        if (_host is not null)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                await _host.StopAsync(timeout.Token);
            }
            catch (Exception ex)
            {
                // The process is exiting; spool replay recovers unacknowledged data on restart.
                _appLogger?.LogWarning(ex, "Runtime shutdown did not finish; exiting anyway.");
            }

            // 不再等待 DisposeAsync：进程即将退出，未确认数据由 spool 在下次启动重放。
            _host = null;
        }

        _appLogger?.LogInformation("Agent exited.");
        _fileLog?.Dispose();
        _fileLog = null;
        _singleInstance?.Dispose();
        Exit();
    }

    private static string ResolveLogDirectory(string? stateDirectory)
    {
        var directory = string.IsNullOrWhiteSpace(stateDirectory) ? "data" : stateDirectory;
        if (!Path.IsPathRooted(directory))
        {
            directory = Path.Combine(AgentConfigLoader.ProgramDirectory, directory);
        }

        return Path.Combine(directory, "logs");
    }
}
