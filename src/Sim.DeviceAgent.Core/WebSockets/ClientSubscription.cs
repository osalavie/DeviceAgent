using System.Text.Json;
using Sim.DeviceAgent.Contracts;

namespace Sim.DeviceAgent.WebSockets;

public sealed class ClientSubscription
{
    public static readonly ClientSubscription All = new([]);

    public ClientSubscription(IEnumerable<string>? devices)
    {
        Devices = new HashSet<string>(devices ?? [], StringComparer.OrdinalIgnoreCase);
    }

    public HashSet<string> Devices { get; }

    public bool Matches(MessageEnvelope envelope)
        => Devices.Count == 0 || (envelope.DeviceId is not null && Devices.Contains(envelope.DeviceId));

    public static ClientSubscription FromJson(JsonElement? element)
    {
        if (element is not { ValueKind: JsonValueKind.Object } subscription)
        {
            return All;
        }

        return new ClientSubscription(ReadSet(subscription, "devices"));
    }

    private static IEnumerable<string> ReadSet(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return array.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Cast<string>()
            .ToArray();
    }
}
