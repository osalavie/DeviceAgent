using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using RabbitMQ.Client;
using Sim.DeviceAgent.Configuration;
using Sim.DeviceAgent.Contracts;
using Sim.DeviceAgent.Spool;

namespace Sim.DeviceAgent.Runtime;

public sealed class RabbitMqSink : IEnvelopeSink
{
    private readonly string _id;
    private readonly SinkConfig _config;
    private readonly AgentOptions _agent;
    private readonly SpoolStore _spool;
    private readonly ILogger<RabbitMqSink> _logger;
    private readonly SemaphoreSlim _publishLock = new(1, 1);
    private readonly Channel<bool> _wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropWrite,
        SingleReader = true,
        SingleWriter = false,
        AllowSynchronousContinuations = false
    });

    private CancellationTokenSource? _cts;
    private Task? _runTask;
    private IChannel? _channel;
    private int _started;
    private int _stopped;
    private int _disposed;
    private int _connected;
    private int _healthy;
    private int _backoffSeconds;
    private long _sentCount;
    private string? _lastError;

    public RabbitMqSink(
        string id,
        SinkConfig config,
        AgentOptions agent,
        ILoggerFactory loggerFactory)
    {
        _id = id;
        _config = config;
        _agent = agent;
        _logger = loggerFactory.CreateLogger<RabbitMqSink>();
        _backoffSeconds = config.ReconnectMinSeconds;

        var spoolRoot = string.IsNullOrWhiteSpace(config.SpoolDirectory)
            ? Path.Combine(agent.StateDirectory, "spool", agent.Id, id)
            : config.SpoolDirectory;
        _spool = new SpoolStore(
            Path.GetFullPath(spoolRoot),
            config.SpoolSegmentMegabytes * 1024L * 1024L,
            config.SpoolMaxMegabytes * 1024L * 1024L,
            loggerFactory);
    }

    public string Id => _id;

    public string Type => "rabbitmq";

    public string Endpoint => $"amqp://{_config.Host}:{_config.Port}/{_config.Exchange}";

    public SinkStatus GetStatus()
    {
        var state = Volatile.Read(ref _stopped) != 0
            ? "stopped"
            : Volatile.Read(ref _connected) != 0
                ? "connected"
                : "disconnected";

        return new SinkStatus(
            _id,
            Type,
            state,
            _spool.PendingCount,
            Volatile.Read(ref _connected) != 0 ? 1 : 0,
            Volatile.Read(ref _lastError),
            Endpoint);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            return Task.CompletedTask;
        }

        _cts = new CancellationTokenSource();
        _runTask = Task.Run(() => RunAsync(_cts.Token), CancellationToken.None);

        _logger.LogInformation(
            "RabbitMQ sink {SinkId} started for {Endpoint} (exchange={Exchange}, routingKey={RoutingKey}, spool: {SpoolRoot}).",
            _id,
            Endpoint,
            _config.Exchange,
            _config.RoutingKey,
            _spool.RootDirectory);
        return Task.CompletedTask;
    }

    public async ValueTask PublishAsync(MessageEnvelope envelope, CancellationToken cancellationToken = default)
    {
        if (string.Equals(envelope.Type, MessageTypes.Status, StringComparison.OrdinalIgnoreCase))
        {
            await PublishStatusAsync(envelope, cancellationToken).ConfigureAwait(false);
            return;
        }

        try
        {
            await _spool.AppendAsync(envelope, cancellationToken).ConfigureAwait(false);
            _wake.Writer.TryWrite(true);
        }
        catch (Exception ex)
        {
            SetError(ex.Message);
            throw;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0)
        {
            return;
        }

        _cts?.Cancel();
        _wake.Writer.TryWrite(true);

        if (_runTask is not null)
        {
            try
            {
                await _runTask.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
                _logger.LogWarning(ex, "RabbitMQ sink {SinkId} did not stop cleanly.", _id);
            }
        }

        try
        {
            await _spool.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "RabbitMQ sink {SinkId} failed to flush spool state.", _id);
        }

        _cts?.Dispose();
        _cts = null;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        await _spool.DisposeAsync().ConfigureAwait(false);
        _publishLock.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            Interlocked.Exchange(ref _healthy, 0);

            try
            {
                await ConnectAndRunAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                SetError(ex.Message);
                _logger.LogWarning(ex, "RabbitMQ sink {SinkId} connection ended.", _id);
            }
            finally
            {
                Interlocked.Exchange(ref _connected, 0);
                Volatile.Write(ref _channel, null);
            }

            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            try
            {
                await _spool.RewindAllAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            if (Volatile.Read(ref _healthy) != 0)
            {
                _backoffSeconds = _config.ReconnectMinSeconds;
            }

            var delay = TimeSpan.FromSeconds(_backoffSeconds)
                        + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 250));
            _backoffSeconds = Math.Min(_backoffSeconds * 2, _config.ReconnectMaxSeconds);

            _logger.LogInformation(
                "RabbitMQ sink {SinkId} reconnecting in {DelaySeconds:0.0}s.",
                _id,
                delay.TotalSeconds);

            try
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task ConnectAndRunAsync(CancellationToken cancellationToken)
    {
        var factory = new ConnectionFactory
        {
            HostName = string.IsNullOrWhiteSpace(_config.Host) ? "127.0.0.1" : _config.Host,
            Port = _config.Port,
            VirtualHost = string.IsNullOrWhiteSpace(_config.VirtualHost) ? "/" : _config.VirtualHost,
            UserName = string.IsNullOrWhiteSpace(_config.UserName) ? "guest" : _config.UserName,
            Password = _config.Password,
            AutomaticRecoveryEnabled = false,
            TopologyRecoveryEnabled = false,
            RequestedHeartbeat = TimeSpan.FromSeconds(30),
            ClientProvidedName = $"{_agent.Id}/{_id}"
        };

        _logger.LogInformation("RabbitMQ sink {SinkId} connecting to {Endpoint}.", _id, Endpoint);

        await using var connection = await factory.CreateConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var channel = await connection.CreateChannelAsync(
            new CreateChannelOptions(
                publisherConfirmationsEnabled: true,
                publisherConfirmationTrackingEnabled: true),
            cancellationToken).ConfigureAwait(false);

        await channel.ExchangeDeclareAsync(
            exchange: _config.Exchange,
            type: string.IsNullOrWhiteSpace(_config.ExchangeType) ? "topic" : _config.ExchangeType,
            durable: _config.Durable,
            autoDelete: false,
            arguments: null,
            passive: false,
            noWait: false,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        Volatile.Write(ref _channel, channel);
        Interlocked.Exchange(ref _connected, 1);
        SetError(null);
        _wake.Writer.TryWrite(true);

        _logger.LogInformation("RabbitMQ sink {SinkId} connected.", _id);

        await PumpLoopAsync(channel, cancellationToken).ConfigureAwait(false);
    }

    private async Task PumpLoopAsync(IChannel channel, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await _wake.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);

            while (!cancellationToken.IsCancellationRequested)
            {
                var entry = await _spool.TryReadNextAsync(cancellationToken).ConfigureAwait(false);
                if (entry is null)
                {
                    break;
                }

                await PublishEntryAsync(channel, entry, cancellationToken).ConfigureAwait(false);
                _spool.MarkDelivered(entry);
                Interlocked.Increment(ref _sentCount);
                Interlocked.Exchange(ref _healthy, 1);
            }
        }
    }

    private async ValueTask PublishStatusAsync(MessageEnvelope envelope, CancellationToken cancellationToken)
    {
        var channel = Volatile.Read(ref _channel);
        if (channel is null || Volatile.Read(ref _connected) == 0)
        {
            return;
        }

        try
        {
            var json = JsonSerializer.Serialize(envelope, AgentJson.Options);
            var entry = new SpoolEntry(
                envelope.DeviceId ?? string.Empty,
                envelope.Seq ?? 0,
                json);
            await PublishEntryAsync(channel, entry, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            SetError(ex.Message);
            _logger.LogWarning(ex, "RabbitMQ sink {SinkId} failed to publish a status message.", _id);
        }
    }

    private async ValueTask PublishEntryAsync(
        IChannel channel,
        SpoolEntry entry,
        CancellationToken cancellationToken)
    {
        using var document = ParseEnvelope(entry.Json);
        var root = document?.RootElement;
        var messageType = GetString(root, "type");
        var deviceType = GetString(root, "deviceType");
        var timestamp = GetTimestamp(root);

        var routingKey = BuildRoutingKey(entry.DeviceId, deviceType);
        var properties = new BasicProperties
        {
            ContentType = "application/json",
            ContentEncoding = "utf-8",
            DeliveryMode = _config.Persistent ? DeliveryModes.Persistent : DeliveryModes.Transient,
            MessageId = $"{_agent.Id}:{entry.DeviceId}:{entry.Seq}",
            AppId = _agent.Id,
            Type = messageType
        };

        if (timestamp is not null)
        {
            properties.Timestamp = new AmqpTimestamp(timestamp.Value.ToUnixTimeSeconds());
        }

        var body = Encoding.UTF8.GetBytes(entry.Json);

        await _publishLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await channel.BasicPublishAsync(
                exchange: _config.Exchange,
                routingKey: routingKey,
                mandatory: false,
                basicProperties: properties,
                body: body,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _publishLock.Release();
        }
    }

    private string BuildRoutingKey(string deviceId, string? deviceType)
    {
        var template = string.IsNullOrWhiteSpace(_config.RoutingKey)
            ? "device.{deviceId}"
            : _config.RoutingKey;

        return template
            .Replace("{agentId}", _agent.Id, StringComparison.OrdinalIgnoreCase)
            .Replace("{deviceId}", deviceId, StringComparison.OrdinalIgnoreCase)
            .Replace("{deviceType}", deviceType ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    private static JsonDocument? ParseEnvelope(string json)
    {
        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? GetString(JsonElement? root, string propertyName)
        => root is JsonElement { ValueKind: JsonValueKind.Object } element &&
           element.TryGetProperty(propertyName, out var property) &&
           property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static DateTimeOffset? GetTimestamp(JsonElement? root)
        => root is JsonElement { ValueKind: JsonValueKind.Object } element &&
           element.TryGetProperty("ts", out var property) &&
           property.ValueKind == JsonValueKind.String &&
           property.TryGetDateTimeOffset(out var timestamp)
            ? timestamp
            : null;

    private void SetError(string? message) => Volatile.Write(ref _lastError, message);
}
