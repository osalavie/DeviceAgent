using System.Text.Json;
using Sim.DeviceAgent.Configuration;
using Xunit;

namespace Sim.DeviceAgent.Tests;

public sealed class AgentConfigWriterTests
{
    [Fact]
    public void Save_WritesCleanCrLfJson_AndRoundTrips()
    {
        var root = CreateTempDirectory();
        try
        {
            var config = new AgentConfig
            {
                Agent = new AgentOptions
                {
                    Id = "agent-test",
                    DisplayName = "Test agent",
                    StateDirectory = Path.Combine(root, "state"),
                    Tray = new TrayOptions { Enabled = true }
                },
                Sinks = new Dictionary<string, SinkConfig>(StringComparer.OrdinalIgnoreCase)
                {
                    ["local"] = new SinkConfig
                    {
                        Type = "wsServer",
                        Bind = "127.0.0.1",
                        Port = 18080,
                        AllowedOrigins = ["http://localhost:5173"],
                        Token = string.Empty
                    },
                    ["center"] = new SinkConfig
                    {
                        Type = "wsClient",
                        Url = "ws://127.0.0.1:18081/device",
                        ReconnectMinSeconds = 1,
                        ReconnectMaxSeconds = 30,
                        SpoolSegmentMegabytes = 64,
                        SpoolMaxMegabytes = 1024
                    },
                    ["mq"] = new SinkConfig
                    {
                        Type = "rabbitmq",
                        Host = "mq.example.com",
                        Port = 5672,
                        VirtualHost = "/",
                        UserName = "producer",
                        Password = "secret",
                        Exchange = "sim.device-agent",
                        ExchangeType = "topic",
                        RoutingKey = "device.{agentId}.{deviceId}"
                    },
                    ["events"] = new SinkConfig
                    {
                        Type = "kafka",
                        BootstrapServers = "kafka-1.example.com:9092,kafka-2.example.com:9092",
                        Topic = "devices.{deviceId}",
                        MessageKey = "{agentId}:{deviceId}",
                        SecurityProtocol = "saslSsl",
                        SaslMechanism = "scramSha256",
                        SaslUsername = "producer",
                        SaslPassword = "secret",
                        SslCaLocation = "C:/certs/ca.pem"
                    }
                },
                Devices =
                [
                    new DeviceConfig
                    {
                        DeviceId = "DS-001",
                        Type = "tcp",
                        DeviceName = "Die shear tester",
                        Host = "192.168.1.50",
                        Port = "5000",
                        ReconnectMinSeconds = 1,
                        ReconnectMaxSeconds = 30,
                        QueueCapacity = 1024,
                        Codec = new CodecConfig
                        {
                            Type = "delimiter",
                            Delimiter = "\r\n",
                            Encoding = "ascii",
                            Parse = new RegexParseOptions
                            {
                                Pattern = "^SHEAR,DIE=(?<dieId>\\S+),FORCE=(?<force>\\d+\\.\\d+)N",
                                IgnoreCase = true
                            },
                            Filter = new RegexParseOptions
                            {
                                Pattern = "^SHEAR,"
                            }
                        },
                        Sinks = ["local", "center", "mq", "events"]
                    }
                ]
            };

            var path = Path.Combine(root, "agent.json");
            AgentConfigWriter.Save(path, config);

            var text = File.ReadAllText(path);
            var withoutCrLf = text.Replace("\r\n", string.Empty, StringComparison.Ordinal);
            Assert.DoesNotContain('\r', withoutCrLf);
            Assert.DoesNotContain('\n', withoutCrLf);

            var bytes = File.ReadAllBytes(path);
            Assert.False(
                bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF,
                "Configuration files are written as UTF-8 without BOM.");

            using var document = JsonDocument.Parse(text);
            var agent = document.RootElement.GetProperty("agent");
            Assert.Equal("state", agent.GetProperty("stateDirectory").GetString());

            var device = document.RootElement.GetProperty("devices")[0];
            Assert.Equal(JsonValueKind.Number, device.GetProperty("port").ValueKind);
            Assert.Equal(5000, device.GetProperty("port").GetInt32());
            Assert.False(device.TryGetProperty("baudRate", out _));
            Assert.Equal("192.168.1.50", device.GetProperty("host").GetString());

            var localSink = document.RootElement.GetProperty("sinks").GetProperty("local");
            Assert.Equal("wsServer", localSink.GetProperty("type").GetString());
            Assert.Equal("127.0.0.1", localSink.GetProperty("bind").GetString());
            Assert.Equal(18080, localSink.GetProperty("port").GetInt32());
            Assert.False(localSink.TryGetProperty("url", out _));
            Assert.False(agent.TryGetProperty("localWs", out _));

            var mqSink = document.RootElement.GetProperty("sinks").GetProperty("mq");
            Assert.Equal("rabbitmq", mqSink.GetProperty("type").GetString());
            Assert.Equal("mq.example.com", mqSink.GetProperty("host").GetString());
            Assert.Equal("device.{agentId}.{deviceId}", mqSink.GetProperty("routingKey").GetString());

            var kafkaSink = document.RootElement.GetProperty("sinks").GetProperty("events");
            Assert.Equal("kafka", kafkaSink.GetProperty("type").GetString());
            Assert.Equal(
                "kafka-1.example.com:9092,kafka-2.example.com:9092",
                kafkaSink.GetProperty("bootstrapServers").GetString());
            Assert.Equal("devices.{deviceId}", kafkaSink.GetProperty("topic").GetString());
            Assert.Equal("saslSsl", kafkaSink.GetProperty("securityProtocol").GetString());
            Assert.Equal("scramSha256", kafkaSink.GetProperty("saslMechanism").GetString());
            Assert.False(kafkaSink.TryGetProperty("messageKey", out _));

            var loaded = AgentConfigLoader.Load(path);
            Assert.Empty(AgentConfigValidator.Validate(loaded));
            Assert.Equal(4, loaded.Sinks.Count);
            Assert.Equal("DS-001", Assert.Single(loaded.Devices).DeviceId);
            Assert.Equal("5000", loaded.Devices[0].Port);
            Assert.Equal("127.0.0.1", loaded.Sinks["local"].Bind);
            Assert.Equal(18080, loaded.Sinks["local"].Port);
            Assert.Equal("mq.example.com", loaded.Sinks["mq"].Host);
            Assert.Equal("secret", loaded.Sinks["mq"].Password);
            Assert.Equal(
                "kafka-1.example.com:9092,kafka-2.example.com:9092",
                loaded.Sinks["events"].BootstrapServers);
            Assert.Equal("producer", loaded.Sinks["events"].SaslUsername);
            Assert.Equal("secret", loaded.Sinks["events"].SaslPassword);
            Assert.Equal("C:/certs/ca.pem", loaded.Sinks["events"].SslCaLocation);
            Assert.Equal(
                "^SHEAR,DIE=(?<dieId>\\S+),FORCE=(?<force>\\d+\\.\\d+)N",
                loaded.Devices[0].Codec.Parse?.Pattern);
            Assert.Equal("^SHEAR,", loaded.Devices[0].Codec.Filter?.Pattern);
            Assert.Equal("^SHEAR,", device.GetProperty("codec").GetProperty("filter").GetProperty("pattern").GetString());
            Assert.True(loaded.Devices[0].Codec.Parse?.IgnoreCase);
            Assert.True(device.GetProperty("codec").GetProperty("parse").GetProperty("ignoreCase").GetBoolean());
            Assert.False(device.GetProperty("codec").GetProperty("filter").TryGetProperty("ignoreCase", out _));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    private static string CreateTempDirectory()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "sim-device-agent-writer-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void DeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // Test cleanup only.
        }
    }
}
