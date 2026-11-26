using System.Text.Json;
using Sim.DeviceAgent.Contracts;

namespace Sim.DeviceAgent.WebSockets;

public sealed class ClientSubscription
{
    public static readonly ClientSubscription All = new([], []);

    public ClientSubscription(
        IEnumerable<string>? devices,
        IEnumerable<string>? types)
    {
        Devices = new HashSet<string>(devices ?? [], StringComparer.OrdinalIgnoreCase);
        Types = new HashSet<string>(types ?? [], StringComparer.OrdinalIgnoreCase);
    }

    public HashSet<string> Devices { get; }

    public HashSet<string> Types { get; }

    public bool Matches(MessageEnvelope envelope)
    {
        if (Devices.Count > 0 && (envelope.DeviceId is null || !Devices.Contains(envelope.DeviceId)))
        {
            return false;
        }

        if (Types.Count > 0 && (envelope.DeviceType is null || !Types.Contains(envelope.DeviceType)))
        {
            return false;
        }

        return true;
    }

    public static ClientSubscription FromJson(JsonElement? element)
    {
        if (element is not { ValueKind: JsonValueKind.Object } subscription)
        {
            return All;
        }

        return new ClientSubscription(
            ReadSet(subscription, "devices"),
            ReadSet(subscription, "types"));
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
