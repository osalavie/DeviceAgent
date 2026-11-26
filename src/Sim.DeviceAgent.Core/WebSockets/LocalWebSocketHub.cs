using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Sim.DeviceAgent.Configuration;
using Sim.DeviceAgent.Contracts;
using Sim.DeviceAgent.Runtime;

namespace Sim.DeviceAgent.WebSockets;

public sealed class LocalWebSocketHub
{
    private const int MaxMessageBytes = 64 * 1024;

    private readonly AgentConfig _config;
    private readonly SinkConfig _sinkConfig;
    private readonly AgentDeviceRegistry _registry;
    private readonly ILogger<LocalWebSocketHub> _logger;
    private readonly ConcurrentDictionary<Guid, ClientConnection> _clients = new();

    public LocalWebSocketHub(
        AgentConfig config,
        SinkConfig sinkConfig,
        AgentDeviceRegistry registry,
        ILogger<LocalWebSocketHub> logger)
    {
        _config = config;
        _sinkConfig = sinkConfig;
        _registry = registry;
        _logger = logger;
    }

    public int AuthenticatedClientCount => _clients.Values.Count(client => client.Authenticated);

    public async Task HandleAsync(HttpContext context)
    {
        var origin = context.Request.Headers.Origin.ToString();
        if (!IsOriginAllowed(origin))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsync("Origin is not allowed.");
            return;
        }

        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync("WebSocket upgrade is required.");
            return;
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        var client = new ClientConnection(Guid.NewGuid(), socket);
        _clients[client.Id] = client;
        var sendTask = SendLoopAsync(client, cts.Token);

        try
        {
            await ReceiveLoopAsync(client, cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Client disconnected or host is shutting down.
        }
        catch (WebSocketException ex)
        {
            _logger.LogDebug(ex, "WebSocket client {ClientId} disconnected.", client.Id);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "WebSocket client {ClientId} failed.", client.Id);
        }
        finally
        {
            _clients.TryRemove(client.Id, out _);
            cts.Cancel();
            client.Outbound.Writer.TryComplete();
            try
            {
                await sendTask.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch
            {
                // The send loop is already failing or finished.
            }

            await CloseAsync(socket, WebSocketCloseStatus.NormalClosure, "closed");
        }
    }

    public ValueTask PublishAsync(MessageEnvelope envelope, CancellationToken cancellationToken = default)
    {
        if (_clients.IsEmpty)
        {
            return ValueTask.CompletedTask;
        }

        var json = JsonSerializer.Serialize(envelope, AgentJson.Options);
        foreach (var client in _clients.Values)
        {
            if (client.Authenticated && client.Subscription.Matches(envelope))
            {
                Enqueue(client, json);
            }
        }

        return ValueTask.CompletedTask;
    }

    private async Task ReceiveLoopAsync(ClientConnection client, CancellationToken cancellationToken)
    {
        using var helloCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        helloCts.CancelAfter(TimeSpan.FromSeconds(3));

        string? helloText;
        try
        {
            helloText = await ReceiveTextAsync(client.Socket, helloCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await CloseAsync(client.Socket, WebSocketCloseStatus.PolicyViolation, "Authentication timeout.");
            return;
        }

        if (helloText is null)
        {
            return;
        }

        if (!TryHandleHello(client, helloText, out var helloError))
        {
            await CloseAsync(client.Socket, WebSocketCloseStatus.PolicyViolation, helloError ?? "Authentication failed.");
            return;
        }

        client.Authenticated = true;
        await EnqueueAsync(client, CreateHelloAck());
        await SendStatusSnapshotAsync(client);

        while (!cancellationToken.IsCancellationRequested)
        {
            var text = await ReceiveTextAsync(client.Socket, cancellationToken);
            if (text is null)
            {
                break;
            }

            await HandleClientMessageAsync(client, text, cancellationToken);
        }
    }

    private bool TryHandleHello(ClientConnection client, string text, out string? error)
    {
        error = null;
        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (TryGetString(root, "type") != MessageTypes.Hello)
            {
                error = "The first message must be hello.";
                return false;
            }

            var token = TryGetString(root, "token");
            if (!AgentTokenAuth.TokenMatches(token, _sinkConfig.Token))
            {
                error = "Invalid agent token.";
                return false;
            }

            client.Subscription = root.TryGetProperty("subscribe", out var subscribe)
                ? ClientSubscription.FromJson(subscribe)
                : ClientSubscription.All;
            return true;
        }
        catch (JsonException)
        {
            error = "Invalid JSON.";
            return false;
        }
    }

    private async Task HandleClientMessageAsync(ClientConnection client, string text, CancellationToken cancellationToken)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text);
        }
        catch (JsonException)
        {
            await SendErrorAsync(client, null, "invalid_json", "Message is not valid JSON.");
            return;
        }

        using (document)
        {
            var root = document.RootElement;
            switch (TryGetString(root, "type"))
            {
                case MessageTypes.Subscribe:
                    client.Subscription = root.TryGetProperty("subscribe", out var subscribe)
                        ? ClientSubscription.FromJson(subscribe)
                        : ClientSubscription.FromJson(root);
                    await EnqueueAsync(client, CreateSubscribedEvent(client));
                    await SendStatusSnapshotAsync(client);
                    break;

                case MessageTypes.Command:
                    var command = root.Clone();
                    _ = Task.Run(() => HandleCommandAsync(client, command, cancellationToken), CancellationToken.None);
                    break;

                case MessageTypes.Ping:
                    await EnqueueAsync(client, new MessageEnvelope
                    {
                        Type = MessageTypes.Pong,
                        AgentId = _config.Agent.Id,
                        Ts = DateTimeOffset.UtcNow
                    });
                    break;

                default:
                    await SendErrorAsync(client, null, "unknown_message_type", $"Unsupported message type '{TryGetString(root, "type")}'.");
                    break;
            }
        }
    }

    private async Task HandleCommandAsync(ClientConnection client, JsonElement root, CancellationToken cancellationToken)
    {
        var requestId = TryGetString(root, "requestId") ?? Guid.NewGuid().ToString("N");
        var deviceId = TryGetString(root, "deviceId");

        try
        {
            if (string.IsNullOrWhiteSpace(deviceId)
                || !root.TryGetProperty("payload", out var payload)
                || payload.ValueKind != JsonValueKind.Object)
            {
                await SendCommandErrorAsync(client, deviceId, requestId, "invalid_command", "deviceId and payload are required.");
                return;
            }

            var text = TryGetString(payload, "text");
            if (string.IsNullOrWhiteSpace(text))
            {
                await SendCommandErrorAsync(client, deviceId, requestId, "invalid_command", "payload.text is required.");
                return;
            }

            var timeoutMs = 2000;
            if (payload.TryGetProperty("timeoutMs", out var timeoutElement)
                && timeoutElement.TryGetInt32(out var requestedTimeout))
            {
                timeoutMs = Math.Clamp(requestedTimeout, 100, 60_000);
            }

            string? matchPattern = null;
            if (payload.TryGetProperty("match", out var match))
            {
                if (match.ValueKind == JsonValueKind.String)
                {
                    matchPattern = match.GetString();
                }
                else if (match.ValueKind == JsonValueKind.Object && match.TryGetProperty("pattern", out var patternElement))
                {
                    matchPattern = patternElement.GetString();
                }
            }

            var device = _registry.Get(deviceId);
            if (device is null)
            {
                await SendCommandErrorAsync(client, deviceId, requestId, "unknown_device", $"Device '{deviceId}' was not found.");
                return;
            }

            var outcome = await device.SendCommandAsync(
                text,
                TimeSpan.FromMilliseconds(timeoutMs),
                matchPattern,
                cancellationToken);

            var response = new MessageEnvelope
            {
                Type = MessageTypes.Response,
                AgentId = _config.Agent.Id,
                DeviceId = device.DeviceId,
                DeviceType = device.DeviceType,
                RequestId = requestId,
                Ts = DateTimeOffset.UtcNow,
                Ok = outcome.Success
            };

            if (outcome.Success && outcome.Match is { } matchResult)
            {
                response.Ts = matchResult.Timestamp;
                response.Seq = matchResult.Seq;
                response.Raw = Convert.ToBase64String(matchResult.Frame.Raw);
                response.Text = matchResult.Frame.Text;
                response.Parsed = matchResult.Frame.Parsed;
                if (matchResult.Frame.ParseError is not null)
                {
                    response.Error = new MessageError("parse_error", matchResult.Frame.ParseError);
                }
            }
            else
            {
                response.Error = new MessageError(
                    outcome.ErrorCode ?? "command_failed",
                    outcome.ErrorMessage ?? "Command failed.",
                    outcome.TimedOut);
            }

            await EnqueueAsync(client, response);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Client disconnected.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Command on device {DeviceId} failed.", deviceId);
            if (!cancellationToken.IsCancellationRequested)
            {
                await SendCommandErrorAsync(client, deviceId, requestId, "command_failed", ex.Message);
            }
        }
    }

    private async Task SendLoopAsync(ClientConnection client, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var message in client.Outbound.Reader.ReadAllAsync(cancellationToken))
            {
                var bytes = Encoding.UTF8.GetBytes(message);
                await client.Socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (WebSocketException ex)
        {
            _logger.LogDebug(ex, "WebSocket send loop for client {ClientId} ended.", client.Id);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "WebSocket send loop for client {ClientId} failed.", client.Id);
        }
    }

    public async ValueTask PublishStatusSnapshotAsync()
    {
        foreach (var client in _clients.Values)
        {
            if (!client.Authenticated)
            {
                continue;
            }

            await SendStatusSnapshotAsync(client);
        }
    }

    private async Task SendStatusSnapshotAsync(ClientConnection client)
    {
        foreach (var status in _registry.GetStatuses())
        {
            var envelope = CreateStatusEnvelope(status);
            if (client.Subscription.Matches(envelope))
            {
                await EnqueueAsync(client, envelope);
            }
        }
    }

    private MessageEnvelope CreateHelloAck()
    {
        var devices = _registry.GetStatuses()
            .Select(status => new
            {
                status.DeviceId,
                status.DeviceType,
                status.DeviceName,
                status.Type,
                status.State
            })
            .ToArray();

        return new MessageEnvelope
        {
            Type = MessageTypes.HelloAck,
            AgentId = _config.Agent.Id,
            Ts = DateTimeOffset.UtcNow,
            Payload = JsonSerializer.SerializeToElement(new
            {
                agentId = _config.Agent.Id,
                displayName = _config.Agent.DisplayName,
                devices
            }, AgentJson.Options)
        };
    }

    private MessageEnvelope CreateSubscribedEvent(ClientConnection client)
    {
        return new MessageEnvelope
        {
            Type = MessageTypes.Event,
            AgentId = _config.Agent.Id,
            Ts = DateTimeOffset.UtcNow,
            Payload = JsonSerializer.SerializeToElement(new
            {
                subscribed = new
                {
                    devices = client.Subscription.Devices.ToArray(),
                    types = client.Subscription.Types.ToArray()
                }
            }, AgentJson.Options)
        };
    }

    private MessageEnvelope CreateStatusEnvelope(DeviceStatus status)
    {
        var envelope = new MessageEnvelope
        {
            Type = MessageTypes.Status,
            AgentId = _config.Agent.Id,
            DeviceId = status.DeviceId,
            DeviceType = status.DeviceType,
            Seq = status.Seq,
            Ts = DateTimeOffset.UtcNow,
            State = status.State,
            Payload = JsonSerializer.SerializeToElement(new
            {
                status.Type,
                status.LastDataAt,
                status.ReceivedFrames,
                status.DroppedFrames,
                status.ParseErrors
            }, AgentJson.Options)
        };

        if (status.LastError is not null)
        {
            envelope.Error = new MessageError("device_error", status.LastError);
        }

        return envelope;
    }

    private async Task SendCommandErrorAsync(
        ClientConnection client,
        string? deviceId,
        string requestId,
        string code,
        string message)
    {
        var device = _registry.Get(deviceId);
        await EnqueueAsync(client, new MessageEnvelope
        {
            Type = MessageTypes.Response,
            AgentId = _config.Agent.Id,
            DeviceId = device?.DeviceId ?? deviceId,
            DeviceType = device?.DeviceType,
            RequestId = requestId,
            Ts = DateTimeOffset.UtcNow,
            Ok = false,
            Error = new MessageError(code, message)
        });
    }

    private async Task SendErrorAsync(
        ClientConnection client,
        string? requestId,
        string code,
        string message)
    {
        await EnqueueAsync(client, new MessageEnvelope
        {
            Type = MessageTypes.Error,
            AgentId = _config.Agent.Id,
            RequestId = requestId,
            Ts = DateTimeOffset.UtcNow,
            Error = new MessageError(code, message)
        });
    }

    private static async Task<string?> ReceiveTextAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        using var message = new MemoryStream();

        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            if (result.MessageType != WebSocketMessageType.Text)
            {
                return null;
            }

            message.Write(buffer, 0, result.Count);
            if (message.Length > MaxMessageBytes)
            {
                return null;
            }

            if (result.EndOfMessage)
            {
                return Encoding.UTF8.GetString(message.ToArray());
            }
        }
    }

    private bool IsOriginAllowed(string origin)
    {
        if (string.IsNullOrWhiteSpace(origin))
        {
            return true;
        }

        return (_sinkConfig.AllowedOrigins ?? [])
            .Any(allowed => string.Equals(allowed, origin, StringComparison.OrdinalIgnoreCase));
    }

    private async Task EnqueueAsync(ClientConnection client, MessageEnvelope envelope)
        => Enqueue(client, JsonSerializer.Serialize(envelope, AgentJson.Options));

    private void Enqueue(ClientConnection client, string json)
    {
        if (client.Outbound.Writer.TryWrite(json))
        {
            return;
        }

        while (client.Outbound.Reader.TryRead(out _))
        {
            Interlocked.Increment(ref client.Dropped);
        }

        if (!client.Outbound.Writer.TryWrite(json))
        {
            Interlocked.Increment(ref client.Dropped);
        }
    }

    private static string? TryGetString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static async Task CloseAsync(WebSocket socket, WebSocketCloseStatus status, string description)
    {
        if (socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived))
        {
            return;
        }

        var safeDescription = description.Length <= 120 ? description : description[..120];
        try
        {
            await socket.CloseAsync(status, safeDescription, CancellationToken.None);
        }
        catch
        {
            // The peer may already have gone away.
        }
    }

    private sealed class ClientConnection
    {
        public ClientConnection(Guid id, WebSocket socket)
        {
            Id = id;
            Socket = socket;
        }

        public Guid Id { get; }

        public WebSocket Socket { get; }

        public Channel<string> Outbound { get; } = Channel.CreateBounded<string>(
            new BoundedChannelOptions(512)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false
            });

        public volatile bool Authenticated;

        public volatile ClientSubscription Subscription = ClientSubscription.All;

        public long Dropped;
    }
}
