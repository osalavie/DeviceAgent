using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Sim.DeviceAgent.Codecs;
using Sim.DeviceAgent.Configuration;
using Sim.DeviceAgent.Contracts;
using Sim.DeviceAgent.Runtime;
using Sim.DeviceAgent.Transports;
using Xunit;

namespace Sim.DeviceAgent.Tests;

public sealed class DeviceCommandTests
{
    [Fact]
    public async Task SendCommand_MatchesResponseFrame_AndPublishesData()
    {
        var (device, sink, _) = CreateDevice();
        await device.StartAsync(CancellationToken.None);

        try
        {
            var outcome = await device.SendCommandAsync(
                "READ\r\n",
                TimeSpan.FromSeconds(2),
                "^RESULT",
                CancellationToken.None);

            Assert.True(outcome.Success);
            Assert.False(outcome.TimedOut);
            Assert.NotNull(outcome.Match);
            Assert.Equal("RESULT,FORCE=12.34N", outcome.Match!.Frame.Text);

            await WaitUntilAsync(
                () => sink.Envelopes.Any(
                    envelope => envelope.Type == MessageTypes.Data
                                && envelope.DeviceId == "PT-001"
                                && envelope.Text == "RESULT,FORCE=12.34N"
                                && envelope.Seq > 0),
                TimeSpan.FromSeconds(2));
        }
        finally
        {
            await device.StopAsync();
        }
    }

    [Fact]
    public async Task SendCommand_TimesOut_WhenNoMatchingFrameArrives()
    {
        var (device, _, _) = CreateDevice(respond: false);
        await device.StartAsync(CancellationToken.None);

        try
        {
            var outcome = await device.SendCommandAsync(
                "READ\r\n",
                TimeSpan.FromMilliseconds(200),
                "^RESULT",
                CancellationToken.None);

            Assert.False(outcome.Success);
            Assert.True(outcome.TimedOut);
            Assert.Equal("command_timeout", outcome.ErrorCode);
        }
        finally
        {
            await device.StopAsync();
        }
    }

    [Fact]
    public async Task NonMatchingFrame_IncrementsParseErrors_AndPublishesParseError()
    {
        var (device, sink, transport) = CreateDevice(parsePattern: "^RESULT");
        await device.StartAsync(CancellationToken.None);

        try
        {
            await transport.EmitAsync("NOISE\r\n");

            await WaitUntilAsync(
                () => device.GetStatus().ParseErrors == 1,
                TimeSpan.FromSeconds(2));

            Assert.Equal(1, device.GetStatus().ParseErrors);
            await WaitUntilAsync(
                () => sink.Envelopes.Any(envelope => envelope.Error?.Code == "parse_error"),
                TimeSpan.FromSeconds(2));
        }
        finally
        {
            await device.StopAsync();
        }
    }

    private static (Device Device, CapturingSink Sink, FakeTransport Transport) CreateDevice(
        bool respond = true,
        string? parsePattern = null)
    {
        var config = new DeviceConfig
        {
            DeviceId = "PT-001",
            Type = "serial",
            DeviceType = "pullTest",
            DeviceName = "Pull tester",
            Port = "COM1",
            QueueCapacity = 64,
            Codec = new CodecConfig
            {
                Type = "delimiter",
                Delimiter = "\r\n",
                Encoding = "ascii",
                Parse = parsePattern is null ? null : new RegexParseOptions { Pattern = parsePattern }
            },
            Sinks = ["local"]
        };

        var sink = new CapturingSink("local", "wsServer");
        var transport = new FakeTransport(respond);
        var device = new Device(
            "agent-test",
            config,
            transport,
            CodecFactory.Create(config.Codec, "serial"),
            [sink],
            new InMemorySequenceStore(),
            NullLogger<Device>.Instance);

        return (device, sink, transport);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (!condition())
        {
            if (DateTimeOffset.UtcNow > deadline)
            {
                throw new TimeoutException("Condition was not met before the timeout.");
            }

            await Task.Delay(25);
        }
    }

    private sealed class FakeTransport : IDeviceTransport
    {
        private readonly bool _respond;
        private readonly TaskCompletionSource<TransportEventHandler> _handlerReady =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public FakeTransport(bool respond)
        {
            _respond = respond;
        }

        public bool CanWrite => true;

        public async Task RunAsync(TransportEventHandler handler, CancellationToken cancellationToken)
        {
            _handlerReady.TrySetResult(handler);
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // Shutdown path.
            }
        }

        public async Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
        {
            if (!_respond)
            {
                return;
            }

            await EmitAsync("RESULT,FORCE=12.34N\r\n");
        }

        public async Task EmitAsync(string text)
        {
            var handler = await _handlerReady.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await handler(new TransportBytes(Encoding.ASCII.GetBytes(text)), CancellationToken.None);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class CapturingSink : IEnvelopeSink
    {
        private readonly ConcurrentQueue<MessageEnvelope> _envelopes = new();

        public CapturingSink(string id, string type)
        {
            Id = id;
            Type = type;
        }

        public string Id { get; }

        public string Type { get; }

        public IReadOnlyCollection<MessageEnvelope> Envelopes => _envelopes.ToArray();

        public SinkStatus GetStatus() => new(Id, Type, "connected", 0, 0, null);

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public ValueTask PublishAsync(MessageEnvelope envelope, CancellationToken cancellationToken = default)
        {
            _envelopes.Enqueue(envelope);
            return ValueTask.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class InMemorySequenceStore : ISequenceStore
    {
        private long _current;

        public long Next(string deviceId) => Interlocked.Increment(ref _current);
    }
}
