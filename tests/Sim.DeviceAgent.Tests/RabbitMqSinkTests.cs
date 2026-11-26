using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using Sim.DeviceAgent.Configuration;
using Sim.DeviceAgent.Contracts;
using Sim.DeviceAgent.Runtime;
using Xunit;

namespace Sim.DeviceAgent.Tests;

public sealed class RabbitMqSinkTests
{
    [Fact]
    public async Task RabbitMqSink_SpoolsData_WhileBrokerIsUnavailable()
    {
        var root = CreateTempDirectory();
        var sink = new RabbitMqSink(
            "mq",
            new SinkConfig
            {
                Type = "rabbitmq",
                Host = "127.0.0.1",
                Port = GetClosedPort(),
                Exchange = "sim.device-agent",
                RoutingKey = "device.{deviceId}"
            },
            new AgentOptions
            {
                Id = "agent-test",
                StateDirectory = root
            },
            NullLoggerFactory.Instance);

        try
        {
            await sink.StartAsync(CancellationToken.None);
            await sink.PublishAsync(new MessageEnvelope
            {
                Type = MessageTypes.Data,
                AgentId = "agent-test",
                DeviceId = "PT-001",
                DeviceType = "pullTest",
                Seq = 1,
                Ts = DateTimeOffset.UtcNow
            });

            var status = sink.GetStatus();

            Assert.Equal("rabbitmq", status.Type);
            Assert.Equal("disconnected", status.State);
            Assert.Equal(1, status.PendingEntries);
            Assert.Contains("127.0.0.1", status.Endpoint, StringComparison.Ordinal);
        }
        finally
        {
            await sink.DisposeAsync();
            DeleteDirectory(root);
        }
    }

    private static int GetClosedPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static string CreateTempDirectory()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "sim-device-agent-rabbitmq-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void DeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // Test cleanup only.
        }
    }
}
