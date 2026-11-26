using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Sim.DeviceAgent.Configuration;

namespace Sim.DeviceAgent.Transports;

public sealed class TcpTransport : IDeviceTransport
{
    private readonly DeviceConfig _config;
    private readonly ILogger<TcpTransport> _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private TcpClient? _client;

    public TcpTransport(DeviceConfig config, ILogger<TcpTransport> logger)
    {
        _config = config;
        _logger = logger;
    }

    public bool CanWrite => true;

    public async Task RunAsync(TransportEventHandler handler, CancellationToken cancellationToken)
    {
        var reconnectDelay = TimeSpan.FromSeconds(_config.ReconnectMinSeconds);
        var maxDelay = TimeSpan.FromSeconds(_config.ReconnectMaxSeconds);

        while (!cancellationToken.IsCancellationRequested)
        {
            using var client = new TcpClient();
            try
            {
                await NotifyAsync(handler, new TransportStateChanged(TransportState.Connecting), cancellationToken);
                await client.ConnectAsync(_config.Host!, int.Parse(_config.Port!), cancellationToken);
                _client = client;
                reconnectDelay = TimeSpan.FromSeconds(_config.ReconnectMinSeconds);
                await NotifyAsync(handler, new TransportStateChanged(TransportState.Connected), cancellationToken);

                using var stream = client.GetStream();
                var buffer = new byte[8192];
                while (!cancellationToken.IsCancellationRequested)
                {
                    var read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken);
                    if (read == 0)
                    {
                        throw new IOException("The remote peer closed the TCP connection.");
                    }

                    var result = await handler(new TransportBytes(buffer.AsSpan(0, read).ToArray()), cancellationToken);
                    if (!result.Success)
                    {
                        _logger.LogWarning("TCP device {DeviceId} rejected a frame: {Error}", _config.DeviceId, result.Error);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "TCP device {DeviceId} disconnected; retrying.", _config.DeviceId);
                await NotifyAsync(handler, new TransportStateChanged(TransportState.Waiting, ex.Message), cancellationToken);
            }
            finally
            {
                _client = null;
            }

            try
            {
                await Task.Delay(reconnectDelay, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            var nextDelay = TimeSpan.FromSeconds(Math.Min(maxDelay.TotalSeconds, reconnectDelay.TotalSeconds * 2));
            reconnectDelay = nextDelay;
        }

        await NotifyAsync(handler, new TransportStateChanged(TransportState.Stopped), CancellationToken.None);
    }

    public async Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            var client = _client ?? throw new InvalidOperationException($"TCP device '{_config.DeviceId}' is not connected.");
            var stream = client.GetStream();
            await stream.WriteAsync(data, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        _client?.Dispose();
        _writeLock.Dispose();
        return ValueTask.CompletedTask;
    }

    private static async Task NotifyAsync(
        TransportEventHandler handler,
        TransportStateChanged state,
        CancellationToken cancellationToken)
    {
        try
        {
            await handler(state, cancellationToken);
        }
        catch when (cancellationToken.IsCancellationRequested)
        {
            // Shutdown path.
        }
        catch
        {
            // State notifications must not take down the transport loop.
        }
    }
}
