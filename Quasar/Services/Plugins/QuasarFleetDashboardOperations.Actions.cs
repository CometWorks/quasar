using System.Globalization;
using System.Text.Json;
using Magnetar.Protocol.Runtime;
using Quasar.Client.Fleet;
using Quasar.Models;
using Quasar.Plugin.Abstractions.Operations;
using Quasar.Services.Auth;
using Quasar.Services.Backup;
using static Quasar.Services.Plugins.QuasarCoreOperations;

namespace Quasar.Services.Plugins;

internal static partial class QuasarFleetDashboardOperations
{
    private static void RegisterStandaloneActions(IServiceCollection services)
    {
        services.AddQuasarOperation<FleetTarget, FleetPaths>(PluginId, "fleet.delete.review", [QuasarPolicyNames.CanEditServers], (caller, input, _) =>
        {
            var server = Current(caller, input);
            return Task.FromResult(new FleetPaths(MagnetarPaths.GetQuasarServerDirectory(server.UniqueName), ""));
        }, Read with { Description = "Returns the server paths affected by deletion." });
        services.AddQuasarOperation<FleetTarget, FleetPaths>(PluginId, "fleet.template.review", [QuasarPolicyNames.CanEditConfigs], (caller, input, _) =>
        {
            var server = Current(caller, input);
            return Task.FromResult(new FleetPaths("", server.GetWorldSavePath()));
        }, Read with { Description = "Returns template source information from the stopped server." });
        services.AddQuasarOperation<FleetTarget, bool>(PluginId, "fleet.delete", [QuasarPolicyNames.CanEditServers], async (caller, input, token) =>
        {
            var server = Current(caller, input);
            var maintenance = caller.Services.GetRequiredService<ServerRestoreCoordinator>();
            if (!maintenance.TryBeginBackup(server.UniqueName, out var scope)) throw new QuasarOperationException(409, "A file operation is in progress.");
            using (scope)
            {
                var supervisor = caller.Services.GetRequiredService<DedicatedServerSupervisor>();
                await caller.Services.GetRequiredService<DedicatedServerCatalog>().DeleteAsync(server.UniqueName, input.Revision,
                    current => current.GoalState == DedicatedServerGoalState.Off && !supervisor.IsServerProcessActive(current.UniqueName), token);
                caller.Services.GetRequiredService<AgentRegistry>().PruneDisconnectedByUniqueName(server.UniqueName);
            }
            return true;
        }, Change with { Description = "Deletes the reviewed standalone server." });
        services.AddQuasarUpload<FleetTarget, string>(PluginId, "fleet.template.create", [QuasarPolicyNames.CanEditConfigs], QuasarSdkLimits.MaximumJsonBytes,
            (caller, input, body, _) => Task.FromResult(new QuasarUpload<string>(async token =>
            {
                var request = await JsonSerializer.DeserializeAsync<FleetTemplateInput>(body, Json, token);
                if (string.IsNullOrWhiteSpace(request?.Name)) throw new QuasarOperationException(400, "Enter a template name.");
                var maintenance = caller.Services.GetRequiredService<ServerRestoreCoordinator>();
                if (!maintenance.TryBeginWorldImport(input.UniqueName, out var scope)) throw new QuasarOperationException(409, "A file operation is in progress.");
                using (scope)
                {
                    var current = Current(caller, input);
                    if (current.GoalState != DedicatedServerGoalState.Off || caller.Services.GetRequiredService<DedicatedServerSupervisor>().IsServerProcessActive(current.UniqueName))
                        throw new QuasarOperationException(409, "Stop the server before copying its world.");
                    var created = await caller.Services.GetRequiredService<QuasarWorldTemplateCatalog>().ImportAsync(request.Name, request.Description, current.GetWorldSavePath(), token);
                    return created.Name;
                }
            })), Change with { Description = "Creates a world template from the selected stopped server." });
        services.AddQuasarRead<FleetLiveRequest>(PluginId, "fleet.live", [QuasarPolicyNames.CanView], (caller, input, _) =>
        {
            if (input.AgentId is null) RequireServer(caller.Services, input.UniqueName);
            var agent = caller.Services.GetRequiredService<AgentRegistry>().GetAgents().Where(a => !a.IsCluster && (input.AgentId is null || a.AgentId == input.AgentId) && a.UniqueNameKey.Equals(input.UniqueName, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(a => a.IsConnected).ThenByDescending(a => a.LastSnapshotReceivedUtc).FirstOrDefault();
            if (agent is null && input.AgentId is not null) throw new QuasarOperationException(404, "Agent no longer available.");
            var metrics = agent?.Snapshot?.Metrics;
            var live = new FleetLive(agent?.Snapshot?.Players.Select(p => new FleetPlayer(p.SteamId.ToString(CultureInfo.InvariantCulture), TextSanitizer.CleanGameText(p.DisplayName),
                TextSanitizer.CleanGameText(p.PlatformDisplayName), p.ServiceName, p.FactionTag, string.IsNullOrEmpty(p.PromoteLevel) ? p.IsAdmin ? "Admin" : "None" : p.PromoteLevel, p.PingMs)).ToArray() ?? [],
                agent?.Snapshot?.RecentChat.OrderByDescending(m => m.TimestampTicksUtc).Take(12).Select(m => new FleetChat(m.TimestampTicksUtc > 0 && m.TimestampTicksUtc <= DateTime.MaxValue.Ticks
                    ? new DateTimeOffset(m.TimestampTicksUtc, TimeSpan.Zero) : null, m.IsServerMessage ? "Server" : TextSanitizer.CleanGameText(m.AuthorName), m.Channel.ToString(), TextSanitizer.CleanGameText(m.Content, true))).ToArray() ?? [],
                agent?.CommandResults.Take(6).Select(r => r.Message).ToArray() ?? [], metrics?.IsSaveInProgress == true, metrics?.LastWorldSaveUtc, metrics?.UnsavedGameTimeSeconds);
            return Task.FromResult(new QuasarRead(_ => Task.FromResult<Stream>(new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(live, Json), false)), "application/json"));
        }, Read with { Description = "Returns live standalone server status." });
    }
    private static DedicatedServerDefinition Current(QuasarOperationContext caller, FleetTarget input)
    {
        var server = RequireServer(caller.Services, input.UniqueName);
        if (server.UpdatedAtUtc != input.Revision) throw new QuasarOperationException(409, "The server changed. Refresh and review again.");
        return server;
    }
}
