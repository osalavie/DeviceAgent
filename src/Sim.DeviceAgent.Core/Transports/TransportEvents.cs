namespace Sim.DeviceAgent.Transports;

public enum TransportState
{
    Disconnected,
    Connecting,
    Connected,
    Waiting,
    Faulted,
    Stopped
}

public abstract record TransportEvent;

public sealed record TransportBytes(byte[] Data) : TransportEvent;

public sealed record TransportFileReady(string Path, FileMetadata Metadata) : TransportEvent;

public sealed record TransportStateChanged(TransportState State, string? Detail = null) : TransportEvent;

public sealed record TransportEventResult(bool Success, string? Error)
{
    public static readonly TransportEventResult Ok = new(true, null);

    public static TransportEventResult Fail(string error) => new(false, error);
}

public delegate Task<TransportEventResult> TransportEventHandler(
    TransportEvent transportEvent,
    CancellationToken cancellationToken);

public interface IDeviceTransport : IAsyncDisposable
{
    bool CanWrite { get; }

    Task RunAsync(TransportEventHandler handler, CancellationToken cancellationToken);

    Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken);
}
