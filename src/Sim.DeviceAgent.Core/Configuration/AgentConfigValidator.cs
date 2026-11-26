using System.Globalization;
using System.IO.Ports;
using System.Net;
using System.Text.RegularExpressions;
using Sim.DeviceAgent.Codecs;

namespace Sim.DeviceAgent.Configuration;

public static class AgentConfigValidator
{
    private static readonly HashSet<string> SupportedTransportTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "serial",
        "tcp",
        "file"
    };

    private static readonly HashSet<string> SupportedCodecTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "delimiter",
        "passthrough",
        "csv"
    };

    private static readonly HashSet<string> SupportedSinkTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "wsServer",
        "wsClient",
        "rabbitmq",
        "kafka"
    };

    private static readonly HashSet<string> SupportedExchangeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "topic",
        "direct",
        "fanout",
        "headers"
    };

    private static readonly HashSet<string> SupportedKafkaSecurityProtocols = new(StringComparer.OrdinalIgnoreCase)
    {
        "plaintext",
        "ssl",
        "saslplaintext",
        "saslssl"
    };

    private static readonly HashSet<string> SupportedKafkaSaslMechanisms = new(StringComparer.OrdinalIgnoreCase)
    {
        "plain",
        "scramsha256",
        "scramsha512"
    };

    public static IReadOnlyList<string> Validate(AgentConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var errors = new List<string>();

        config.Agent ??= new AgentOptions();
        config.Sinks ??= new Dictionary<string, SinkConfig>(StringComparer.OrdinalIgnoreCase);
        config.Devices ??= [];

        ValidateAgent(config.Agent, errors);

        var wsServerEndpoints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var sink in config.Sinks)
        {
            if (string.IsNullOrWhiteSpace(sink.Key))
            {
                errors.Add("Sink id cannot be empty.");
            }

            if (sink.Value is null)
            {
                errors.Add($"Sink '{sink.Key}' is null.");
                continue;
            }

            if (!string.IsNullOrWhiteSpace(sink.Key) && !IsSafePathSegment(sink.Key))
            {
                errors.Add($"Sink id '{sink.Key}' must use only letters, digits, '.', '_' or '-'.");
            }

            switch (sink.Value.Type.ToLowerInvariant())
            {
                case "wsserver":
                    ValidateWsServerSink(sink.Key, sink.Value, wsServerEndpoints, errors);
                    break;

                case "wsclient":
                    ValidateCenterSink(sink.Key, sink.Value, errors);
                    break;

                case "rabbitmq":
                    ValidateRabbitMqSink(sink.Key, sink.Value, errors);
                    break;

                case "kafka":
                    ValidateKafkaSink(sink.Key, sink.Value, errors);
                    break;

                default:
                    errors.Add($"Sink '{sink.Key}' uses unsupported type '{sink.Value.Type}'. Supported values: wsServer, wsClient, rabbitmq, kafka.");
                    break;
            }
        }

        var deviceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var enabledDeviceCount = 0;

        foreach (var device in config.Devices)
        {
            if (device is null)
            {
                errors.Add("Device entry is null.");
                continue;
            }

            if (!device.Enabled)
            {
                continue;
            }

            enabledDeviceCount++;
            var label = string.IsNullOrWhiteSpace(device.DeviceId) ? "<unnamed>" : device.DeviceId;

            if (string.IsNullOrWhiteSpace(device.DeviceId))
            {
                errors.Add("Device id cannot be empty.");
            }
            else if (!IsSafePathSegment(device.DeviceId))
            {
                errors.Add($"Device '{label}' id must use only letters, digits, '.', '_' or '-'.");
            }
            else if (!deviceIds.Add(device.DeviceId))
            {
                errors.Add($"Duplicate deviceId '{device.DeviceId}'.");
            }

            ValidateDeviceTransport(device, label, errors);
            ValidateDeviceCodec(device, label, errors);

            device.Sinks ??= [];
            if (device.Sinks.Count == 0)
            {
                errors.Add($"Device '{label}' must reference at least one sink.");
            }

            foreach (var sinkRef in device.Sinks)
            {
                if (!config.Sinks.ContainsKey(sinkRef))
                {
                    errors.Add($"Device '{label}' references unknown sink '{sinkRef}'.");
                }
            }

            if (device.QueueCapacity < 16 || device.QueueCapacity > 1_000_000)
            {
                errors.Add($"Device '{label}' queueCapacity must be between 16 and 1000000.");
            }
        }

        if (enabledDeviceCount == 0)
        {
            errors.Add("At least one enabled device is required.");
        }

        return errors;
    }

    private static void ValidateAgent(AgentOptions agent, List<string> errors)
    {
        agent.Tray ??= new TrayOptions();

        if (string.IsNullOrWhiteSpace(agent.Id))
        {
            errors.Add("agent.id is required.");
        }
        else if (!IsSafePathSegment(agent.Id))
        {
            errors.Add("agent.id must use only letters, digits, '.', '_' or '-'.");
        }

        if (string.IsNullOrWhiteSpace(agent.StateDirectory))
        {
            errors.Add("agent.stateDirectory is required.");
        }
    }

    private static void ValidateDeviceTransport(DeviceConfig device, string label, List<string> errors)
    {
        if (!SupportedTransportTypes.Contains(device.Type))
        {
            errors.Add($"Device '{label}' uses unsupported type '{device.Type}'. Supported values: serial, tcp, file.");
            return;
        }

        switch (device.Type.ToLowerInvariant())
        {
            case "serial":
                if (string.IsNullOrWhiteSpace(device.Port))
                {
                    errors.Add($"Serial device '{label}' requires port.");
                }

                if (device.BaudRate is < 300 or > 921600)
                {
                    errors.Add($"Serial device '{label}' baudRate must be between 300 and 921600.");
                }

                if (device.DataBits is < 5 or > 8)
                {
                    errors.Add($"Serial device '{label}' dataBits must be between 5 and 8.");
                }

                if (!Enum.TryParse<Parity>(device.Parity, ignoreCase: true, out _))
                {
                    errors.Add($"Serial device '{label}' parity must be None, Odd, Even, Mark or Space.");
                }

                if (!Enum.TryParse<StopBits>(device.StopBits, ignoreCase: true, out _))
                {
                    errors.Add($"Serial device '{label}' stopBits must be One, OnePointFive or Two.");
                }

                if (device.ReadTimeoutMs is < 50 or > 10000)
                {
                    errors.Add($"Serial device '{label}' readTimeoutMs must be between 50 and 10000.");
                }

                break;

            case "tcp":
                if (string.IsNullOrWhiteSpace(device.Host))
                {
                    errors.Add($"TCP device '{label}' requires host.");
                }

                if (!int.TryParse(device.Port, out var tcpPort) || tcpPort is < 1 or > 65535)
                {
                    errors.Add($"TCP device '{label}' requires a valid port between 1 and 65535.");
                }

                if (device.ReconnectMinSeconds < 1 || device.ReconnectMaxSeconds < device.ReconnectMinSeconds)
                {
                    errors.Add($"TCP device '{label}' reconnect range is invalid.");
                }

                break;

            case "file":
                if (string.IsNullOrWhiteSpace(device.Directory))
                {
                    errors.Add($"File device '{label}' requires directory.");
                }

                if (string.IsNullOrWhiteSpace(device.Pattern))
                {
                    errors.Add($"File device '{label}' requires pattern.");
                }

                if (string.IsNullOrWhiteSpace(device.ArchiveDirectory))
                {
                    errors.Add($"File device '{label}' requires archiveDirectory.");
                }

                if (string.IsNullOrWhiteSpace(device.ErrorDirectory))
                {
                    errors.Add($"File device '{label}' requires errorDirectory.");
                }

                if (device.StableSeconds < 0.1)
                {
                    errors.Add($"File device '{label}' stableSeconds must be at least 0.1.");
                }

                if (device.PollIntervalSeconds < 0.1)
                {
                    errors.Add($"File device '{label}' pollIntervalSeconds must be at least 0.1.");
                }

                if (!string.IsNullOrWhiteSpace(device.ArchiveDirectory)
                    && !string.IsNullOrWhiteSpace(device.ErrorDirectory)
                    && string.Equals(
                        Path.GetFullPath(device.ArchiveDirectory),
                        Path.GetFullPath(device.ErrorDirectory),
                        StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add($"File device '{label}' archiveDirectory and errorDirectory must be different.");
                }

                break;
        }
    }

    private static void ValidateDeviceCodec(DeviceConfig device, string label, List<string> errors)
    {
        var codec = device.Codec ??= new CodecConfig();
        if (!SupportedCodecTypes.Contains(codec.Type))
        {
            errors.Add($"Device '{label}' uses unsupported codec '{codec.Type}'. Supported values: delimiter, passthrough, csv.");
            return;
        }

        if (codec.MaxFrameBytes is < 64 or > 4 * 1024 * 1024)
        {
            errors.Add($"Device '{label}' codec.maxFrameBytes must be between 64 and 4194304.");
        }

        try
        {
            _ = EncodingResolver.Resolve(codec.Encoding);
        }
        catch (ArgumentException ex)
        {
            errors.Add($"Device '{label}': {ex.Message}");
        }

        switch (codec.Type.ToLowerInvariant())
        {
            case "delimiter":
                if (string.IsNullOrEmpty(codec.Delimiter))
                {
                    errors.Add($"Device '{label}' delimiter codec requires a non-empty delimiter.");
                }

                if (!string.IsNullOrWhiteSpace(codec.Parse?.Pattern))
                {
                    ValidateRegex(label, codec.Parse.Pattern, errors);
                }

                break;

            case "passthrough":
                break;

            case "csv":
                if (!string.Equals(device.Type, "file", StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add($"Device '{label}' uses csv codec, which is supported only for file devices.");
                }

                if (!codec.HasHeader)
                {
                    errors.Add($"Device '{label}' csv codec requires hasHeader=true in this MVP.");
                }

                codec.CsvDelimiter ??= string.Empty;
                if (codec.CsvDelimiter.Length != 1)
                {
                    errors.Add($"Device '{label}' csv codec requires csvDelimiter to be exactly one character.");
                }

                if (codec.Columns is null)
                {
                    codec.Columns = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                }

                foreach (var column in codec.Columns)
                {
                    if (string.IsNullOrWhiteSpace(column.Key) || string.IsNullOrWhiteSpace(column.Value))
                    {
                        errors.Add($"Device '{label}' csv columns contain an empty key or header name.");
                    }
                }

                break;
        }
    }

    private static void ValidateRegex(string deviceLabel, string pattern, List<string> errors)
    {
        try
        {
            _ = new Regex(pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));
        }
        catch (ArgumentException ex)
        {
            errors.Add($"Device '{deviceLabel}' regex parse pattern is invalid: {ex.Message}");
        }
    }

    private static void ValidateWsServerSink(
        string sinkId,
        SinkConfig sink,
        Dictionary<string, string> endpoints,
        List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(sink.Bind))
        {
            errors.Add($"wsServer sink '{sinkId}' requires bind.");
        }
        else if (!IsLoopback(sink.Bind))
        {
            errors.Add($"wsServer sink '{sinkId}' bind must be a loopback address. Received '{sink.Bind}'.");
        }

        if (sink.Port is < 1 or > 65535)
        {
            errors.Add($"wsServer sink '{sinkId}' port must be between 1 and 65535.");
        }
        else if (!string.IsNullOrWhiteSpace(sink.Bind))
        {
            var endpoint = $"{sink.Bind}:{sink.Port}";
            if (endpoints.TryGetValue(endpoint, out var existing))
            {
                errors.Add($"wsServer sink '{sinkId}' reuses listen endpoint '{endpoint}' already used by sink '{existing}'.");
            }
            else
            {
                endpoints[endpoint] = sinkId;
            }
        }

        sink.AllowedOrigins ??= [];
        foreach (var origin in sink.AllowedOrigins)
        {
            if (!IsValidOrigin(origin))
            {
                errors.Add($"wsServer sink '{sinkId}' allowedOrigins contains an invalid origin '{origin}'.");
            }
        }
    }

    private static void ValidateRabbitMqSink(string sinkId, SinkConfig sink, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(sink.Host))
        {
            errors.Add($"RabbitMQ sink '{sinkId}' requires host.");
        }

        if (sink.Port is < 1 or > 65535)
        {
            errors.Add($"RabbitMQ sink '{sinkId}' port must be between 1 and 65535.");
        }

        if (string.IsNullOrWhiteSpace(sink.VirtualHost))
        {
            errors.Add($"RabbitMQ sink '{sinkId}' requires virtualHost.");
        }

        if (string.IsNullOrWhiteSpace(sink.UserName))
        {
            errors.Add($"RabbitMQ sink '{sinkId}' requires userName.");
        }

        if (string.IsNullOrWhiteSpace(sink.Exchange))
        {
            errors.Add($"RabbitMQ sink '{sinkId}' requires exchange.");
        }

        if (!SupportedExchangeTypes.Contains(sink.ExchangeType ?? string.Empty))
        {
            errors.Add($"RabbitMQ sink '{sinkId}' exchangeType must be topic, direct, fanout or headers.");
        }

        if (string.IsNullOrWhiteSpace(sink.RoutingKey))
        {
            errors.Add($"RabbitMQ sink '{sinkId}' requires routingKey.");
        }

        ValidateReliableSink(sinkId, sink, "RabbitMQ", errors);
    }

    private static void ValidateKafkaSink(string sinkId, SinkConfig sink, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(sink.BootstrapServers))
        {
            errors.Add($"Kafka sink '{sinkId}' requires bootstrapServers.");
        }
        else
        {
            foreach (var server in sink.BootstrapServers.Split(',', StringSplitOptions.TrimEntries))
            {
                if (!IsValidBootstrapServer(server))
                {
                    errors.Add($"Kafka sink '{sinkId}' bootstrapServers contains an invalid entry '{server}'.");
                }
            }
        }

        if (string.IsNullOrWhiteSpace(sink.Topic))
        {
            errors.Add($"Kafka sink '{sinkId}' requires topic.");
        }

        var securityProtocol = NormalizeKafkaSetting(sink.SecurityProtocol);
        if (!SupportedKafkaSecurityProtocols.Contains(securityProtocol))
        {
            errors.Add($"Kafka sink '{sinkId}' securityProtocol must be plaintext, ssl, saslPlaintext or saslSsl.");
        }

        if (!SupportedKafkaSaslMechanisms.Contains(NormalizeKafkaSetting(sink.SaslMechanism)))
        {
            errors.Add($"Kafka sink '{sinkId}' saslMechanism must be plain, scramSha256 or scramSha512.");
        }

        if (securityProtocol is "saslplaintext" or "saslssl" &&
            string.IsNullOrWhiteSpace(sink.SaslUsername))
        {
            errors.Add($"Kafka sink '{sinkId}' requires saslUsername when securityProtocol is SASL.");
        }

        ValidateReliableSink(sinkId, sink, "Kafka", errors);
    }

    private static bool IsValidBootstrapServer(string server)
    {
        if (string.IsNullOrWhiteSpace(server) || server.Any(char.IsWhiteSpace))
        {
            return false;
        }

        if (server.StartsWith('['))
        {
            var closing = server.IndexOf(']');
            if (closing <= 1)
            {
                return false;
            }

            var remainder = server[(closing + 1)..];
            if (remainder.Length == 0)
            {
                return true;
            }

            return remainder.StartsWith(':') &&
                   int.TryParse(
                       remainder[1..],
                       NumberStyles.Integer,
                       CultureInfo.InvariantCulture,
                       out var ipv6Port) &&
                   ipv6Port is >= 1 and <= 65535;
        }

        var colon = server.LastIndexOf(':');
        if (colon < 0)
        {
            return true;
        }

        var host = server[..colon];
        return host.Length > 0 &&
               !host.Contains(':') &&
               int.TryParse(
                   server[(colon + 1)..],
                   NumberStyles.Integer,
                   CultureInfo.InvariantCulture,
                   out var port) &&
               port is >= 1 and <= 65535;
    }

    private static string NormalizeKafkaSetting(string? value)
        => value?.Trim()
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant() ?? string.Empty;

    private static void ValidateCenterSink(string sinkId, SinkConfig sink, List<string> errors)
    {
        if (!Uri.TryCreate(sink.Url, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("ws" or "wss"))
        {
            errors.Add($"Center sink '{sinkId}' requires an absolute ws:// or wss:// url.");
        }

        ValidateReliableSink(sinkId, sink, "Center", errors);
    }

    private static void ValidateReliableSink(string sinkId, SinkConfig sink, string label, List<string> errors)
    {
        if (sink.ReconnectMinSeconds < 1 || sink.ReconnectMaxSeconds < sink.ReconnectMinSeconds)
        {
            errors.Add($"{label} sink '{sinkId}' reconnect range is invalid.");
        }

        if (sink.SpoolSegmentMegabytes is < 1 or > 1024)
        {
            errors.Add($"{label} sink '{sinkId}' spoolSegmentMegabytes must be between 1 and 1024.");
        }

        if (sink.SpoolMaxMegabytes < sink.SpoolSegmentMegabytes || sink.SpoolMaxMegabytes > 1_048_576)
        {
            errors.Add($"{label} sink '{sinkId}' spoolMaxMegabytes must be at least spoolSegmentMegabytes and at most 1048576.");
        }
    }

    private static bool IsSafePathSegment(string value)
    {
        return value.Length > 0 &&
               value is not "." and not ".." &&
               value.All(character =>
                   char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-');
    }

    private static bool IsLoopback(string? bind)
    {
        if (string.IsNullOrWhiteSpace(bind))
        {
            return false;
        }

        if (string.Equals(bind, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IPAddress.TryParse(bind, out var address) && IPAddress.IsLoopback(address);
    }

    private static bool IsValidOrigin(string? origin)
    {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri))
        {
            return false;
        }

        return uri.Scheme is "http" or "https"
               && string.IsNullOrEmpty(uri.Query)
               && uri.AbsolutePath == "/";
    }
}
