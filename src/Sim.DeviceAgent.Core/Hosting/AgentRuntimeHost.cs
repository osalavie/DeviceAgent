using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sim.DeviceAgent.Configuration;
using Sim.DeviceAgent.Runtime;

namespace Sim.DeviceAgent.Hosting;

public sealed record AgentStatusSnapshot(
    string AgentId,
    string DisplayName,
    string ConfigPath,
    IReadOnlyList<DeviceStatus> Devices,
    IReadOnlyList<SinkStatus> Sinks,
    ConfigReloadStatus Config);

public sealed class AgentRuntimeHost : IAsyncDisposable
{
    private readonly IHost _host;
    private int _started;
    private int _disposed;

    private AgentRuntimeHost(IHost host, AgentConfig config, AgentConfigFile configFile)
    {
        _host = host;
        Config = config;
        ConfigFile = configFile;
    }

    public AgentConfig Config { get; }

    public AgentConfigFile ConfigFile { get; }

    public AgentConfigReloader Reloader => _host.Services.GetRequiredService<AgentConfigReloader>();

    public static AgentRuntimeHost Create(
        AgentConfig config,
        string configPath,
        Action<ILoggingBuilder>? configureLogging = null)
    {
        var configFile = new AgentConfigFile(configPath);

        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            ContentRootPath = AgentConfigLoader.ProgramDirectory
        });

        builder.Logging.ClearProviders();
        if (configureLogging is not null)
        {
            configureLogging(builder.Logging);
        }
        else
        {
            builder.Logging.AddSimpleConsole(options =>
            {
                options.TimestampFormat = "yyyy-MM-dd HH:mm:ss.fff ";
                options.SingleLine = true;
            });
        }

        builder.Services.AddSingleton(config);
        builder.Services.AddSingleton(configFile);
        builder.Services.AddSingleton<AgentDeviceRegistry>();
        builder.Services.AddSingleton<AgentSinkRegistry>();
        builder.Services.AddSingleton<ISequenceStore>(services => new FileSequenceStore(
            config.Agent.StateDirectory,
            services.GetRequiredService<ILogger<FileSequenceStore>>()));
        builder.Services.AddSingleton<DeviceAgentRuntime>();
        builder.Services.AddSingleton<AgentConfigReloader>();
        builder.Services.AddHostedService(services => services.GetRequiredService<DeviceAgentRuntime>());
        builder.Services.AddHostedService<ConfigWatcherService>();

        var host = builder.Build();
        return new AgentRuntimeHost(host, config, configFile);
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            return;
        }

        await _host.StartAsync(cancellationToken).ConfigureAwait(false);

        var logger = _host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Sim.DeviceAgent");
        logger.LogInformation(
            "Device agent {AgentId} started (config: {ConfigPath}).",
            Config.Agent.Id,
            ConfigFile.Path);
    }

    public Task StopAsync(CancellationToken cancellationToken) => _host.StopAsync(cancellationToken);

    public AgentStatusSnapshot GetSnapshot()
    {
        var registry = _host.Services.GetRequiredService<AgentDeviceRegistry>();
        var sinks = _host.Services.GetRequiredService<AgentSinkRegistry>();
        return new AgentStatusSnapshot(
            Config.Agent.Id,
            Config.Agent.DisplayName,
            ConfigFile.Path,
            registry.GetStatuses(),
            sinks.GetStatuses(),
            Reloader.GetStatus());
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await ((IAsyncDisposable)_host).DisposeAsync().ConfigureAwait(false);
    }
}
