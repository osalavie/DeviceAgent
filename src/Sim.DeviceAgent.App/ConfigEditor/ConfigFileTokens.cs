using System.Text.Json;

namespace Sim.DeviceAgent.App.ConfigEditor;

internal static class ConfigFileTokens
{
    public static (string? LocalToken, Dictionary<string, string> SinkTokens) Read(string path)
    {
        var sinkTokens = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var document = JsonDocument.Parse(
                File.ReadAllText(path),
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip
                });

            var root = document.RootElement;
            string? localToken = null;

            if (TryGetProperty(root, "agent", out var agent) &&
                TryGetProperty(agent, "localWs", out var localWs) &&
                TryGetProperty(localWs, "token", out var token) &&
                token.ValueKind == JsonValueKind.String)
            {
                localToken = token.GetString();
            }

            if (TryGetProperty(root, "sinks", out var sinks) && sinks.ValueKind == JsonValueKind.Object)
            {
                foreach (var sink in sinks.EnumerateObject())
                {
                    if (TryGetProperty(sink.Value, "token", out var sinkToken) &&
                        sinkToken.ValueKind == JsonValueKind.String)
                    {
                        sinkTokens[sink.Name] = sinkToken.GetString() ?? string.Empty;
                    }
                }
            }

            return (localToken, sinkTokens);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return (null, sinkTokens);
        }
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }
}
