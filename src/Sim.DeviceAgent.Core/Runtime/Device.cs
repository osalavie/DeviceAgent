using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Sim.DeviceAgent.Codecs;
using Sim.DeviceAgent.Configuration;
using Sim.DeviceAgent.Contracts;
using Sim.DeviceAgent.Transports;

namespace Sim.DeviceAgent.Runtime;

public sealed class Device : IAsyncDisposable
{
    private readonly string _agentId;
    private readonly DeviceConfig _config;
    private readonly IDeviceTransport _transport;
    private readonly IDeviceCodec _codec;
    private readonly IReadOnlyList<IEnvelopeSink> _sinks;
    private readonly ISequenceStore _sequenceStore;
    private readonly ILogger<Device> _logger;
    private readonly Encoding _encoding;
    private readonly Channel<QueuedFrame> _queue;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _statusLock = new();

    private IFrameDecoder? _decoder;
    private Task? _transportTask;
    private Task? _processTask;
    private PendingCommand? _pendingCommand;
    private int _commandInFlight;
    private int _stopped;
    private long _seq;
    private string _state = TransportState.Connecting.ToString().ToLowerInvariant();
    private DateTimeOffset? _lastDataAt;
    private long _receivedFrames;
    private long _droppedFrames;
    private long _parseErrors;
    private string? _lastError;

    public Device(
        string agentId,
        DeviceConfig config,
        IDeviceTransport transport,
        IDeviceCodec codec,
        IReadOnlyList<IEnvelopeSink> sinks,
        ISequenceStore sequenceStore,
        ILogger<Device> logger)
    {
        _agentId = agentId;
        _config = config;
        _transport = transport;
        _codec = codec;
        _sinks = sinks;
        _sequenceStore = sequenceStore;
        _logger = logger;
        _encoding = EncodingResolver.Resolve(config.Codec.Encoding);
        _queue = Channel.CreateBounded<QueuedFrame>(new BoundedChannelOptions(config.QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
    }

    public string DeviceId => _config.DeviceId;

    public string DeviceType => _config.DeviceType;

    public string DeviceName => _config.DeviceName;

    public string Type => _config.Type;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        UpdateState(TransportState.Connecting, null);
        await PublishStatusAsync(cancellationToken);

        var token = _cts.Token;
        _transportTask = Task.Run(() => _transport.RunAsync(HandleTransportEventAsync, token), token);
        _processTask = Task.Run(() => ProcessLoopAsync(token), token);
    }

    public async Task<CommandOutcome> SendCommandAsync(
        string text,
        TimeSpan timeout,
        string? matchPattern,
        CancellationToken cancellationToken)
    {
        if (!_transport.CanWrite)
        {
            return CommandOutcome.Fail("write_not_supported", $"Device '{DeviceId}' uses a read-only transport.");
        }

        if (Interlocked.CompareExchange(ref _commandInFlight, 1, 0) != 0)
        {
            return CommandOutcome.Fail("command_busy", "Another command is already waiting for a response on this device.");
        }

        try
        {
            Regex? matcher = null;
            if (!string.IsNullOrWhiteSpace(matchPattern))
            {
                try
                {
                    matcher = new Regex(
                        matchPattern,
                        RegexOptions.CultureInvariant,
                        TimeSpan.FromMilliseconds(250));
                }
                catch (ArgumentException ex)
                {
                    return CommandOutcome.Fail("invalid_match_pattern", ex.Message);
                }
            }

            var completion = new TaskCompletionSource<CommandMatch>(TaskCreationOptions.RunContinuationsAsynchronously);
            var pending = new PendingCommand(matcher, completion);
            Volatile.Write(ref _pendingCommand, pending);

            try
            {
                await _transport.WriteAsync(_encoding.GetBytes(text), cancellationToken);
                var effectiveTimeout = timeout <= TimeSpan.Zero
                    ? TimeSpan.FromSeconds(2)
                    : timeout > TimeSpan.FromSeconds(60)
                        ? TimeSpan.FromSeconds(60)
                        : timeout;

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(effectiveTimeout);
                var match = await completion.Task.WaitAsync(timeoutCts.Token);
                return CommandOutcome.Ok(match);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return CommandOutcome.Timeout();
            }
            catch (OperationCanceledException)
            {
                return CommandOutcome.Fail("command_cancelled", "The command was cancelled.");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Command on device {DeviceId} failed.", DeviceId);
                return CommandOutcome.Fail("command_failed", ex.Message);
            }
            finally
            {
                Volatile.Write(ref _pendingCommand, null);
            }
        }
        finally
        {
            Interlocked.Exchange(ref _commandInFlight, 0);
        }
    }

    public DeviceStatus GetStatus()
    {
        lock (_statusLock)
        {
            return new DeviceStatus(
                _config.DeviceId,
                _config.DeviceType,
                _config.DeviceName,
                _config.Type,
                _state,
                _lastDataAt,
                Interlocked.Read(ref _seq),
                _receivedFrames,
                _droppedFrames,
                _parseErrors,
                _lastError);
        }
    }

    public async Task StopAsync()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0)
        {
            return;
        }

        _cts.Cancel();
        Volatile.Read(ref _pendingCommand)?.Completion.TrySetException(new OperationCanceledException("The device is stopping."));

        try
        {
            await _transport.DisposeAsync();
        }
        catch
        {
            // Ignore transport disposal failures during shutdown.
        }

        var tasks = new[] { _transportTask, _processTask }
            .Where(task => task is not null)
            .Cast<Task>()
            .ToArray();

        if (tasks.Length > 0)
        {
            try
            {
                await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Device {DeviceId} did not stop cleanly.", DeviceId);
            }
        }

        _queue.Writer.TryComplete();
        UpdateState(TransportState.Stopped, null);
        await PublishStatusAsync(CancellationToken.None);
        _cts.Dispose();
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    private async Task<TransportEventResult> HandleTransportEventAsync(
        TransportEvent transportEvent,
        CancellationToken cancellationToken)
    {
        switch (transportEvent)
        {
            case TransportBytes bytes:
                return HandleBytes(bytes.Data);

            case TransportFileReady file:
                return await HandleFileAsync(file, cancellationToken);

            case TransportStateChanged state:
                UpdateState(state.State, state.Detail);
                await PublishStatusAsync(cancellationToken);
                return TransportEventResult.Ok;

            default:
                return TransportEventResult.Ok;
        }
    }

    private TransportEventResult HandleBytes(byte[] data)
    {
        try
        {
            _decoder ??= _codec.CreateDecoder();
            foreach (var frame in _decoder.Push(data))
            {
                if (frame.ParseError is not null)
                {
                    lock (_statusLock)
                    {
                        _parseErrors++;
                    }
                }

                Enqueue(frame, null);
            }

            return TransportEventResult.Ok;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Device {DeviceId} failed to decode a frame.", DeviceId);
            lock (_statusLock)
            {
                _parseErrors++;
            }

            return TransportEventResult.Fail(ex.Message);
        }
    }

    private async Task<TransportEventResult> HandleFileAsync(
        TransportFileReady file,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(
                file.Path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 8192,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            var index = 0;
            await foreach (var frame in _codec.DecodeFileAsync(stream, file.Metadata, cancellationToken))
            {
                if (frame.ParseError is not null)
                {
                    lock (_statusLock)
                    {
                        _parseErrors++;
                    }
                }

                Enqueue(
                    frame,
                    new FileRef(file.Metadata.Name, file.Metadata.Size, file.Metadata.Sha256, index++));
            }

            return TransportEventResult.Ok;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Device {DeviceId} failed to parse file {File}.", DeviceId, file.Path);
            return TransportEventResult.Fail(ex.Message);
        }
    }

    private async Task ProcessLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var item in _queue.Reader.ReadAllAsync(cancellationToken))
            {
                var seq = _sequenceStore.Next(_config.DeviceId);
                Interlocked.Exchange(ref _seq, seq);
                var timestamp = DateTimeOffset.UtcNow;
                lock (_statusLock)
                {
                    _receivedFrames++;
                    _lastDataAt = timestamp;
                }

                var envelope = BuildDataEnvelope(item, seq, timestamp);
                try
                {
                    await PublishAsync(envelope, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                CompletePendingCommand(item.Frame, seq, timestamp);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutdown path.
        }
    }

    private void Enqueue(DecodedFrame frame, FileRef? fileRef)
    {
        var item = new QueuedFrame(frame, fileRef);
        if (_queue.Writer.TryWrite(item))
        {
            return;
        }

        while (_queue.Reader.TryRead(out _))
        {
            lock (_statusLock)
            {
                _droppedFrames++;
            }

            if (_queue.Writer.TryWrite(item))
            {
                return;
            }
        }

        lock (_statusLock)
        {
            _droppedFrames++;
        }
    }

    private MessageEnvelope BuildDataEnvelope(QueuedFrame item, long seq, DateTimeOffset timestamp)
    {
        var envelope = new MessageEnvelope
        {
            Type = MessageTypes.Data,
            AgentId = _agentId,
            DeviceId = _config.DeviceId,
            DeviceType = _config.DeviceType,
            Seq = seq,
            Ts = timestamp,
            Raw = Convert.ToBase64String(item.Frame.Raw),
            Text = item.Frame.Text,
            Parsed = item.Frame.Parsed,
            FileRef = item.FileRef
        };

        if (item.Frame.ParseError is not null)
        {
            envelope.Error = new MessageError("parse_error", item.Frame.ParseError);
        }

        return envelope;
    }

    private void CompletePendingCommand(DecodedFrame frame, long seq, DateTimeOffset timestamp)
    {
        var pending = Volatile.Read(ref _pendingCommand);
        if (pending is null)
        {
            return;
        }

        if (pending.Matcher is not null)
        {
            if (frame.Text is null)
            {
                return;
            }

            try
            {
                if (!pending.Matcher.IsMatch(frame.Text))
                {
                    return;
                }
            }
            catch (RegexMatchTimeoutException)
            {
                return;
            }
        }

        pending.Completion.TrySetResult(new CommandMatch(frame, seq, timestamp));
    }

    private async Task PublishStatusAsync(CancellationToken cancellationToken)
    {
        var status = GetStatus();
        var envelope = new MessageEnvelope
        {
            Type = MessageTypes.Status,
            AgentId = _agentId,
            DeviceId = status.DeviceId,
            DeviceType = status.DeviceType,
            Seq = status.Seq,
            Ts = DateTimeOffset.UtcNow,
            State = status.State,
            Payload = JsonSerializer.SerializeToElement(new
            {
                status.Type,
                status.LastDataAt,
                status.ReceivedFrames,
                status.DroppedFrames,
                status.ParseErrors
            }, AgentJson.Options)
        };

        if (status.LastError is not null)
        {
            envelope.Error = new MessageError("device_error", status.LastError);
        }

        try
        {
            await PublishAsync(envelope, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutdown path.
        }
    }

    private async Task PublishAsync(MessageEnvelope envelope, CancellationToken cancellationToken)
    {
        foreach (var sink in _sinks)
        {
            try
            {
                await sink.PublishAsync(envelope, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Sink {SinkId} failed to publish device {DeviceId}.", sink.Id, DeviceId);
            }
        }
    }

    private void UpdateState(TransportState state, string? detail)
    {
        lock (_statusLock)
        {
            _state = state.ToString().ToLowerInvariant();
            if (!string.IsNullOrWhiteSpace(detail))
            {
                _lastError = detail;
            }
            else if (state is TransportState.Connected)
            {
                _lastError = null;
            }
        }
    }

    private sealed record QueuedFrame(DecodedFrame Frame, FileRef? FileRef);

    private sealed record PendingCommand(Regex? Matcher, TaskCompletionSource<CommandMatch> Completion);
}
