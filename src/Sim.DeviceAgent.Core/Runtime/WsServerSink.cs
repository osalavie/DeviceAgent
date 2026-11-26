using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sim.DeviceAgent.Configuration;
using Sim.DeviceAgent.Contracts;
using Sim.DeviceAgent.Hosting;
using Sim.DeviceAgent.WebSockets;

namespace Sim.DeviceAgent.Runtime;

public sealed class WsServerSink : IEnvelopeSink
{
    private readonly string _id;
    private readonly SinkConfig _config;
    private readonly AgentConfig _agentConfig;
    private readonly AgentDeviceRegistry _deviceRegistry;
    private readonly AgentSinkRegistry _sinkRegistry;
    private readonly AgentConfigReloader? _reloader;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<WsServerSink> _logger;
    private readonly LocalWebSocketHub _hub;
    private WebApplication? _app;
    private int _started;
    private int _stopped;
    private int _disposed;
    private int _startFailed;
    private volatile string _state = "stopped";
    private string? _lastError;
    private string? _actualEndpoint;

    public WsServerSink(
        string id,
        SinkConfig config,
        AgentConfig agentConfig,
        AgentDeviceRegistry deviceRegistry,
        AgentSinkRegistry sinkRegistry,
        AgentConfigReloader? reloader,
        ILoggerFactory loggerFactory)
    {
        _id = id;
        _config = config;
        _agentConfig = agentConfig;
        _deviceRegistry = deviceRegistry;
        _sinkRegistry = sinkRegistry;
        _reloader = reloader;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<WsServerSink>();
        _hub = new LocalWebSocketHub(
            agentConfig,
            config,
            deviceRegistry,
            loggerFactory.CreateLogger<LocalWebSocketHub>());

        ConfiguredEndpoint = $"ws://{FormatHost(config.Bind)}:{config.Port}/ws";
    }

    public string Id => _id;

    public string Type => "wsServer";

    public string ConfiguredEndpoint { get; }

    public string Endpoint => _actualEndpoint ?? ConfiguredEndpoint;

    public SinkStatus GetStatus()
        => new(Id, Type, _state, 0, _hub.AuthenticatedClientCount, _lastError, Endpoint);

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            return;
        }

        WebApplication? app = null;
        try
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                ContentRootPath = AgentConfigLoader.ProgramDirectory
            });
            builder.Logging.ClearProviders();
            builder.Services.AddSingleton<ILoggerFactory>(new SharedLoggerFactory(_loggerFactory));
            builder.WebHost.UseUrls($"http://{FormatHost(_config.Bind)}:{_config.Port}");

            app = builder.Build();
            app.UseWebSockets(new WebSocketOptions
            {
                KeepAliveInterval = TimeSpan.FromSeconds(30)
            });
            app.Map("/ws", context => _hub.HandleAsync(context));
            ManagementEndpoints.Map(app, _agentConfig, _deviceRegistry, _sinkRegistry, _reloader);

            await app.StartAsync(cancellationToken).ConfigureAwait(false);
            _app = app;
            _actualEndpoint = ResolveEndpoint(app) ?? ConfiguredEndpoint;
            _state = "listening";
            _lastError = null;

            _logger.LogInformation(
                "wsServer sink {SinkId} listening on {Endpoint}.",
                Id,
                Endpoint);

            if (string.IsNullOrEmpty(_config.Token))
            {
                _logger.LogInformation(
                    "wsServer sink {SinkId} has no token; loopback clients connect without authentication.",
                    Id);
            }

            if (_config.AllowedOrigins.Count == 0)
            {
                _logger.LogWarning(
                    "wsServer sink {SinkId} allowedOrigins is empty. Browser WebSocket connections will be rejected; non-browser clients may connect without an Origin header.",
                    Id);
            }
        }
        catch (Exception ex)
        {
            _state = "faulted";
            _lastError = ex.Message;
            Volatile.Write(ref _startFailed, 1);

            if (app is not null)
            {
                try
                {
                    await app.DisposeAsync().ConfigureAwait(false);
                }
                catch
                {
                    // The original startup failure is the actionable error.
                }
            }

            throw;
        }
    }

    public ValueTask PublishAsync(MessageEnvelope envelope, CancellationToken cancellationToken = default)
        => _hub.PublishAsync(envelope, cancellationToken);

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0)
        {
            return;
        }

        var app = Interlocked.Exchange(ref _app, null);
        if (app is not null)
        {
            try
            {
                await app.StopAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "wsServer sink {SinkId} did not stop cleanly.", Id);
            }

            try
            {
                await app.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "wsServer sink {SinkId} failed to dispose its web host.", Id);
            }
        }

        if (Volatile.Read(ref _startFailed) == 0)
        {
            _state = "stopped";
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await StopAsync(CancellationToken.None).ConfigureAwait(false);
    }

    public ValueTask PublishStatusSnapshotAsync()
        => _hub.PublishStatusSnapshotAsync();

    private static string? ResolveEndpoint(WebApplication app)
    {
        var address = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()?
            .Addresses
            .FirstOrDefault();

        return string.IsNullOrWhiteSpace(address)
            ? null
            : address.Replace("http://", "ws://", StringComparison.OrdinalIgnoreCase) + "/ws";
    }

    private static string FormatHost(string? bind)
        => !string.IsNullOrWhiteSpace(bind) &&
           IPAddress.TryParse(bind, out var address) &&
           address.AddressFamily == AddressFamily.InterNetworkV6
            ? $"[{bind}]"
            : string.IsNullOrWhiteSpace(bind) ? "127.0.0.1" : bind;

    private sealed class SharedLoggerFactory : ILoggerFactory
    {
        private readonly ILoggerFactory _inner;

        public SharedLoggerFactory(ILoggerFactory inner) => _inner = inner;

        public void AddProvider(ILoggerProvider provider) => _inner.AddProvider(provider);

        public ILogger CreateLogger(string categoryName) => _inner.CreateLogger(categoryName);

        // The shared factory is owned by the agent host; this wrapper must not dispose it.
        public void Dispose()
        {
        }
    }
}
