using System.Text.Json;
using Magnetar.Protocol.Transport;
using Microsoft.AspNetCore.Authorization;
using Quasar.Client.Clusters;
using Quasar.Models;
using Quasar.Plugin.Abstractions.Operations;
using Quasar.Services.Auth;
using Admin = CometWorks.ClusterGateway.AdminContract.V1;

namespace Quasar.Services.Plugins;

internal static partial class QuasarClusterDetailOperations
{
    private static readonly string[] EditSettings = [QuasarPolicyNames.ClusterQuery, QuasarPolicyNames.ClusterManage, QuasarPolicyNames.CanEditConfigs];

    private static void AddClusterSettings(this IServiceCollection services)
    {
        services.AddQuasarRead<ClusterPanelRequest>(Plugin, "cluster.dashboard", Query, async (caller, input, token) =>
        {
            var cluster = Cluster(caller, input.UniqueName);
            var release = caller.Services.GetRequiredService<ClusterReleaseMonitor>().GetSnapshot();
            var setup = caller.Services.GetRequiredService<ClusterSetupService>().GetStatus(cluster.UniqueName);
            var profiles = caller.Services.GetRequiredService<QuasarConfigProfileCatalog>();
            var worlds = caller.Services.GetRequiredService<QuasarWorldTemplateCatalog>();
            var auth = caller.Services.GetRequiredService<IAuthorizationService>();
            async Task<bool> Allowed(string policy) => (await auth.AuthorizeAsync(caller.User, null, policy)).Succeeded;
            bool manage = await Allowed(QuasarPolicyNames.ClusterManage);
            bool edit = manage && await Allowed(QuasarPolicyNames.CanEditConfigs);
            var spec = cluster.Preparation?.SpecificationJson;
            return JsonRead(new ClusterDashboard(cluster.UniqueName, cluster.DisplayName, cluster.GoalState == DedicatedServerGoalState.On, cluster.Gateway is not null,
                cluster.Gateway is not null && !string.IsNullOrWhiteSpace(cluster.HostCommandUrl), cluster.ActiveDeployment is not null,
                ClusterReleaseMonitor.GetDeployedVersion(cluster), release.Release?.Version, ClusterReleaseMonitor.IsUpdateAvailable(cluster, release.Release),
                release.LastCheckedUtc, release.Error, setup?.Phase, setup?.Error, manage, await Allowed(QuasarPolicyNames.ClusterMaintenance), edit,
                cluster.ConfigProfileId, cluster.PreparedProfile?.ProfileId, cluster.ShutdownGracePeriodSeconds, ClusterConversionService.HotSpares(spec),
                ClusterConversionService.RegularNodes(spec).Select(h => new HostNodeCount(h.HostId, h.RegularNodes)).ToArray(),
                edit ? profiles.GetProfiles().Select(p => new ClusterProfileChoice(p.ConfigProfileId, p.Name,
                    QuasarDefaultPlugins.Pending(p).Select(d => d.DisplayName).ToArray())).ToArray() : [],
                profiles.GetProfile(cluster.ConfigProfileId)?.Name ?? cluster.ConfigProfileId,
                worlds.GetTemplate(cluster.WorldTemplateId)?.Name ?? cluster.WorldTemplateId));
        }, Read with { Description = "Returns settings and status for the selected cluster." });
        services.AddQuasarOperation<ClusterPanelRequest, bool>(Plugin, "cluster.start.available", Manage, (caller, input, token) =>
            caller.Services.GetRequiredService<ClusterCommandService>().CanStartAsync(Cluster(caller, input.UniqueName), token), Read with { Description = "Checks whether the cluster can be started." });
        services.AddQuasarOperation<ClusterPanelRequest, SaveClusterSettings>(Plugin, "cluster.settings.save", EditSettings, async (caller, input, token) =>
        {
            var c = Cluster(caller, input.UniqueName);
            var settings = input.Arguments<SaveClusterSettings>();
            var saved = await caller.Services.GetRequiredService<ClusterCatalog>().UpdateAdministrationAsync(c.UniqueName, new(settings.DisplayName, settings.ShutdownGracePeriodSeconds), token);
            return new(saved.DisplayName, saved.ShutdownGracePeriodSeconds);
        }, Change with { Description = "Saves the selected cluster's reviewed settings." });
        services.AddQuasarOperation<ClusterPanelRequest, bool>(Plugin, "cluster.settings.defaults", EditSettings, async (caller, input, token) =>
        {
            _ = Cluster(caller, input.UniqueName);
            var change = input.Arguments<ClusterDefaultOffer>();
            var profiles = caller.Services.GetRequiredService<QuasarConfigProfileCatalog>();
            var profile = profiles.GetProfile(change.ProfileId) ?? throw new QuasarOperationException(404, "Profile not found.");
            QuasarDefaultPlugins.Apply(profile, change.Enable);
            await profiles.UpsertAsync(profile, token);
            return true;
        }, Change with { Description = "Applies the selected cluster settings defaults." });
        RegisterStage("cluster.settings.profile", "Changes the configuration profile for the selected cluster.", (s, id, i, actor, t) => s.StageProfileSelectionAsync(id, i.Arguments<string>(), actor, t));
        RegisterStage("cluster.settings.spares", "Updates the cluster's spare node settings.", (s, id, i, actor, t) => s.StageHotSparesAsync(id, i.Arguments<int>(), actor, t));
        RegisterStage("cluster.settings.nodes", "Changes node assignments for the selected cluster.", (s, id, i, actor, t) =>
        {
            var nodes = i.Arguments<HostNodeCount>();
            return s.StageRegularNodesAsync(id, nodes.HostId, nodes.RegularNodes, actor, t);
        });

        void RegisterStage(string name, string description, Func<ClusterUpdatePreparationService,string,ClusterPanelRequest,string,CancellationToken,Task<ClusterOperation>> stage) =>
            services.AddQuasarOperation<ClusterPanelRequest, PanelOperation>(Plugin, name, EditSettings, async (caller, input, token) =>
                Project(await stage(caller.Services.GetRequiredService<ClusterUpdatePreparationService>(), Cluster(caller, input.UniqueName).UniqueName,
                    input, caller.User.Identity?.Name ?? "web-ui", token)), Change with { Description = description, Deadline = QuasarOperationMetadata.MaximumDeadline });

        RegisterRead(services, "cluster.panel.diagnostics", "Returns the selected cluster's gateway diagnostics.", Manage, (g,c,_,t) => g.GetDiagnosticsAsync(c,t));
        RegisterRead(services, "cluster.panel.artifacts", "Returns the gateway's published artifact inventory.", Manage, (g,c,_,t) => g.GetArtifactsAsync(c,t));
        RegisterRead(services, "cluster.panel.handover", "Returns the current gateway handover configuration.", Manage, (g,c,_,t) => g.GetHandoverConfigAsync(c,t));
        RegisterRead(services, "cluster.panel.rotation", "Returns the current rotation configuration.", Manage, (g,c,_,t) => g.GetRotationAsync(c,t));
        RegisterRead(services, "cluster.panel.policy", "Returns current gateway policy settings.", Manage, (g,c,_,t) => g.GetPolicyAsync(c,t));
        services.AddQuasarUpload<ClusterPanelRequest, PanelOperation>(Plugin, "cluster.panel.command", Manage, AgentMessageLimits.MessageBytes,
            (caller, input, stream, _) =>
            {
                var cluster = Cluster(caller, input.UniqueName);
                return Task.FromResult(new QuasarUpload<PanelOperation>(async token =>
                {
                    var command = await JsonSerializer.DeserializeAsync<ClusterAdminCommand>(stream, cancellationToken: token)
                        ?? throw new QuasarOperationException(400, "A cluster command is required.");
                    await caller.EnsureAuthorizedAsync(token);
                    cluster = Cluster(caller, input.UniqueName);
                    if (command.RequiresAdministrator && !(await caller.Services.GetRequiredService<IAuthorizationService>()
                        .AuthorizeAsync(caller.User, null, QuasarPolicyNames.ClusterMaintenance)).Succeeded)
                        throw new QuasarOperationException(403, "Cluster maintenance permission is required.");
                    if (string.Equals(command.Action?.Trim(), "lobby-set", StringComparison.OrdinalIgnoreCase)
                        && command.Parameters?.Deserialize<Admin.LobbyUpdate>(new JsonSerializerOptions(JsonSerializerDefaults.Web))?.BlueprintXml is not null)
                        throw new QuasarOperationException(400, "Use the lobby blueprint upload.");
                    return await Submit(caller, cluster, command, token);
                }));
            }, Change with { Description = "Submits a validated administrative command to the selected gateway." });
        services.AddQuasarOperation<ClusterPanelRequest, PanelOperation>(Plugin, "cluster.panel.goal", Manage, async (caller, input, token) =>
        {
            var cluster = Cluster(caller, input.UniqueName);
            return Project(await caller.Services.GetRequiredService<ClusterCommandService>().SetGoalAsync(cluster.UniqueName,
                new(input.Arguments<bool>() ? DedicatedServerGoalState.On : DedicatedServerGoalState.Off), Guid.NewGuid().ToString("N"),
                caller.User.Identity?.Name ?? "web-ui", token));
        }, Change with { Description = "Updates the requested cluster runtime goal." });
    }
}
