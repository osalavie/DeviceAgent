using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sim.DeviceAgent.Configuration;

public sealed class AgentConfig
{
    public AgentOptions Agent { get; set; } = new();

    public Dictionary<string, SinkConfig> Sinks { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public List<DeviceConfig> Devices { get; set; } = [];
}

public sealed class AgentOptions
{
    public string Id { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string StateDirectory { get; set; } = "data";

    public TrayOptions Tray { get; set; } = new();

    // Legacy agent.localWs block; migrated into wsServer sinks on load and never written back.
    [JsonPropertyName("localWs")]
    public LegacyLocalWsOptions? LocalWs { get; set; }
}

public sealed class TrayOptions
{
    public bool Enabled { get; set; } = true;
}

public sealed class LegacyLocalWsOptions
{
    public bool Enabled { get; set; } = true;

    public string Bind { get; set; } = "127.0.0.1";

    public int Port { get; set; } = 18080;

    public List<string> AllowedOrigins { get; set; } = [];

    public string Token { get; set; } = string.Empty;
}

public sealed class DeviceConfig
{
    public string DeviceId { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    public string Type { get; set; } = string.Empty;

    public string DeviceName { get; set; } = string.Empty;

    [JsonConverter(typeof(FlexibleStringJsonConverter))]
    public string? Port { get; set; }

    public int BaudRate { get; set; } = 9600;

    public int DataBits { get; set; } = 8;

    public string Parity { get; set; } = "None";

    public string StopBits { get; set; } = "One";

    public bool DtrEnable { get; set; }

    public bool RtsEnable { get; set; }

    public int ReadTimeoutMs { get; set; } = 200;

    public string? Host { get; set; }

    public int ReconnectMinSeconds { get; set; } = 1;

    public int ReconnectMaxSeconds { get; set; } = 30;

    public string? Directory { get; set; }

    public string Pattern { get; set; } = "*.*";

    public double StableSeconds { get; set; } = 5;

    public double PollIntervalSeconds { get; set; } = 5;

    public string? ArchiveDirectory { get; set; }

    public string? ErrorDirectory { get; set; }

    public CodecConfig Codec { get; set; } = new();

    public List<string> Sinks { get; set; } = [];

    public int QueueCapacity { get; set; } = 1024;
}

public sealed class SinkConfig
{
    public string Type { get; set; } = string.Empty;

    // wsClient target.
    public string Url { get; set; } = string.Empty;

    // wsServer listen endpoint and hello authentication.
    public string? Bind { get; set; }

    public string Token { get; set; } = string.Empty;

    public List<string> AllowedOrigins { get; set; } = [];

    // RabbitMQ producer.
    public string? Host { get; set; }

    public int Port { get; set; }

    public string VirtualHost { get; set; } = "/";

    public string UserName { get; set; } = "guest";

    public string Password { get; set; } = string.Empty;

    public string Exchange { get; set; } = string.Empty;

    public string ExchangeType { get; set; } = "topic";

    public string RoutingKey { get; set; } = "device.{deviceId}";

    public bool Durable { get; set; } = true;

    public bool Persistent { get; set; } = true;

    // Kafka producer.
    public string? BootstrapServers { get; set; }

    public string Topic { get; set; } = string.Empty;

    public string MessageKey { get; set; } = "{agentId}:{deviceId}";

    public string SecurityProtocol { get; set; } = "plaintext";

    public string SaslMechanism { get; set; } = "plain";

    public string SaslUsername { get; set; } = string.Empty;

    public string SaslPassword { get; set; } = string.Empty;

    public string? SslCaLocation { get; set; }

    public int ReconnectMinSeconds { get; set; } = 1;

    public int ReconnectMaxSeconds { get; set; } = 30;

    public int SpoolSegmentMegabytes { get; set; } = 64;

    public int SpoolMaxMegabytes { get; set; } = 1024;

    public string? SpoolDirectory { get; set; }
}

public sealed class CodecConfig
{
    public string Type { get; set; } = "delimiter";

    public string Delimiter { get; set; } = "\r\n";

    public string Encoding { get; set; } = "utf-8";

    public RegexParseOptions? Parse { get; set; }

    public string CsvDelimiter { get; set; } = ",";

    public bool HasHeader { get; set; } = true;

    public Dictionary<string, string> Columns { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public int MaxFrameBytes { get; set; } = 65536;
}

public sealed class RegexParseOptions
{
    public string Type { get; set; } = "regex";

    public string Pattern { get; set; } = string.Empty;
}

public sealed class FlexibleStringJsonConverter : JsonConverter<string>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Number when reader.TryGetInt32(out var number) => number.ToString(System.Globalization.CultureInfo.InvariantCulture),
            JsonTokenType.Null => null,
            _ => throw new JsonException("Expected a string or integer.")
        };
    }

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value);
    }
}
