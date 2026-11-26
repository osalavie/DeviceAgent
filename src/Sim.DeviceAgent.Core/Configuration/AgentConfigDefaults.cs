namespace Sim.DeviceAgent.Configuration;

public static class AgentConfigDefaults
{
    public static AgentConfig Create(string? machineName, string baseDirectory)
    {
        var name = string.IsNullOrWhiteSpace(machineName) ? Environment.MachineName : machineName;
        var dataDirectory = Path.Combine(baseDirectory, "data");

        return new AgentConfig
        {
            Agent = new AgentOptions
            {
                Id = SanitizeAgentId(name),
                DisplayName = name,
                StateDirectory = dataDirectory,
                Tray = new TrayOptions { Enabled = true }
            },
            Sinks = new Dictionary<string, SinkConfig>(StringComparer.OrdinalIgnoreCase)
            {
                ["local"] = new SinkConfig
                {
                    Type = "wsServer",
                    Bind = "127.0.0.1",
                    Port = 18080
                }
            },
            Devices =
            [
                new DeviceConfig
                {
                    DeviceId = "file-001",
                    Enabled = true,
                    Type = "file",
                    DeviceType = "generic",
                    DeviceName = "文件接入（示例）",
                    Directory = Path.Combine(dataDirectory, "in"),
                    Pattern = "*.*",
                    StableSeconds = 5,
                    PollIntervalSeconds = 5,
                    ArchiveDirectory = Path.Combine(dataDirectory, "archive"),
                    ErrorDirectory = Path.Combine(dataDirectory, "error"),
                    QueueCapacity = 1024,
                    Codec = new CodecConfig
                    {
                        Type = "passthrough",
                        Encoding = "utf-8"
                    },
                    Sinks = ["local"]
                }
            ]
        };
    }

    public static string SanitizeAgentId(string value)
    {
        var sanitized = new string(value
            .Select(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-'
                ? character
                : '-')
            .ToArray())
            .Trim('-');

        return string.IsNullOrWhiteSpace(sanitized) ? "agent" : sanitized;
    }
}
