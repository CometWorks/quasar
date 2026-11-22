using System.Text;
using System.Text.Json;
using Magnetar.Protocol.Transport;
using Quasar.Client.Clusters;
using Quasar.Models;
using Quasar.Plugin.Abstractions.Operations;

namespace Quasar.Services.Plugins;

internal static partial class QuasarClusterDetailOperations
{
    private static void AddClusterContent(this IServiceCollection services)
    {
        services.AddQuasarOperation<ClusterPanelRequest, ClusterContentView>(Plugin, "cluster.content.read", Query, (caller, input, _) =>
        {
            var cluster = Cluster(caller, input.UniqueName);
            var content = caller.Services.GetRequiredService<ClusterContentMonitor>();
            var snapshot = content.GetSnapshot(cluster);
            return Task.FromResult(new ClusterContentView(content.ScheduledChecksEnabled, cluster.QueuedContentUpdates is not null, snapshot.CheckedAt,
                snapshot.Items.Select(i => new ContentItem(i.Kind, i.Id, i.Name, i.PinnedVersion, i.BaselineVersion, i.LatestVersion, i.ObservedAt,
                    i.Error, i.DetailsUrl, i.HasUpdate)).ToArray(), snapshot.InventoryError, snapshot.CheckError));
        }, Read with { Description = "Returns the state of cluster content and pending changes." });
        services.AddQuasarOperation<ClusterPanelRequest, bool>(Plugin, "cluster.content.check", Manage, async (caller, input, token) =>
        {
            _ = Cluster(caller, input.UniqueName);
            await caller.Services.GetRequiredService<ClusterContentMonitor>().CheckNowAsync(token);
            return true;
        }, Change with { Description = "Checks the selected cluster for content changes.", Deadline = QuasarOperationMetadata.MaximumDeadline });
        services.AddQuasarOperation<ClusterPanelRequest, bool>(Plugin, "cluster.content.queue", Manage, async (caller, input, token) =>
        {
            var cluster = Cluster(caller, input.UniqueName);
            var snapshot = caller.Services.GetRequiredService<ClusterContentMonitor>().GetSnapshot(cluster);
            await caller.Services.GetRequiredService<ClusterCatalog>().QueueContentUpdatesAsync(cluster.UniqueName, snapshot, token);
            return true;
        }, Change with { Description = "Queues selected content changes for the next deployment." });
        services.AddQuasarRead<ClusterPanelRequest>(Plugin, "cluster.settings.plugins", Manage, (caller, input, _) =>
        {
            var plugins = PreparedPlugins(Cluster(caller, input.UniqueName), caller.Services.GetRequiredService<QuasarConfigProfileCatalog>());
            var bytes = JsonSerializer.SerializeToUtf8Bytes(plugins, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            return Task.FromResult(new QuasarRead(_ => Task.FromResult<Stream>(new MemoryStream(bytes, false)), "application/json", Length: bytes.Length));
        }, Read with { Description = "Returns the selected cluster's staged plugin settings." });
        services.AddQuasarUpload<ClusterPanelRequest, bool>(Plugin, "cluster.settings.plugin.stage", Manage, AgentMessageLimits.MessageBytes, (caller, input, stream, cancellationToken) =>
        {
            _ = Cluster(caller, input.UniqueName);
            var target = input.Arguments<ClusterPluginTarget>();
            return Task.FromResult(new QuasarUpload<bool>(async token =>
            {
                using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
                string values = await reader.ReadToEndAsync(token);
                await caller.EnsureAuthorizedAsync(token);
                var cluster = Cluster(caller, input.UniqueName);
                var operation = await caller.Services.GetRequiredService<ClusterUpdatePreparationService>().StagePluginConfigurationAsync(cluster.UniqueName,
                    target.PluginId, target.ConfigType, values, caller.User.Identity?.Name ?? "web-ui", token);
                if (operation.State != ClusterOperationState.Succeeded)
                    throw new QuasarOperationException(409, operation.Error?.Message ?? "Configuration preparation is incomplete.");
                return true;
            }));
        }, Change with { Description = "Stages a selected plugin's configuration for deployment.", Deadline = QuasarOperationMetadata.MaximumDeadline });
    }

    private static ClusterPluginConfig[] PreparedPlugins(ClusterDefinition cluster, QuasarConfigProfileCatalog profiles)
    {
        try
        {
            using var spec = JsonDocument.Parse(cluster.Preparation?.SpecificationJson ?? "{}");
            if (!spec.RootElement.TryGetProperty("pluginConfigurations", out var plugins)) return [];
            var profile = profiles.GetProfile(cluster.ConfigProfileId);
            return plugins.EnumerateObject().SelectMany(plugin =>
            {
                bool multiple = plugin.Value.TryGetProperty("configurations", out var configs);
                string label = plugin.Name.Equals("direct-transport", StringComparison.OrdinalIgnoreCase) ? "Cluster networking"
                    : profile?.Plugins.FirstOrDefault(p => p.PluginId.Equals(plugin.Name, StringComparison.OrdinalIgnoreCase))?.DisplayName
                      ?? plugin.Name switch { "dotnet-compat" => ".NET Compatibility", "linux-compat" => "Linux Compatibility", "quasar-agent" => "Quasar Agent", _ => plugin.Name };
                return (multiple ? configs.EnumerateArray().ToArray() : [plugin.Value])
                    .Where(config => config.TryGetProperty("configuration", out _))
                    .Select(config => new ClusterPluginConfig(plugin.Name, label + (multiple ? " / " + config.GetProperty("configType").GetString() : ""),
                        config.GetProperty("configType").GetString() ?? "", config.GetProperty("configuration").GetRawText()));
            }).ToArray();
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException) { return []; }
    }
}
