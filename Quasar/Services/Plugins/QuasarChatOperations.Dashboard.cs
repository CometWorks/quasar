using System.Text.Json;
using Magnetar.Protocol.Model;
using Magnetar.Protocol.Transport;
using Microsoft.AspNetCore.Authorization;
using Quasar.Models;
using Quasar.Plugin.Abstractions.Operations;
using Quasar.Services.Auth;
using Browser = Quasar.Client.Operations;
using static Quasar.Services.Plugins.QuasarCoreOperations;

namespace Quasar.Services.Plugins;

public static partial class QuasarChatOperations
{
    public sealed record ChatNodeRequest(string UniqueName = "", string Text = "");
    private static async Task<bool> Has(QuasarOperationContext caller, string policy) =>
        (await caller.Services.GetRequiredService<IAuthorizationService>().AuthorizeAsync(caller.User, null, policy)).Succeeded;

    private static async Task Require(QuasarOperationContext caller, string policy)
    {
        if (!await Has(caller, policy)) throw new QuasarOperationException(403, "Permission denied.");
    }

    private static void AddDashboardChat(IServiceCollection services)
    {
        services.AddDiscordDashboardOperations();
        services.AddAccessDashboardOperations();
        services.AddPlayerDashboardOperations();
        services.AddAnalyticsDashboardOperations();
        services.AddQuasarRead<ChatNodeRequest>(PluginId, "chat.table", [QuasarPolicyNames.CanView], async (caller, input, _) =>
        {
            await Require(caller, QuasarPolicyNames.CanView);
            var canControl = await Has(caller, QuasarPolicyNames.CanControlServers);
            var canQueryClusters = await Has(caller, QuasarPolicyNames.ClusterQuery);
            var agents = caller.Services.GetRequiredService<AgentRegistry>().GetAgents().Where(a => a.IsConnected && a.TelemetryKey.Length > 0
                && (!a.IsCluster || canQueryClusters && caller.User.CanQueryCluster(a.ClusterId))).OrderByDescending(a => a.LastSeenUtc).ToArray();
            var definitions = caller.Services.GetRequiredService<DedicatedServerCatalog>().GetServers();
            var targets = definitions.Select(d => new Browser.ChatTarget(d.UniqueName, string.IsNullOrWhiteSpace(d.DisplayName) ? d.UniqueName : d.DisplayName))
                .Concat(agents.Select(a => new Browser.ChatTarget(a.TelemetryKey, a.IsCluster
                    ? $"{a.ClusterId} / {a.ClusterSlot} / {a.ClusterNodeId} / epoch {a.ClusterEpoch}" : a.ServerDisplayName)))
                .DistinctBy(t => t.UniqueName, StringComparer.OrdinalIgnoreCase).OrderByDescending(t => agents.Any(a => t.UniqueName.Equals(a.TelemetryKey, StringComparison.OrdinalIgnoreCase)))
                .ThenBy(t => t.DisplayName, StringComparer.OrdinalIgnoreCase).ToArray();
            var selected = targets.FirstOrDefault(t => t.UniqueName.Equals(input.UniqueName, StringComparison.OrdinalIgnoreCase)) ?? targets.FirstOrDefault();
            var agent = agents.FirstOrDefault(a => a.TelemetryKey.Equals(selected?.UniqueName, StringComparison.OrdinalIgnoreCase));
            var state = caller.Services.GetRequiredService<DedicatedServerSupervisor>().GetSnapshots()
                .FirstOrDefault(s => s.UniqueName.Equals(selected?.UniqueName, StringComparison.OrdinalIgnoreCase))?.State;
            var stable = state is not (DedicatedServerProcessState.Starting or DedicatedServerProcessState.Stopping or DedicatedServerProcessState.Restarting);
            var canSend = canControl && agent is not null && (!agent.IsCluster || await Has(caller, QuasarPolicyNames.ClusterManage));
            var snapshot = agent?.Snapshot;
            var table = new Browser.DashboardChat(targets, agents.Length, selected?.UniqueName ?? "", agent is not null, snapshot is not null, agent?.WorldDisplayName,
                snapshot?.Metrics.PlayersOnline ?? 0, snapshot?.Metrics.MaxPlayers ?? 0, canControl, canSend, canControl && stable && agent is { IsCluster: false },
                canControl && stable && definitions.Any(d => d.UniqueName.Equals(selected?.UniqueName, StringComparison.OrdinalIgnoreCase)),
                (snapshot?.RecentChat ?? []).OrderBy(m => m.TimestampTicksUtc).Select(Line).Select(m => new Browser.ChatLine(m.Time, m.Author, m.Channel, m.Text)).ToArray(),
                (snapshot?.ChatCommands ?? []).Select(c => new Browser.ChatSuggestion(c.Text, c.Syntax, c.Description)).ToArray(),
                (agent?.CommandResults ?? []).Take(6).Select(c => new Browser.ChatCommandResult(c.Success, c.Message)).ToArray());
            var bytes = JsonSerializer.SerializeToUtf8Bytes(table, JsonSerializerOptions.Web);
            return new(_ => Task.FromResult<Stream>(new MemoryStream(bytes, false)), "application/json", Length: bytes.Length);
        }, new() { Effect = QuasarOperationEffect.Read, AgentAccess = QuasarAgentAccess.Denied, Description = "Streams the authorized node's current chat console." });

        RegisterNodeCommand("chat.node.refresh", ServerCommandType.Refresh);
        RegisterNodeCommand("chat.node.send", ServerCommandType.SendChat);
        RegisterNodeCommand("chat.node.command", ServerCommandType.RunChatCommand);

        void RegisterNodeCommand(string operation, ServerCommandType command)
        {
            var policy = command == ServerCommandType.Refresh ? QuasarPolicyNames.CanView : QuasarPolicyNames.CanControlServers;
            services.AddQuasarOperation<ChatNodeRequest, bool>(PluginId, operation, [policy], async (caller, input, _) =>
            {
                await Require(caller, policy);
                var registry = caller.Services.GetRequiredService<AgentRegistry>();
                var agent = registry.GetAgents().FirstOrDefault(a => a.IsConnected && a.TelemetryKey == input.UniqueName);
                if (agent is null || agent.IsCluster && (!caller.User.CanQueryCluster(agent.ClusterId) || !await Has(caller, QuasarPolicyNames.ClusterQuery)))
                    throw new QuasarOperationException(404, "Selected server is not connected.");
                if (agent.IsCluster && command != ServerCommandType.Refresh) await Require(caller, QuasarPolicyNames.ClusterManage);
                var text = input.Text?.Trim() ?? "";
                if (command != ServerCommandType.Refresh && (text.Length == 0 || command == ServerCommandType.RunChatCommand != text.StartsWith('!')))
                    throw new QuasarOperationException(400, "Enter a message or a command starting with !.");
                await registry.SendCommandAsync(new ServerCommandEnvelope { UniqueName = agent.UniqueNameKey, AgentId = agent.AgentId, ServerId = agent.ServerKey,
                    CommandType = command, Text = text, IssuedAtUtc = DateTimeOffset.UtcNow });
                return true;
            }, new() { AgentAccess = QuasarAgentAccess.Denied, Effect = command == ServerCommandType.Refresh ? QuasarOperationEffect.Read : QuasarOperationEffect.Change,
                Description = $"Requests {command} on the selected connected chat node." });
        }
    }
}
