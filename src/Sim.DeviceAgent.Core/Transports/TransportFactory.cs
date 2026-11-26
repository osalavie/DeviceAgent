using Microsoft.Extensions.Logging;
using Sim.DeviceAgent.Configuration;

namespace Sim.DeviceAgent.Transports;

public static class TransportFactory
{
    public static IDeviceTransport Create(DeviceConfig config, ILoggerFactory loggerFactory)
    {
        return config.Type.ToLowerInvariant() switch
        {
            "serial" => new SerialTransport(config, loggerFactory.CreateLogger<SerialTransport>()),
            "tcp" => new TcpTransport(config, loggerFactory.CreateLogger<TcpTransport>()),
            "file" => new FileTransport(config, loggerFactory.CreateLogger<FileTransport>()),
            _ => throw new NotSupportedException($"Unsupported device type '{config.Type}'.")
        };
    }
}
