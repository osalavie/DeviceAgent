namespace Sim.DeviceAgent.Transports;

public sealed record FileMetadata(string Name, long Size, string Sha256, DateTimeOffset LastWriteTime);
