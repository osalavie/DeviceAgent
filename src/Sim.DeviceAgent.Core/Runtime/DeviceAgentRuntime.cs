using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sim.DeviceAgent.Codecs;
using Sim.DeviceAgent.Configuration;
using Sim.DeviceAgent.Transports;
using Sim.DeviceAgent.WebSockets;

namespace Sim.DeviceAgent.Runtime;

public sealed class DeviceAgentRuntime : IHostedService, IAsyncDisposable
{
    private readonly AgentConfig _config;
    private readonly AgentDeviceRegistry _registry;
    private readonly AgentSinkRegistry _sinkRegistry;
    private readonly ISequenceStore _sequenceStore;
    private readonly IServiceProvider? _services;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<DeviceAgentRuntime> _logger;
    private readonly List<Device> _devices = [];
    private readonly List<IEnvelopeSink> _sinks = [];
    private readonly Dictionary<string, IEnvelopeSink> _sinksById = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private int _started;
    private int _running;
    private int _stopped;
    private int _disposed;

    public DeviceAgentRuntime(
        AgentConfig config,
        AgentDeviceRegistry registry,
        AgentSinkRegistry sinkRegistry,
        ISequenceStore sequenceStore,
        ILoggerFactory loggerFactory,
        ILogger<DeviceAgentRuntime> logger,
        IServiceProvider? services = null)
    {
        _config = config;
        _registry = registry;
        _sinkRegistry = sinkRegistry;
        _sequenceStore = sequenceStore;
        _loggerFactory = loggerFactory;
        _logger = logger;
        _services = services;
    }

    public bool IsRunning =>
        Volatile.Read(ref _running) != 0 && Volatile.Read(ref _stopped) == 0;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            return;
        }

        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _stopped) != 0)
            {
                return;
            }

            await StartSinksAsync(cancellationToken).ConfigureAwait(false);
            await StartDevicesAsync(cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _running, 1);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task ReloadAsync(AgentConfig candidate, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!IsRunning)
            {
                throw new InvalidOperationException("The agent runtime is not running; configuration cannot be reloaded.");
            }

            _logger.LogInformation("Applying a configuration reload.");

            await StopDevicesAsync(cancellationToken).ConfigureAwait(false);
            await StopSinksAsync(cancellationToken).ConfigureAwait(false);

            _config.Agent = candidate.Agent;
            _config.Sinks = candidate.Sinks;
            _config.Devices = candidate.Devices;

            await StartSinksAsync(cancellationToken).ConfigureAwait(false);
            await StartDevicesAsync(cancellationToken).ConfigureAwait(false);
            await PublishWsServerStatusSnapshotsAsync().ConfigureAwait(false);

            _logger.LogInformation(
                "Configuration reload complete: {DeviceCount} device(s), {SinkCount} sink(s).",
                _devices.Count,
                _sinks.Count);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0)
        {
            return;
        }

        await _lifecycleGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            _logger.LogInformation("Stopping the agent runtime.");
            Volatile.Write(ref _running, 0);
            await StopDevicesAsync(cancellationToken).ConfigureAwait(false);
            await StopSinksAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Agent runtime stopped.");
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);

        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _lifecycleGate.Dispose();
        }
    }

    private async Task StartSinksAsync(CancellationToken cancellationToken)
    {
        _sinkRegistry.Clear();

        foreach (var (sinkId, sinkConfig) in _config.Sinks)
        {
            IEnvelopeSink? sink = null;
            try
            {
                sink = CreateSink(sinkId, sinkConfig);
                _sinkRegistry.Add(sink);
                await sink.StartAsync(cancellationToken).ConfigureAwait(false);
                _sinks.Add(sink);
                _sinksById[sinkId] = sink;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Sink {SinkId} failed to start.", sinkId);
                if (sink is not null)
                {
                    try
                    {
                        await sink.DisposeAsync().ConfigureAwait(false);
                    }
                    catch
                    {
                        // The original startup failure is the actionable error.
                    }
                }
            }
        }
    }

    private async Task StartDevicesAsync(CancellationToken cancellationToken)
    {
        _registry.Clear();

        foreach (var deviceConfig in _config.Devices.Where(device => device.Enabled))
        {
            var deviceSinks = ResolveSinks(deviceConfig);
            if (deviceSinks.Count == 0)
            {
                _logger.LogError(
                    "Device {DeviceId} has no available sink; the device is skipped.",
                    deviceConfig.DeviceId);
                continue;
            }

            IDeviceTransport? transport = null;
            try
            {
                transport = TransportFactory.Create(deviceConfig, _loggerFactory);
                var codec = CodecFactory.Create(deviceConfig.Codec, deviceConfig.Type);
                var device = new Device(
                    _config.Agent.Id,
                    deviceConfig,
                    transport,
                    codec,
                    deviceSinks,
                    _sequenceStore,
                    _loggerFactory.CreateLogger<Device>());

                _devices.Add(device);
                _registry.Add(device);
                await device.StartAsync(cancellationToken).ConfigureAwait(false);

                _logger.LogInformation(
                    "Started device {DeviceId} (transport={TransportType}, sinks={Sinks}).",
                    deviceConfig.DeviceId,
                    deviceConfig.Type,
                    string.Join(",", deviceSinks.Select(sink => sink.Id)));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Device {DeviceId} failed to start.", deviceConfig.DeviceId);
                if (transport is not null)
                {
                    await transport.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
    }

    private async Task StopDevicesAsync(CancellationToken cancellationToken)
    {
        if (_devices.Count > 0)
        {
            try
            {
                await Task.WhenAll(_devices.Select(device => device.StopAsync()))
                    .WaitAsync(TimeSpan.FromSeconds(15), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "One or more devices did not stop cleanly.");
            }
        }

        _devices.Clear();
        _registry.Clear();
    }

    private async Task StopSinksAsync(CancellationToken cancellationToken)
    {
        for (var index = _sinks.Count - 1; index >= 0; index--)
        {
            var sink = _sinks[index];
            try
            {
                await sink.StopAsync(cancellationToken).ConfigureAwait(false);
                await sink.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Sink {SinkId} did not stop cleanly.", sink.Id);
            }
        }

        _sinks.Clear();
        _sinksById.Clear();
        _sinkRegistry.Clear();
    }

    private IEnvelopeSink CreateSink(string sinkId, SinkConfig sinkConfig)
    {
        return sinkConfig.Type.ToLowerInvariant() switch
        {
            "wsserver" => new WsServerSink(
                sinkId,
                sinkConfig,
                _config,
                _registry,
                _sinkRegistry,
                _services?.GetService<AgentConfigReloader>(),
                _loggerFactory),
            "wsclient" => new CenterWebSocketSink(
                sinkId,
                sinkConfig,
                _config.Agent,
                _loggerFactory),
            "rabbitmq" => new RabbitMqSink(
                sinkId,
                sinkConfig,
                _config.Agent,
                _loggerFactory),
            "kafka" => new KafkaSink(
                sinkId,
                sinkConfig,
                _config.Agent,
                _loggerFactory),
            _ => throw new InvalidOperationException($"Unsupported sink type '{sinkConfig.Type}'.")
        };
    }

    private async Task PublishWsServerStatusSnapshotsAsync()
    {
        foreach (var sink in _sinks.OfType<WsServerSink>())
        {
            await sink.PublishStatusSnapshotAsync().ConfigureAwait(false);
        }
    }

    private IReadOnlyList<IEnvelopeSink> ResolveSinks(DeviceConfig deviceConfig)
    {
        var sinks = new List<IEnvelopeSink>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var sinkRef in deviceConfig.Sinks)
        {
            if (!seen.Add(sinkRef))
            {
                continue;
            }

            if (_sinksById.TryGetValue(sinkRef, out var sink))
            {
                sinks.Add(sink);
            }
            else
            {
                _logger.LogWarning(
                    "Device {DeviceId} references sink {SinkId}, but that sink is not running.",
                    deviceConfig.DeviceId,
                    sinkRef);
            }
        }

        return sinks;
    }
}
