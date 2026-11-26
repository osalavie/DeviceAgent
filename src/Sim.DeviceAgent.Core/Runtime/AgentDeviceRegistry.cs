using System.Collections.Concurrent;

namespace Sim.DeviceAgent.Runtime;

public sealed class AgentDeviceRegistry
{
    private readonly ConcurrentDictionary<string, Device> _devices = new(StringComparer.OrdinalIgnoreCase);

    public void Add(Device device) => _devices[device.DeviceId] = device;

    public void Clear() => _devices.Clear();

    public Device? Get(string? deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return null;
        }

        return _devices.TryGetValue(deviceId, out var device) ? device : null;
    }

    public IReadOnlyList<Device> All()
        => _devices.Values.OrderBy(device => device.DeviceId, StringComparer.OrdinalIgnoreCase).ToArray();

    public IReadOnlyList<DeviceStatus> GetStatuses()
        => All().Select(device => device.GetStatus()).ToArray();
}
