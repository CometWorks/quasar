using System.Text;
using System.Text.Json;
using Magnetar.Protocol.Model;
using Magnetar.Protocol.Transport;
using Microsoft.AspNetCore.Authorization;
using Quasar.Client.Fleet;
using Quasar.Plugin.Abstractions.Operations;
using Quasar.Services.Auth;
using Quasar.Services.PluginSdk;
using static Quasar.Services.Plugins.QuasarCoreOperations;

namespace Quasar.Services.Plugins;

public static class QuasarLivePluginDashboardOperations
{
    private static readonly QuasarOperationMetadata Read = new() { AgentAccess = QuasarAgentAccess.Denied, RequireDefaultPolicies = true, Effect = QuasarOperationEffect.Read };
    private static readonly string[] Edit = [QuasarPolicyNames.CanEditConfigs];
    private static readonly string[] View = [QuasarPolicyNames.CanView];
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static void AddQuasarLivePluginDashboardOperations(this IServiceCollection services)
    {
        services.AddQuasarOperation<EmptyRequest, LivePlugins>(PluginId, "liveplugins.list", View, async (caller, _, _) =>
        {
            var configs = caller.Services.GetRequiredService<PluginConfigService>();
            var servers = caller.Services.GetRequiredService<DedicatedServerCatalog>();
            var agents = caller.Services.GetRequiredService<AgentRegistry>().GetAgents()
                .Where(a => a.IsConnected && (!a.IsCluster || caller.User.CanQueryCluster(a.ClusterId)) && configs.HasConfigs(a.AgentId))
                .Select(a => new ConfigAgent(a.AgentId, a.ConnectionId, AgentName(servers, a), a.HostDisplayName, a.IsCluster ? a.ClusterId : null,
                    configs.GetConfigsForAgent(a.AgentId).Select(p => new AgentPlugin(p.PluginId, p.DisplayName)).ToArray())).ToArray();
            bool canEdit = (await caller.Services.GetRequiredService<IAuthorizationService>().AuthorizeAsync(caller.User, null, QuasarPolicyNames.CanEditConfigs)).Succeeded;
            return new(agents, canEdit, configs.Revision, caller.Services.GetRequiredService<PluginLogStream>().Revision);
        }, Read with { Description = "Lists connected configuration providers and plugin names for the dashboard." });
        services.AddQuasarRead<PluginSelection>(PluginId, "liveplugins.schema", Edit, (caller, input, _) =>
        {
            var config = FindPlugin(caller, input);
            byte[] bytes = Encoding.UTF8.GetBytes(config.ConfigJson);
            return Task.FromResult(new QuasarRead(_ => Task.FromResult<Stream>(new MemoryStream(bytes, writable: false)), "application/json",
                Length: bytes.Length));
        }, Read with { Description = "Reads the schema and current configuration of a selected standalone plugin." });
        services.AddQuasarUpload<PluginSelection, bool>(PluginId, "liveplugins.apply", Edit, AgentMessageLimits.MessageBytes, (caller, input, staged, _) =>
        {
            FindPlugin(caller, input);
            return Task.FromResult(new QuasarUpload<bool>(async token =>
            {
                using var reader = new StreamReader(staged, Encoding.UTF8, leaveOpen: true);
                var values = await reader.ReadToEndAsync(token);
                using var document = JsonDocument.Parse(values);
                if (document.RootElement.ValueKind != JsonValueKind.Object) throw new QuasarOperationException(400, "Plugin values must be a JSON object.");
                await caller.EnsureAuthorizedAsync(token);
                FindPlugin(caller, input);
                await caller.Services.GetRequiredService<PluginConfigService>().UpdatePluginConfigAsync(input.AgentId, input.PluginId, values, input.ConnectionId, token);
                return true;
            }));
        }, Read with { Effect = QuasarOperationEffect.Change, Description = "Applies new values to the selected standalone plugin on its original live connection." });
        services.AddQuasarRead<PluginLogRequest>(PluginId, "liveplugins.logs", View, (caller, input, _) =>
        {
            var logs = caller.Services.GetRequiredService<PluginLogStream>();
            var registry = caller.Services.GetRequiredService<AgentRegistry>();
            var servers = caller.Services.GetRequiredService<DedicatedServerCatalog>();
            var labels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var agents = registry.GetAgents().ToLookup(a => a.TelemetryKey, StringComparer.OrdinalIgnoreCase);
            foreach (var key in logs.GetUniqueNames())
            {
                var agent = agents[key].FirstOrDefault();
                if (agent?.IsCluster == true && !caller.User.CanQueryCluster(agent.ClusterId)) continue;
                labels[key] = agent is null ? servers.GetServer(key)?.DisplayName ?? key : AgentName(servers, agent);
            }
            DateTimeOffset? from = input.Range switch
            {
                "all" => null, "7d" => DateTimeOffset.UtcNow.AddDays(-7), "1h" => DateTimeOffset.UtcNow.AddHours(-1), _ => DateTimeOffset.UtcNow.AddDays(-1),
            };
            var entries = logs.Query(new() { UniqueName = input.Server, Plugin = input.Plugin, Level = input.Level, Text = input.Text, FromUtc = from, Limit = input.Limit })
                .Where(e => labels.ContainsKey(e.UniqueName)).ToArray();
            Func<PluginLogEntry, object> by = input.Sort switch
            { "server" => e => labels[e.UniqueName], "plugin" => e => e.Plugin, "level" => e => e.Level, _ => e => e.TimestampUtc };
            int size = Math.Clamp(input.PageSize, 1, 100);
            var sorted = input.Descending ? entries.OrderByDescending(by) : entries.OrderBy(by);
            var page = sorted.Skip(Math.Clamp(input.Page, 0, entries.Length / size) * size).Take(size)
                .Select(e => new PluginLogLine(e.TimestampUtc, labels[e.UniqueName], e.Level, e.Plugin, e.Message, e.Exception)).ToArray();
            var result = new PluginLogResult(page, entries.Length, labels.Select(p => new ConversionDestination(p.Key, p.Value)).ToArray(), logs.GetPlugins().ToArray());
            return Task.FromResult(new QuasarRead(_ => Task.FromResult<Stream>(new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(result, Json), writable: false)), "application/json"));
        }, Read with { Description = "Reads a filtered page of structured plugin logs from the worker." });
    }

    private static string AgentName(DedicatedServerCatalog servers, AgentRuntimeState agent)
    {
        if (agent.IsCluster) return $"{agent.ClusterId} / {agent.ClusterSlot} / {agent.ClusterNodeId} / epoch {agent.ClusterEpoch}";
        var name = servers.GetServer(agent.UniqueNameKey)?.DisplayName;
        return string.IsNullOrWhiteSpace(name) ? agent.ServerDisplayName : name;
    }

    private static PluginConfigData FindPlugin(QuasarOperationContext caller, PluginSelection input)
    {
        var registry = caller.Services.GetRequiredService<AgentRegistry>();
        var agent = registry.GetAgents().FirstOrDefault(a => a.IsConnected && a.AgentId == input.AgentId && a.ConnectionId == input.ConnectionId);
        if (agent is null) throw new QuasarOperationException(409, "The original agent connection is no longer available.");
        if (agent.IsCluster) throw new QuasarOperationException(403, "Manage plugin settings through the cluster.");
        return caller.Services.GetRequiredService<PluginConfigService>().GetConfigsForAgent(input.AgentId).FirstOrDefault(p => p.PluginId == input.PluginId)
            ?? throw new QuasarOperationException(404, "The plugin is no longer present.");
    }
}
