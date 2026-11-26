using Sim.DeviceAgent.Configuration;
using Xunit;

namespace Sim.DeviceAgent.Tests;

public sealed class AgentConfigDefaultsTests
{
    [Fact]
    public void Defaults_AreValid_AndRoundTripThroughWriter()
    {
        var root = CreateTempDirectory();
        try
        {
            var config = AgentConfigDefaults.Create("TEST-HOST", root);

            Assert.Empty(AgentConfigValidator.Validate(config));
            Assert.Equal("TEST-HOST", config.Agent.Id);
            Assert.Single(config.Sinks);
            Assert.True(config.Sinks.ContainsKey("local"));
            var device = Assert.Single(config.Devices);
            Assert.True(device.Enabled);
            Assert.Equal("file", device.Type);
            Assert.Equal(["local"], device.Sinks);

            var path = Path.Combine(root, "agent.json");
            AgentConfigWriter.Save(path, config);

            Assert.True(File.Exists(path));
            var loaded = AgentConfigLoader.Load(path);
            Assert.Empty(AgentConfigValidator.Validate(loaded));
            Assert.Equal("file-001", Assert.Single(loaded.Devices).DeviceId);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public void SanitizeAgentId_ReplacesUnsafeCharacters()
    {
        Assert.Equal("CNGUA1NB0237", AgentConfigDefaults.SanitizeAgentId("CNGUA1NB0237"));
        Assert.Equal("host-01", AgentConfigDefaults.SanitizeAgentId("host 01"));
        Assert.Equal("agent", AgentConfigDefaults.SanitizeAgentId("中文主机"));
    }

    private static string CreateTempDirectory()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "sim-device-agent-defaults-tests",
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
