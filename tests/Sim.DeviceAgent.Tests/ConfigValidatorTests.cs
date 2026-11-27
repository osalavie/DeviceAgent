using System.Text.Json;
using Sim.DeviceAgent.Configuration;
using Xunit;

namespace Sim.DeviceAgent.Tests;

public sealed class ConfigValidatorTests
{
    [Fact]
    public void ValidFileDevice_HasNoErrors()
    {
        var errors = AgentConfigValidator.Validate(CreateValidConfig());

        Assert.Empty(errors);
    }

    [Fact]
    public void ExampleConfig_LoadsAndValidates()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "agent.example.json");

        var config = AgentConfigLoader.Load(path);
        var errors = AgentConfigValidator.Validate(config);

        Assert.Empty(errors);
    }

    [Fact]
    public void DuplicateDeviceId_IsRejected()
    {
        var config = CreateValidConfig();
        config.Devices.Add(new DeviceConfig
        {
            DeviceId = "PT-001",
            Type = "file",
            Directory = "./in-2",
            Pattern = "*.csv",
            ArchiveDirectory = "./archive-2",
            ErrorDirectory = "./error-2",
            Codec = new CodecConfig { Type = "csv" },
            Sinks = ["local"]
        });

        var errors = AgentConfigValidator.Validate(config);

        Assert.Contains(errors, error => error.Contains("Duplicate deviceId", StringComparison.Ordinal));
    }

    [Fact]
    public void UnknownSink_IsRejected()
    {
        var config = CreateValidConfig();
        config.Devices[0].Sinks = ["missing"];

        var errors = AgentConfigValidator.Validate(config);

        Assert.Contains(errors, error => error.Contains("unknown sink", StringComparison.Ordinal));
    }

    [Fact]
    public void UnsupportedTransportType_IsRejected()
    {
        var config = CreateValidConfig();
        config.Devices[0].Type = "modbus";

        var errors = AgentConfigValidator.Validate(config);

        Assert.Contains(errors, error => error.Contains("unsupported type", StringComparison.Ordinal));
    }

    [Fact]
    public void CsvCodec_IsRejectedForSerialDevice()
    {
        var config = CreateValidConfig();
        config.Devices[0].Type = "serial";
        config.Devices[0].Port = "COM9";

        var errors = AgentConfigValidator.Validate(config);

        Assert.Contains(errors, error => error.Contains("only for file devices", StringComparison.Ordinal));
    }

    [Fact]
    public void InvalidRegex_IsRejected()
    {
        var config = CreateValidConfig();
        config.Devices[0].Type = "serial";
        config.Devices[0].Port = "COM9";
        config.Devices[0].Codec = new CodecConfig
        {
            Type = "delimiter",
            Delimiter = "\r\n",
            Parse = new RegexParseOptions { Pattern = "(" }
        };

        var errors = AgentConfigValidator.Validate(config);

        Assert.Contains(errors, error => error.Contains("regex", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void InvalidFilterRegex_IsRejected()
    {
        var config = CreateValidConfig();
        config.Devices[0].Type = "serial";
        config.Devices[0].Port = "COM9";
        config.Devices[0].Codec = new CodecConfig
        {
            Type = "delimiter",
            Delimiter = "\r\n",
            Filter = new RegexParseOptions { Pattern = "(" }
        };

        var errors = AgentConfigValidator.Validate(config);

        Assert.Contains(errors, error => error.Contains("regex filter", StringComparison.Ordinal));
    }

    [Fact]
    public void NonLoopbackBind_IsRejected()
    {
        var config = CreateValidConfig();
        config.Sinks["local"].Bind = "0.0.0.0";

        var errors = AgentConfigValidator.Validate(config);

        Assert.Contains(errors, error => error.Contains("loopback", StringComparison.Ordinal));
    }

    [Fact]
    public void WsServerSink_RequiresBindAndPort()
    {
        var config = CreateValidConfig();
        config.Sinks["local"].Bind = null;
        config.Sinks["local"].Port = 0;

        var errors = AgentConfigValidator.Validate(config);

        Assert.Contains(errors, error => error.Contains("requires bind", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("port must be between", StringComparison.Ordinal));
    }

    [Fact]
    public void DuplicateWsServerEndpoint_IsRejected()
    {
        var config = CreateValidConfig();
        config.Sinks["extra"] = new SinkConfig
        {
            Type = "wsServer",
            Bind = "127.0.0.1",
            Port = 18080
        };
        config.Devices[0].Sinks = ["local", "extra"];

        var errors = AgentConfigValidator.Validate(config);

        Assert.Contains(errors, error => error.Contains("reuses listen endpoint", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidCenterSink_HasNoErrors()
    {
        var config = CreateValidConfig();
        config.Sinks["center"] = new SinkConfig
        {
            Type = "wsClient",
            Url = "wss://center.example.com/device-gateway",
            Token = "center-token"
        };
        config.Devices[0].Sinks = ["local", "center"];

        var errors = AgentConfigValidator.Validate(config);

        Assert.Empty(errors);
    }

    [Fact]
    public void CenterSink_RequiresWebSocketUrl_ButTokenIsOptional()
    {
        var config = CreateValidConfig();
        config.Sinks["center"] = new SinkConfig
        {
            Type = "wsClient",
            Url = "https://center.example.com/device-gateway",
            Token = string.Empty
        };

        var errors = AgentConfigValidator.Validate(config);

        Assert.Contains(errors, error => error.Contains("ws:// or wss://", StringComparison.Ordinal));
        Assert.DoesNotContain(errors, error => error.Contains("requires token", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ValidRabbitMqSink_HasNoErrors()
    {
        var config = CreateValidConfig();
        config.Sinks["mq"] = new SinkConfig
        {
            Type = "rabbitmq",
            Host = "mq.example.com",
            Port = 5672,
            VirtualHost = "/",
            UserName = "producer",
            Password = "secret",
            Exchange = "sim.device-agent",
            ExchangeType = "topic",
            RoutingKey = "device.{deviceId}"
        };
        config.Devices[0].Sinks = ["local", "mq"];

        var errors = AgentConfigValidator.Validate(config);

        Assert.Empty(errors);
    }

    [Fact]
    public void RabbitMqSink_RequiresExchangeRoutingKeyAndKnownExchangeType()
    {
        var config = CreateValidConfig();
        config.Sinks["mq"] = new SinkConfig
        {
            Type = "rabbitmq",
            Host = "",
            Port = 0,
            Exchange = "",
            ExchangeType = "unknown",
            RoutingKey = ""
        };

        var errors = AgentConfigValidator.Validate(config);

        Assert.Contains(errors, error => error.Contains("requires host", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("port must be between", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("requires exchange", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("exchangeType", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("requires routingKey", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidKafkaSink_HasNoErrors()
    {
        var config = CreateValidConfig();
        config.Sinks["events"] = new SinkConfig
        {
            Type = "kafka",
            BootstrapServers = "kafka-1.example.com:9092,kafka-2.example.com:9092",
            Topic = "devices.{deviceId}",
            MessageKey = "{agentId}:{deviceId}"
        };
        config.Devices[0].Sinks = ["local", "events"];

        var errors = AgentConfigValidator.Validate(config);

        Assert.Empty(errors);
    }

    [Fact]
    public void KafkaSink_RequiresBootstrapServersTopicAndValidSettings()
    {
        var config = CreateValidConfig();
        config.Sinks["events"] = new SinkConfig
        {
            Type = "kafka",
            BootstrapServers = "bad host:0",
            Topic = "",
            SecurityProtocol = "gssapi",
            SaslMechanism = "kerberos"
        };

        var errors = AgentConfigValidator.Validate(config);

        Assert.Contains(errors, error => error.Contains("invalid entry", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("requires topic", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("securityProtocol must be", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("saslMechanism must be", StringComparison.Ordinal));
    }

    [Fact]
    public void KafkaSink_SaslProtocol_RequiresUserName()
    {
        var config = CreateValidConfig();
        config.Sinks["events"] = new SinkConfig
        {
            Type = "kafka",
            BootstrapServers = "kafka.example.com:9093",
            Topic = "devices",
            SecurityProtocol = "saslSsl",
            SaslMechanism = "scram-sha-256",
            SaslUsername = ""
        };

        var errors = AgentConfigValidator.Validate(config);

        Assert.Contains(errors, error => error.Contains("requires saslUsername", StringComparison.Ordinal));
    }

    [Fact]
    public void LegacyLocalWs_IsMigratedIntoWsServerSink()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "sim-device-agent-legacy-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            var path = Path.Combine(root, "agent.json");
            File.WriteAllText(path, """
                {
                  "agent": {
                    "id": "agent-test",
                    "localWs": {
                      "enabled": true,
                      "bind": "127.0.0.1",
                      "port": 18090,
                      "allowedOrigins": ["http://localhost:5173"],
                      "token": "legacy-token"
                    }
                  },
                  "sinks": { "local": { "type": "wsServer" } },
                  "devices": [
                    {
                      "deviceId": "PT-001",
                      "type": "file",
                      "directory": "./in",
                      "pattern": "*.csv",
                      "archiveDirectory": "./archive",
                      "errorDirectory": "./error",
                      "codec": { "type": "csv" },
                      "sinks": ["local"]
                    }
                  ]
                }
                """);

            var config = AgentConfigLoader.Load(path);

            Assert.Null(config.Agent.LocalWs);
            var sink = config.Sinks["local"];
            Assert.Equal("127.0.0.1", sink.Bind);
            Assert.Equal(18090, sink.Port);
            Assert.Equal("legacy-token", sink.Token);
            Assert.Equal(["http://localhost:5173"], sink.AllowedOrigins);
            Assert.Empty(AgentConfigValidator.Validate(config));
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
                // Test cleanup only.
            }
        }
    }

    [Fact]
    public void UnsafeDeviceId_IsRejected()
    {
        var config = CreateValidConfig();
        config.Devices[0].DeviceId = "../PT-001";

        var errors = AgentConfigValidator.Validate(config);

        Assert.Contains(errors, error => error.Contains("id must use", StringComparison.Ordinal));
    }

    [Fact]
    public void NoEnabledDevices_IsRejected()
    {
        var config = CreateValidConfig();
        config.Devices.Clear();

        var errors = AgentConfigValidator.Validate(config);

        Assert.Contains(errors, error => error.Contains("At least one enabled device", StringComparison.Ordinal));
    }

    private static AgentConfig CreateValidConfig()
    {
        return new AgentConfig
        {
            Agent = new AgentOptions
            {
                Id = "agent-test",
                DisplayName = "Test agent"
            },
            Sinks = new Dictionary<string, SinkConfig>(StringComparer.OrdinalIgnoreCase)
            {
                ["local"] = new SinkConfig
                {
                    Type = "wsServer",
                    Bind = "127.0.0.1",
                    Port = 18080,
                    Token = "test-token",
                    AllowedOrigins = ["http://localhost:5173"]
                }
            },
            Devices =
            [
                new DeviceConfig
                {
                    DeviceId = "PT-001",
                    Type = "file",
                    DeviceName = "Pull tester file",
                    Directory = "./in",
                    Pattern = "*.csv",
                    StableSeconds = 1,
                    PollIntervalSeconds = 1,
                    ArchiveDirectory = "./archive",
                    ErrorDirectory = "./error",
                    Codec = new CodecConfig
                    {
                        Type = "csv",
                        CsvDelimiter = ",",
                        HasHeader = true,
                        Columns = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["sampleId"] = "Sample",
                            ["force"] = "ForceN"
                        }
                    },
                    Sinks = ["local"]
                }
            ]
        };
    }
}
