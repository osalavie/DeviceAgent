using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sim.DeviceAgent.Configuration;

public static class AgentConfigWriter
{
    private const int DefaultQueueCapacity = 1024;
    private const int DefaultMaxFrameBytes = 65536;

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static void Save(string path, AgentConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var fullPath = Path.GetFullPath(path);
        var baseDirectory = Path.GetDirectoryName(fullPath)
                            ?? throw new InvalidOperationException($"Configuration path '{path}' has no directory.");

        Directory.CreateDirectory(baseDirectory);

        var json = Serialize(config, baseDirectory) + "\r\n";

        var temporary = fullPath + ".tmp";
        File.WriteAllText(temporary, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(temporary, fullPath, overwrite: true);
    }

    public static string Serialize(AgentConfig config, string baseDirectory)
    {
        ArgumentNullException.ThrowIfNull(config);

        var model = CreateFileModel(config, baseDirectory);
        return JsonSerializer.Serialize(model, Options).ReplaceLineEndings("\r\n");
    }

    private static AgentFileModel CreateFileModel(AgentConfig config, string baseDirectory)
    {
        var agent = config.Agent ?? new AgentOptions();
        var tray = agent.Tray ?? new TrayOptions();

        return new AgentFileModel
        {
            Agent = new AgentFileOptions
            {
                Id = agent.Id,
                DisplayName = Optional(agent.DisplayName),
                StateDirectory = ToConfigRelativePath(agent.StateDirectory, baseDirectory) ?? "data",
                Tray = new TrayFileOptions { Enabled = tray.Enabled }
            },
            Sinks = (config.Sinks ?? new Dictionary<string, SinkConfig>())
                .Where(pair => pair.Value is not null)
                .ToDictionary(
                    pair => pair.Key,
                    pair => CreateSinkModel(pair.Value, baseDirectory),
                    StringComparer.OrdinalIgnoreCase),
            Devices = (config.Devices ?? [])
                .Select(device => CreateDeviceModel(device, baseDirectory))
                .ToList()
        };
    }

    private static SinkFileModel CreateSinkModel(SinkConfig sink, string baseDirectory)
    {
        var type = sink.Type.ToLowerInvariant();
        var model = new SinkFileModel
        {
            Type = type switch
            {
                "wsserver" => "wsServer",
                "wsclient" => "wsClient",
                _ => type
            }
        };

        switch (type)
        {
            case "wsserver":
                model.Bind = sink.Bind;
                model.Port = sink.Port;
                model.AllowedOrigins = sink.AllowedOrigins is { Count: > 0 }
                    ? sink.AllowedOrigins.ToList()
                    : null;
                model.Token = Optional(sink.Token);
                break;

            case "rabbitmq":
                model.Host = sink.Host;
                model.Port = sink.Port;
                model.VirtualHost = Optional(sink.VirtualHost, "/");
                model.UserName = Optional(sink.UserName, "guest");
                model.Password = Optional(sink.Password);
                model.Exchange = sink.Exchange;
                model.ExchangeType = Optional(sink.ExchangeType, "topic");
                model.RoutingKey = Optional(sink.RoutingKey, "device.{deviceId}");
                model.Durable = sink.Durable ? null : false;
                model.Persistent = sink.Persistent ? null : false;
                AppendReliableSinkFields(model, sink, baseDirectory);
                break;

            case "kafka":
                model.BootstrapServers = sink.BootstrapServers;
                model.Topic = sink.Topic;
                model.MessageKey = Optional(sink.MessageKey, "{agentId}:{deviceId}");
                model.SecurityProtocol = Optional(sink.SecurityProtocol, "plaintext");
                model.SaslMechanism = Optional(sink.SaslMechanism, "plain");
                model.SaslUsername = Optional(sink.SaslUsername);
                model.SaslPassword = Optional(sink.SaslPassword);
                model.SslCaLocation = Optional(sink.SslCaLocation);
                AppendReliableSinkFields(model, sink, baseDirectory);
                break;

            default:
                model.Url = sink.Url;
                model.Token = Optional(sink.Token);
                AppendReliableSinkFields(model, sink, baseDirectory);
                break;
        }

        return model;
    }

    private static void AppendReliableSinkFields(SinkFileModel model, SinkConfig sink, string baseDirectory)
    {
        model.ReconnectMinSeconds = sink.ReconnectMinSeconds;
        model.ReconnectMaxSeconds = sink.ReconnectMaxSeconds;
        model.SpoolSegmentMegabytes = sink.SpoolSegmentMegabytes;
        model.SpoolMaxMegabytes = sink.SpoolMaxMegabytes;
        model.SpoolDirectory = ToConfigRelativePath(sink.SpoolDirectory, baseDirectory);
    }

    private static DeviceFileModel CreateDeviceModel(DeviceConfig device, string baseDirectory)
    {
        var type = device.Type.ToLowerInvariant();
        var model = new DeviceFileModel
        {
            DeviceId = device.DeviceId,
            Enabled = device.Enabled ? null : false,
            Type = type,
            DeviceName = device.DeviceName,
            QueueCapacity = device.QueueCapacity == DefaultQueueCapacity ? null : device.QueueCapacity,
            Sinks = device.Sinks?.ToList() ?? [],
            Codec = CreateCodecModel(device.Codec)
        };

        switch (type)
        {
            case "serial":
                model.Port = device.Port;
                model.BaudRate = device.BaudRate;
                model.DataBits = device.DataBits;
                model.Parity = device.Parity;
                model.StopBits = device.StopBits;
                model.DtrEnable = device.DtrEnable ? true : null;
                model.RtsEnable = device.RtsEnable ? true : null;
                model.ReadTimeoutMs = device.ReadTimeoutMs;
                break;

            case "tcp":
                model.Host = device.Host;
                model.Port = int.TryParse(device.Port, out var tcpPort) ? tcpPort : device.Port;
                model.ReconnectMinSeconds = device.ReconnectMinSeconds;
                model.ReconnectMaxSeconds = device.ReconnectMaxSeconds;
                break;

            case "file":
                model.Directory = ToConfigRelativePath(device.Directory, baseDirectory);
                model.Pattern = device.Pattern;
                model.StableSeconds = device.StableSeconds;
                model.PollIntervalSeconds = device.PollIntervalSeconds;
                model.ArchiveDirectory = ToConfigRelativePath(device.ArchiveDirectory, baseDirectory);
                model.ErrorDirectory = ToConfigRelativePath(device.ErrorDirectory, baseDirectory);
                break;
        }

        return model;
    }

    private static CodecFileModel? CreateCodecModel(CodecConfig? codec)
    {
        if (codec is null)
        {
            return null;
        }

        var type = codec.Type.ToLowerInvariant();
        var model = new CodecFileModel
        {
            Type = type,
            Encoding = Optional(codec.Encoding, "utf-8"),
            MaxFrameBytes = codec.MaxFrameBytes == DefaultMaxFrameBytes ? null : codec.MaxFrameBytes
        };

        switch (type)
        {
            case "delimiter":
                model.Delimiter = codec.Delimiter;
                if (!string.IsNullOrWhiteSpace(codec.Parse?.Pattern))
                {
                    model.Parse = CreateRegexModel(codec.Parse);
                }

                if (!string.IsNullOrWhiteSpace(codec.Filter?.Pattern))
                {
                    model.Filter = CreateRegexModel(codec.Filter);
                }

                break;

            case "csv":
                model.CsvDelimiter = Optional(codec.CsvDelimiter, ",");
                model.HasHeader = codec.HasHeader ? null : false;
                model.Columns = codec.Columns is { Count: > 0 }
                    ? new Dictionary<string, string>(codec.Columns, StringComparer.OrdinalIgnoreCase)
                    : null;
                break;
        }

        return model;
    }

    private static ParseFileModel CreateRegexModel(RegexParseOptions options)
        => new()
        {
            Pattern = options.Pattern,
            IgnoreCase = options.IgnoreCase ? true : null
        };

    private static string? Optional(string? value, string? defaultValue = null)
        => string.IsNullOrWhiteSpace(value) || string.Equals(value, defaultValue, StringComparison.OrdinalIgnoreCase)
            ? null
            : value;

    private static string? ToConfigRelativePath(string? path, string baseDirectory)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        var fullPath = Path.GetFullPath(path);
        var relative = Path.GetRelativePath(baseDirectory, fullPath);
        if (relative == ".." ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            Path.IsPathRooted(relative))
        {
            return fullPath;
        }

        return relative.Replace('\\', '/');
    }

    private sealed class AgentFileModel
    {
        public AgentFileOptions Agent { get; set; } = new();

        public Dictionary<string, SinkFileModel> Sinks { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        public List<DeviceFileModel> Devices { get; set; } = [];
    }

    private sealed class AgentFileOptions
    {
        public string Id { get; set; } = string.Empty;

        public string? DisplayName { get; set; }

        public string StateDirectory { get; set; } = "data";

        public TrayFileOptions Tray { get; set; } = new();
    }

    private sealed class TrayFileOptions
    {
        public bool Enabled { get; set; } = true;
    }

    private sealed class SinkFileModel
    {
        public string Type { get; set; } = "wsServer";

        public string? Bind { get; set; }

        public int? Port { get; set; }

        public List<string>? AllowedOrigins { get; set; }

        public string? Url { get; set; }

        public string? Token { get; set; }

        public string? Host { get; set; }

        public string? VirtualHost { get; set; }

        public string? UserName { get; set; }

        public string? Password { get; set; }

        public string? Exchange { get; set; }

        public string? ExchangeType { get; set; }

        public string? RoutingKey { get; set; }

        public bool? Durable { get; set; }

        public bool? Persistent { get; set; }

        public string? BootstrapServers { get; set; }

        public string? Topic { get; set; }

        public string? MessageKey { get; set; }

        public string? SecurityProtocol { get; set; }

        public string? SaslMechanism { get; set; }

        public string? SaslUsername { get; set; }

        public string? SaslPassword { get; set; }

        public string? SslCaLocation { get; set; }

        public int? ReconnectMinSeconds { get; set; }

        public int? ReconnectMaxSeconds { get; set; }

        public int? SpoolSegmentMegabytes { get; set; }

        public int? SpoolMaxMegabytes { get; set; }

        public string? SpoolDirectory { get; set; }
    }

    private sealed class DeviceFileModel
    {
        public string DeviceId { get; set; } = string.Empty;

        public bool? Enabled { get; set; }

        public string Type { get; set; } = string.Empty;

        public string DeviceName { get; set; } = string.Empty;

        public int? QueueCapacity { get; set; }

        public List<string> Sinks { get; set; } = [];

        public object? Port { get; set; }

        public int? BaudRate { get; set; }

        public int? DataBits { get; set; }

        public string? Parity { get; set; }

        public string? StopBits { get; set; }

        public bool? DtrEnable { get; set; }

        public bool? RtsEnable { get; set; }

        public int? ReadTimeoutMs { get; set; }

        public string? Host { get; set; }

        public int? ReconnectMinSeconds { get; set; }

        public int? ReconnectMaxSeconds { get; set; }

        public string? Directory { get; set; }

        public string? Pattern { get; set; }

        public double? StableSeconds { get; set; }

        public double? PollIntervalSeconds { get; set; }

        public string? ArchiveDirectory { get; set; }

        public string? ErrorDirectory { get; set; }

        public CodecFileModel? Codec { get; set; }
    }

    private sealed class CodecFileModel
    {
        public string Type { get; set; } = "delimiter";

        public string? Delimiter { get; set; }

        public string? Encoding { get; set; }

        public int? MaxFrameBytes { get; set; }

        public ParseFileModel? Parse { get; set; }

        public ParseFileModel? Filter { get; set; }

        public string? CsvDelimiter { get; set; }

        public bool? HasHeader { get; set; }

        public Dictionary<string, string>? Columns { get; set; }
    }

    private sealed class ParseFileModel
    {
        public string Pattern { get; set; } = string.Empty;

        public bool? IgnoreCase { get; set; }
    }
}
