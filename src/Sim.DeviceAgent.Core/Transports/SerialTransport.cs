using System.IO.Ports;
using Microsoft.Extensions.Logging;
using Sim.DeviceAgent.Configuration;

namespace Sim.DeviceAgent.Transports;

public sealed class SerialTransport : IDeviceTransport
{
    private readonly DeviceConfig _config;
    private readonly ILogger<SerialTransport> _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private SerialPort? _port;

    public SerialTransport(DeviceConfig config, ILogger<SerialTransport> logger)
    {
        _config = config;
        _logger = logger;
    }

    public bool CanWrite => true;

    public async Task RunAsync(TransportEventHandler handler, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            SerialPort? port = null;
            try
            {
                await NotifyAsync(handler, new TransportStateChanged(TransportState.Connecting), cancellationToken);
                port = CreatePort();
                port.Open();
                _port = port;
                DiscardStaleInput(port);
                await NotifyAsync(handler, new TransportStateChanged(TransportState.Connected), cancellationToken);

                using var registration = cancellationToken.Register(static state =>
                {
                    try
                    {
                        ((SerialPort)state!).Close();
                    }
                    catch
                    {
                        // Closing an already broken port is expected during shutdown.
                    }
                }, port);

                var buffer = new byte[4096];
                while (!cancellationToken.IsCancellationRequested)
                {
                    int read;
                    try
                    {
                        read = port.Read(buffer, 0, buffer.Length);
                    }
                    catch (TimeoutException)
                    {
                        continue;
                    }

                    if (read <= 0)
                    {
                        continue;
                    }

                    var result = await handler(new TransportBytes(buffer.AsSpan(0, read).ToArray()), cancellationToken);
                    if (!result.Success)
                    {
                        _logger.LogWarning("Serial device {DeviceId} rejected a frame: {Error}", _config.DeviceId, result.Error);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Serial device {DeviceId} is unavailable; retrying.", _config.DeviceId);
                await NotifyAsync(handler, new TransportStateChanged(TransportState.Waiting, ex.Message), cancellationToken);
            }
            finally
            {
                _port = null;
                try
                {
                    port?.Dispose();
                }
                catch
                {
                    // Ignore disposal failures for a disconnected device.
                }
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        await NotifyAsync(handler, new TransportStateChanged(TransportState.Stopped), CancellationToken.None);
    }

    public async Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            var port = _port ?? throw new InvalidOperationException($"Serial device '{_config.DeviceId}' is not connected.");
            port.Write(data.ToArray(), 0, data.Length);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        try
        {
            _port?.Dispose();
        }
        catch
        {
            // Ignore disposal failures.
        }

        _writeLock.Dispose();
        return ValueTask.CompletedTask;
    }

    // 重连后先把打开前积压在驱动/系统缓冲区的旧帧读掉，避免把历史数据当成新数据。
    // 不用 DiscardInBuffer/PurgeComm，避免 PURGE_RXABORT 把驱动的挂起读队列永久停掉。
    private void DiscardStaleInput(SerialPort port)
    {
        try
        {
            var pending = port.BytesToRead;
            if (pending <= 0)
            {
                return;
            }

            var buffer = new byte[pending];
            var read = port.Read(buffer, 0, pending);
            _logger.LogInformation(
                "Serial device {DeviceId} discarded {Bytes} stale byte(s) after opening the port.",
                _config.DeviceId,
                read);
        }
        catch (TimeoutException)
        {
            // 旧数据在读取前已消失，无需处理。
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Serial device {DeviceId} could not discard stale input.", _config.DeviceId);
        }
    }

    private SerialPort CreatePort()
    {
        var parity = Enum.Parse<Parity>(_config.Parity, ignoreCase: true);
        var stopBits = Enum.Parse<StopBits>(_config.StopBits, ignoreCase: true);
        return new SerialPort(_config.Port, _config.BaudRate, parity, _config.DataBits, stopBits)
        {
            ReadTimeout = _config.ReadTimeoutMs,
            WriteTimeout = 2000,
            DtrEnable = _config.DtrEnable,
            RtsEnable = _config.RtsEnable,
            Handshake = Handshake.None
        };
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
