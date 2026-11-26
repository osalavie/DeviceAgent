using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Confluent.Kafka;
using Sim.DeviceAgent.Configuration;
using Sim.DeviceAgent.Contracts;
using Sim.DeviceAgent.Spool;

namespace Sim.DeviceAgent.Runtime;

public sealed class KafkaSink : IEnvelopeSink
{
    private const string DefaultMessageKey = "{agentId}:{deviceId}";
    private static readonly TimeSpan MetadataTimeout = TimeSpan.FromSeconds(5);

    private readonly string _id;
    private readonly SinkConfig _config;
    private readonly AgentOptions _agent;
    private readonly SpoolStore _spool;
    private readonly ILogger<KafkaSink> _logger;
    private readonly Channel<bool> _wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropWrite,
        SingleReader = true,
        SingleWriter = false,
        AllowSynchronousContinuations = false
    });

    private CancellationTokenSource? _cts;
    private Task? _runTask;
    private IProducer<string, byte[]>? _producer;
    private int _started;
    private int _stopped;
    private int _disposed;
    private int _connected;
    private int _healthy;
    private int _backoffSeconds;
    private string? _lastError;

    public KafkaSink(
        string id,
        SinkConfig config,
        AgentOptions agent,
        ILoggerFactory loggerFactory)
    {
        _id = id;
        _config = config;
        _agent = agent;
        _logger = loggerFactory.CreateLogger<KafkaSink>();
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

    public string Type => "kafka";

    public string Endpoint => $"kafka://{_config.BootstrapServers}/{_config.Topic}";

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
            "Kafka sink {SinkId} started for {Endpoint} (topic={Topic}, messageKey={MessageKey}, spool: {SpoolRoot}).",
            _id,
            Endpoint,
            _config.Topic,
            _config.MessageKey,
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
                _logger.LogWarning(ex, "Kafka sink {SinkId} did not stop cleanly.", _id);
            }
        }

        try
        {
            await _spool.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Kafka sink {SinkId} failed to flush spool state.", _id);
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
                _logger.LogWarning(ex, "Kafka sink {SinkId} connection ended.", _id);
            }
            finally
            {
                Interlocked.Exchange(ref _connected, 0);
                Volatile.Write(ref _producer, null);
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
                "Kafka sink {SinkId} reconnecting in {DelaySeconds:0.0}s.",
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
        var producerConfig = BuildProducerConfig();

        _logger.LogInformation("Kafka sink {SinkId} connecting to {Endpoint}.", _id, Endpoint);

        using var producer = new ProducerBuilder<string, byte[]>(producerConfig)
            .SetErrorHandler((_, error) =>
            {
                if (error.IsFatal)
                {
                    SetError(error.Reason);
                }

                _logger.LogDebug(
                    "Kafka sink {SinkId} client error {Code}: {Reason}.",
                    _id,
                    error.Code,
                    error.Reason);
            })
            .Build();
        using var admin = new AdminClientBuilder(producerConfig).Build();

        // The producer connects lazily; probe metadata so an unreachable broker
        // surfaces as a reconnect instead of stalling the spool pump.
        admin.GetMetadata(MetadataTimeout);

        Volatile.Write(ref _producer, producer);
        Interlocked.Exchange(ref _connected, 1);
        SetError(null);
        _wake.Writer.TryWrite(true);

        _logger.LogInformation("Kafka sink {SinkId} connected.", _id);

        await PumpLoopAsync(producer, cancellationToken).ConfigureAwait(false);
    }

    private async Task PumpLoopAsync(
        IProducer<string, byte[]> producer,
        CancellationToken cancellationToken)
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

                await PublishEntryAsync(producer, entry, cancellationToken).ConfigureAwait(false);
                _spool.MarkDelivered(entry);
                Interlocked.Exchange(ref _healthy, 1);
            }
        }
    }

    private async ValueTask PublishStatusAsync(MessageEnvelope envelope, CancellationToken cancellationToken)
    {
        var producer = Volatile.Read(ref _producer);
        if (producer is null || Volatile.Read(ref _connected) == 0)
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
            await PublishEntryAsync(producer, entry, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            SetError(ex.Message);
            _logger.LogWarning(ex, "Kafka sink {SinkId} failed to publish a status message.", _id);
        }
    }

    private async ValueTask PublishEntryAsync(
        IProducer<string, byte[]> producer,
        SpoolEntry entry,
        CancellationToken cancellationToken)
    {
        using var document = ParseEnvelope(entry.Json);
        var root = document?.RootElement;
        var messageType = GetString(root, "type");
        var timestamp = GetTimestamp(root);

        var topic = BuildTemplate(_config.Topic, entry.DeviceId);
        var key = BuildTemplate(
            string.IsNullOrWhiteSpace(_config.MessageKey) ? DefaultMessageKey : _config.MessageKey,
            entry.DeviceId);

        var headers = new Headers();
        AddHeader(headers, "agentId", _agent.Id);
        AddHeader(headers, "deviceId", entry.DeviceId);
        AddHeader(headers, "type", messageType);
        AddHeader(headers, "messageId", $"{_agent.Id}:{entry.DeviceId}:{entry.Seq}");

        var message = new Message<string, byte[]>
        {
            Key = key,
            Value = Encoding.UTF8.GetBytes(entry.Json),
            Headers = headers,
            Timestamp = new Timestamp(timestamp ?? DateTimeOffset.UtcNow)
        };

        try
        {
            var delivery = await producer.ProduceAsync(topic, message, cancellationToken).ConfigureAwait(false);
            if (delivery.Status != PersistenceStatus.Persisted)
            {
                throw new KafkaException(new Error(
                    ErrorCode.Local_MsgTimedOut,
                    $"Message was not persisted (status: {delivery.Status}).",
                    isFatal: false));
            }
        }
        catch (KafkaException ex)
        {
            SetError(ex.Error.Reason);
            throw;
        }
    }

    private ProducerConfig BuildProducerConfig()
    {
        var protocol = ParseSecurityProtocol(_config.SecurityProtocol);
        var config = new ProducerConfig
        {
            BootstrapServers = _config.BootstrapServers,
            ClientId = $"{_agent.Id}/{_id}",
            SecurityProtocol = protocol,
            Acks = Acks.All,
            EnableIdempotence = true,
            // Bound how long a stuck delivery blocks the per-device spool pump.
            MessageTimeoutMs = 30000
        };

        if (protocol is SecurityProtocol.Ssl or SecurityProtocol.SaslSsl &&
            !string.IsNullOrWhiteSpace(_config.SslCaLocation))
        {
            config.SslCaLocation = _config.SslCaLocation;
        }

        if (protocol is SecurityProtocol.SaslPlaintext or SecurityProtocol.SaslSsl)
        {
            config.SaslMechanism = ParseSaslMechanism(_config.SaslMechanism);
            if (!string.IsNullOrWhiteSpace(_config.SaslUsername))
            {
                config.SaslUsername = _config.SaslUsername;
            }

            if (!string.IsNullOrEmpty(_config.SaslPassword))
            {
                config.SaslPassword = _config.SaslPassword;
            }
        }

        return config;
    }

    private string BuildTemplate(string template, string deviceId)
    {
        return template
            .Replace("{agentId}", _agent.Id, StringComparison.OrdinalIgnoreCase)
            .Replace("{deviceId}", deviceId, StringComparison.OrdinalIgnoreCase);
    }

    private static void AddHeader(Headers headers, string key, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            headers.Add(key, Encoding.UTF8.GetBytes(value));
        }
    }

    private static SecurityProtocol ParseSecurityProtocol(string? value)
    {
        return NormalizeSetting(value) switch
        {
            "ssl" => SecurityProtocol.Ssl,
            "saslplaintext" => SecurityProtocol.SaslPlaintext,
            "saslssl" => SecurityProtocol.SaslSsl,
            _ => SecurityProtocol.Plaintext
        };
    }

    private static SaslMechanism ParseSaslMechanism(string? value)
    {
        return NormalizeSetting(value) switch
        {
            "scramsha256" => SaslMechanism.ScramSha256,
            "scramsha512" => SaslMechanism.ScramSha512,
            _ => SaslMechanism.Plain
        };
    }

    private static string NormalizeSetting(string? value)
        => value?.Trim()
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant() ?? string.Empty;

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
