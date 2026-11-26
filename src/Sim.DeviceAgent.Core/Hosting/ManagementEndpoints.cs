using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Sim.DeviceAgent.Configuration;
using Sim.DeviceAgent.Runtime;

namespace Sim.DeviceAgent.Hosting;

public static class ManagementEndpoints
{
    public static void Map(
        WebApplication app,
        AgentConfig config,
        AgentDeviceRegistry registry,
        AgentSinkRegistry sinks,
        AgentConfigReloader? reloader)
    {
        app.MapGet("/health", () => Results.Ok(new { status = "ok", agentId = config.Agent.Id }));

        app.MapGet("/api/status", () =>
        {
            return Results.Ok(new
            {
                agentId = config.Agent.Id,
                displayName = config.Agent.DisplayName,
                wsServers = config.Sinks
                    .Where(pair => pair.Value is not null &&
                                   string.Equals(pair.Value.Type, "wsServer", StringComparison.OrdinalIgnoreCase))
                    .Select(pair => new
                    {
                        sinkId = pair.Key,
                        bind = pair.Value.Bind,
                        port = pair.Value.Port,
                        url = $"ws://{pair.Value.Bind}:{pair.Value.Port}/ws",
                        tokenRequired = !string.IsNullOrEmpty(pair.Value.Token),
                        allowedOrigins = pair.Value.AllowedOrigins
                    })
                    .ToArray(),
                devices = registry.GetStatuses(),
                sinks = sinks.GetStatuses(),
                config = reloader?.GetStatus()
            });
        });

        if (reloader is not null)
        {
            app.MapPost("/api/config/reload", async (CancellationToken cancellationToken) =>
            {
                var result = await reloader.ReloadAsync(cancellationToken, force: true).ConfigureAwait(false);
                return result.Success
                    ? Results.Ok(result)
                    : Results.Json(result, statusCode: StatusCodes.Status400BadRequest);
            });
        }
    }
}
