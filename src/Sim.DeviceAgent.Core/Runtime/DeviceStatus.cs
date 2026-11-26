namespace Sim.DeviceAgent.Runtime;

public sealed record DeviceStatus(
    string DeviceId,
    string DeviceName,
    string Type,
    string State,
    DateTimeOffset? LastDataAt,
    long Seq,
    long ReceivedFrames,
    long DroppedFrames,
    long ParseErrors,
    string? LastError);
