namespace Sim.DeviceAgent.Contracts;

public sealed record FileRef(string Name, long Size, string Sha256, int Index);
