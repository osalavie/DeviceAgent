using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Sim.DeviceAgent.Configuration;
using Sim.DeviceAgent.Contracts;
using Sim.DeviceAgent.Runtime;
using Sim.DeviceAgent.WebSockets;
using Xunit;

namespace Sim.DeviceAgent.Tests;

public sealed class ProtocolTests
{
    [Fact]
    public void Subscription_AllMatchesEveryEnvelope()
    {
        var envelope = CreateEnvelope();

        Assert.True(ClientSubscription.All.Matches(envelope));
    }

    [Fact]
    public void Subscription_FiltersByDevice()
    {
        var envelope = CreateEnvelope();

        Assert.True(new ClientSubscription(["PT-001"]).Matches(envelope));
        Assert.False(new ClientSubscription(["DS-001"]).Matches(envelope));
        Assert.True(new ClientSubscription(null).Matches(envelope));
    }

    [Fact]
    public void TokenMatching_IsOptionalForLoopbackUse()
    {
        Assert.True(AgentTokenAuth.TokenMatches(null, string.Empty));
        Assert.True(AgentTokenAuth.TokenMatches("anything", string.Empty));
        Assert.True(AgentTokenAuth.TokenMatches("secret", "secret"));
        Assert.False(AgentTokenAuth.TokenMatches(null, "secret"));
        Assert.False(AgentTokenAuth.TokenMatches("wrong", "secret"));
    }

    [Fact]
    public async Task WsServerSink_StartsListeningAndReportsEndpoint()
    {
        var config = new AgentConfig
        {
            Agent = new AgentOptions { Id = "agent-test" },
            Sinks = new Dictionary<string, SinkConfig>(StringComparer.OrdinalIgnoreCase)
        };
        var sinkConfig = new SinkConfig
        {
            Type = "wsServer",
            Bind = "127.0.0.1",
            Port = 0
        };
        var sink = new WsServerSink(
            "local",
            sinkConfig,
            config,
            new AgentDeviceRegistry(),
            new AgentSinkRegistry(),
            reloader: null,
            NullLoggerFactory.Instance);

        try
        {
            await sink.StartAsync(CancellationToken.None);

            var status = sink.GetStatus();

            Assert.Equal("listening", status.State);
            Assert.Equal(0, status.Clients);
            Assert.Equal(0, status.PendingEntries);
            Assert.StartsWith("ws://127.0.0.1:", status.Endpoint, StringComparison.Ordinal);
            Assert.EndsWith("/ws", status.Endpoint, StringComparison.Ordinal);
        }
        finally
        {
            await sink.DisposeAsync();
        }
    }

    [Fact]
    public async Task WsServerSinks_ListenOnSeparateAddresses_WithPerSinkTokens()
    {
        var agentConfig = new AgentConfig
        {
            Agent = new AgentOptions { Id = "agent-test", DisplayName = "Test agent" }
        };
        var registry = new AgentDeviceRegistry();
        var sinkRegistry = new AgentSinkRegistry();
        var protectedSink = new WsServerSink(
            "protected",
            new SinkConfig { Type = "wsServer", Bind = "127.0.0.1", Port = 0, Token = "secret" },
            agentConfig,
            registry,
            sinkRegistry,
            reloader: null,
            NullLoggerFactory.Instance);
        var openSink = new WsServerSink(
            "open",
            new SinkConfig { Type = "wsServer", Bind = "127.0.0.1", Port = 0 },
            agentConfig,
            registry,
            sinkRegistry,
            reloader: null,
            NullLoggerFactory.Instance);

        try
        {
            await protectedSink.StartAsync(CancellationToken.None);
            await openSink.StartAsync(CancellationToken.None);

            Assert.NotEqual(protectedSink.Endpoint, openSink.Endpoint);

            await Assert.ThrowsAnyAsync<WebSocketException>(
                () => SendHelloAsync(protectedSink.Endpoint, token: null));

            var protectedAck = await SendHelloAsync(protectedSink.Endpoint, "secret");
            Assert.Equal(MessageTypes.HelloAck, GetType(protectedAck));

            var openAck = await SendHelloAsync(openSink.Endpoint, token: null);
            Assert.Equal(MessageTypes.HelloAck, GetType(openAck));
        }
        finally
        {
            await protectedSink.DisposeAsync();
            await openSink.DisposeAsync();
        }
    }

    [Fact]
    public async Task WsServerSink_ServesManagementEndpoints()
    {
        var agentConfig = new AgentConfig
        {
            Agent = new AgentOptions { Id = "agent-test", DisplayName = "Test agent" }
        };
        var sinkConfig = new SinkConfig { Type = "wsServer", Bind = "127.0.0.1", Port = 0 };
        agentConfig.Sinks["local"] = sinkConfig;
        var sink = new WsServerSink(
            "local",
            sinkConfig,
            agentConfig,
            new AgentDeviceRegistry(),
            new AgentSinkRegistry(),
            reloader: null,
            NullLoggerFactory.Instance);

        try
        {
            await sink.StartAsync(CancellationToken.None);
            var uri = new Uri(sink.Endpoint);
            using var client = new HttpClient { BaseAddress = new Uri($"http://{uri.Authority}") };

            var health = await client.GetStringAsync("/health");
            Assert.Contains("agent-test", health, StringComparison.Ordinal);

            using var status = JsonDocument.Parse(await client.GetStringAsync("/api/status"));
            var root = status.RootElement;
            Assert.Equal("agent-test", root.GetProperty("agentId").GetString());
            var server = Assert.Single(root.GetProperty("wsServers").EnumerateArray());
            Assert.Equal("local", server.GetProperty("sinkId").GetString());
            Assert.EndsWith("/ws", server.GetProperty("url").GetString(), StringComparison.Ordinal);
            Assert.Equal(0, server.GetProperty("port").GetInt32());
        }
        finally
        {
            await sink.DisposeAsync();
        }
    }

    private static async Task<string> SendHelloAsync(string endpoint, string? token)
    {
        using var socket = new ClientWebSocket();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await socket.ConnectAsync(new Uri(endpoint), timeout.Token);

        var hello = JsonSerializer.Serialize(new
        {
            v = 1,
            type = MessageTypes.Hello,
            token
        }, AgentJson.Options);

        await socket.SendAsync(
            Encoding.UTF8.GetBytes(hello),
            WebSocketMessageType.Text,
            endOfMessage: true,
            timeout.Token);

        var buffer = new byte[16 * 1024];
        var result = await socket.ReceiveAsync(buffer, timeout.Token);
        if (result.MessageType == WebSocketMessageType.Close)
        {
            throw new WebSocketException($"The server closed the connection: {result.CloseStatus}.");
        }

        return Encoding.UTF8.GetString(buffer, 0, result.Count);
    }

    private static string? GetType(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.TryGetProperty("type", out var type) ? type.GetString() : null;
    }

    [Fact]
    public void Envelope_SerializesWithCamelCaseAndOmitsNulls()
    {
        var envelope = CreateEnvelope();

        var json = JsonSerializer.Serialize(envelope, AgentJson.Options);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal(MessageTypes.Data, root.GetProperty("type").GetString());
        Assert.Equal("agent-test", root.GetProperty("agentId").GetString());
        Assert.Equal("PT-001", root.GetProperty("deviceId").GetString());
        Assert.Equal(7, root.GetProperty("seq").GetInt64());
        Assert.False(root.TryGetProperty("channelId", out _));
        Assert.False(root.TryGetProperty("text", out _));
        Assert.False(root.TryGetProperty("error", out _));
    }

    private static MessageEnvelope CreateEnvelope()
    {
        return new MessageEnvelope
        {
            Type = MessageTypes.Data,
            AgentId = "agent-test",
            DeviceId = "PT-001",
            Seq = 7,
            Ts = DateTimeOffset.UtcNow
        };
    }
}
