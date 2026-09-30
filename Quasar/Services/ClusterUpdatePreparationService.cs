using System.Text.Json;
using System.Text.Json.Nodes;
using Magnetar.Protocol.Runtime;
using Quasar.ClusterDeployment;
using Quasar.Models;

namespace Quasar.Services;

/// <summary>Builds a candidate with the selected installation and the cluster's retained world seed and topology.</summary>
public sealed class ClusterUpdatePreparationService(ClusterCatalog catalog, ClusterDependencyService dependencies,
    ClusterHostClient hosts, ClusterDeploymentService deployments, QuasarConfigProfileCatalog? profiles = null)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public Task<ClusterOperation> StageProfileSelectionAsync(string clusterId, string profileId,
        string actor, CancellationToken token) => catalog.WithLifecycleAsync(clusterId, async cluster =>
        {
            if (cluster.ActiveDeployment is null || cluster.Update is { Phase: not ClusterUpdatePhase.Complete }
                || cluster.PendingDeploymentHash is not null || cluster.PendingRestoreHash is not null)
                throw new InvalidOperationException("Finish the current setup, update or activation before changing profiles.");
            var current = profiles?.GetProfile(cluster.ConfigProfileId)
                ?? throw new InvalidOperationException("The active profile is unavailable; its deployed settings cannot be checked.");
            var selected = profiles.GetProfile(profileId)
                ?? throw new InvalidOperationException("Choose an existing configuration profile.");
            if (current.UpdatedAtUtc > cluster.ActiveDeployment.ActivatedAt)
                throw new InvalidOperationException("The active profile changed since deployment; its deployed world settings cannot be verified.");
            EnsureCompatibleProfileSelection(current, selected);
            string selectedHash = ProfileHash(selected);
            var request = cluster.Preparation ?? throw new InvalidOperationException("Complete guided setup before changing profiles.");
            var inputs = await dependencies.GetDeploymentInputsAsync(cluster, token);
            if (ClusterDeploymentFiles.Hash(JsonSerializer.SerializeToUtf8Bytes(inputs, Json)) != request.InputsSha256)
                throw new InvalidOperationException("Stage the selected release before changing profiles.");
            var spec = JsonNode.Parse(request.SpecificationJson)!.AsObject();
            spec["memberLimit"] = ClusterConversionService.MemberLimit(selected);
            spec["administrators"] = JsonSerializer.SerializeToNode(ClusterConversionService.Administrators(selected), Json);
            spec["selectedConfigProfileId"] = selected.ConfigProfileId;
            string json = spec.ToJsonString();
            string hash = ClusterDeploymentFiles.Hash(System.Text.Encoding.UTF8.GetBytes(json));
            request = request with { SpecificationJson = json, Hosts = request.Hosts.Select(h => h with {
                ConfigurationDirectory = Path.Combine(Path.GetDirectoryName(h.ConfigurationDirectory)!, "config-" + hash) }).ToArray() };
            var result = await deployments.PrepareAsync(clusterId, request, Guid.NewGuid().ToString("N"), actor, token);
            if (result.State == ClusterOperationState.Succeeded)
            {
                if (profiles.GetProfile(profileId) is not { } latest || ProfileHash(latest) != selectedHash)
                    throw new InvalidOperationException("The selected profile changed during preparation. Stage it again.");
                var deployment = result.Result!.Value.Deserialize<ClusterDeploymentRequest>(Json)
                    ?? throw new InvalidDataException("Host preparation returned no deployment.");
                await catalog.RecordSelectedReleasePreparationAsync(cluster, deployment, token, selected.ConfigProfileId, selectedHash);
            }
            return result;
        }, token);

    internal static string ProfileHash(QuasarConfigProfile profile) =>
        ClusterDeploymentFiles.Hash(JsonSerializer.SerializeToUtf8Bytes(profile, Json));

    internal static void EnsureCompatibleProfileSelection(QuasarConfigProfile current, QuasarConfigProfile selected)
    {
        ClusterConversionService.ValidateAdmission(selected);
        static JsonObject Settings(object value) => JsonSerializer.SerializeToNode(value, Json)!.AsObject();
        var currentRoot = Settings(current.RootSettings);
        var selectedRoot = Settings(selected.RootSettings);
        var currentSession = Settings(current.SessionSettings);
        var selectedSession = Settings(selected.SessionSettings);
        currentSession.Remove("maxPlayers");
        selectedSession.Remove("maxPlayers");
        if (!JsonNode.DeepEquals(currentRoot, selectedRoot) || !JsonNode.DeepEquals(currentSession, selectedSession)
            || JsonSerializer.Serialize(current.Mods, Json) != JsonSerializer.Serialize(selected.Mods, Json)
            || JsonSerializer.Serialize(current.Plugins, Json) != JsonSerializer.Serialize(selected.Plugins, Json))
            throw new InvalidOperationException("This profile changes world settings, administrators, mods or plugin selection. The current deployment format cannot apply those changes to an existing world. Select a profile differing only in max players.");
    }

    public Task<ClusterOperation> StagePluginConfigurationAsync(string clusterId, string pluginId,
        string configType, string values, string actor, CancellationToken token) =>
        catalog.WithLifecycleAsync(clusterId, async cluster =>
        {
            if (cluster.ActiveDeployment is null || cluster.Update is { Phase: not ClusterUpdatePhase.Complete }
                || cluster.PendingDeploymentHash is not null || cluster.PendingRestoreHash is not null)
                throw new InvalidOperationException("Finish the current setup, update or activation before changing configuration.");
            var request = cluster.Preparation ?? throw new InvalidOperationException("Complete guided setup before changing plugin configuration.");
            var inputs = await dependencies.GetDeploymentInputsAsync(cluster, token);
            if (ClusterDeploymentFiles.Hash(JsonSerializer.SerializeToUtf8Bytes(inputs, Json)) != request.InputsSha256)
                throw new InvalidOperationException("Stage the selected release before changing its plugin configuration.");
            var spec = JsonNode.Parse(request.SpecificationJson)!;
            SetPluginValues(spec, pluginId, configType, values);
            string json = spec.ToJsonString();
            string hash = ClusterDeploymentFiles.Hash(System.Text.Encoding.UTF8.GetBytes(json));
            request = request with { SpecificationJson = json, Hosts = request.Hosts.Select(h => h with {
                ConfigurationDirectory = Path.Combine(Path.GetDirectoryName(h.ConfigurationDirectory)!, "config-" + hash) }).ToArray() };
            var result = await deployments.PrepareAsync(clusterId, request, Guid.NewGuid().ToString("N"), actor, token);
            if (result.State == ClusterOperationState.Succeeded)
            {
                var deployment = result.Result!.Value.Deserialize<ClusterDeploymentRequest>(Json)
                    ?? throw new InvalidDataException("Host preparation returned no deployment.");
                await catalog.RecordSelectedReleasePreparationAsync(cluster, deployment, token,
                    cluster.PreparedProfile?.ProfileId, cluster.PreparedProfile?.ProfileSha256);
            }
            return result;
        }, token);

    internal static void SetPluginValues(System.Text.Json.Nodes.JsonNode specification, string pluginId, string configType, string values)
    {
        var plugin = specification["pluginConfigurations"]![pluginId]!;
        var config = plugin["configurations"] is System.Text.Json.Nodes.JsonArray configs
            ? configs.Single(c => c?["configType"]?.GetValue<string>() == configType)! : plugin;
        config["configuration"]!["values"] = JsonNode.Parse(values) as JsonObject
            ?? throw new InvalidDataException("Plugin settings must be a JSON object.");
    }

    public async Task<ClusterOperation> PrepareAsync(string clusterId, string actor, CancellationToken token)
    {
        var cluster = catalog.GetCluster(clusterId) ?? throw new KeyNotFoundException(clusterId);
        var previous = cluster.Preparation ?? throw new InvalidOperationException("No saved preparation exists. Import a deployment request first.");
        if (cluster.ActiveDeployment is null || cluster.PackageSelection is null || cluster.DependencyManifestSha256 is null)
            throw new InvalidOperationException("Select a cluster package and freeze its dependencies first.");
        if (cluster.Update is { Phase: not ClusterUpdatePhase.Complete }
            || cluster.PendingDeploymentHash is not null || cluster.PendingRestoreHash is not null)
            throw new InvalidOperationException("Finish the current cluster update or activation first.");
        if (previous.Hosts.Length != cluster.ActiveDeployment.Hosts.Length
            || previous.Hosts.Any(host => cluster.ActiveDeployment.Hosts.All(active => active.HostId != host.HostId)))
            throw new InvalidOperationException("Saved preparation has a different Host inventory. Import a current deployment request.");

        var inputs = await dependencies.GetDeploymentInputsAsync(cluster, token);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(inputs, Json);
        string hash = ClusterDeploymentFiles.Hash(bytes);
        string workspace = Path.Combine(MagnetarPaths.GetQuasarDirectory(), "ClusterUpdates", Guid.NewGuid().ToString("N"));
        try
        {
            var installed = await ClusterDeploymentFiles.PrepareAsync(bytes, hash, Path.Combine(workspace, "installation"), token);
            string archive = Path.Combine(workspace, "installation.tar");
            await using (var output = File.Create(archive))
                await ClusterDeploymentFiles.ExportAsync(installed.Directory, output, token);
            var transfer = Guid.NewGuid();
            var preparationHosts = new List<ClusterPreparationHost>();
            foreach (var old in previous.Hosts)
            {
                var active = cluster.ActiveDeployment.Hosts.Single(host => host.HostId == old.HostId);
                var target = cluster.Clone();
                target.HostCommandUrl = active.CommandUrl;
                target.HostCommandTokenEnvironmentVariable = active.TokenEnvironmentVariable;
                var remote = await hosts.TransferConversionInputAsync(target, transfer, "installation", archive, hash, token);
                if (remote.HostId != old.HostId) throw new InvalidDataException("Transfer returned another Host identity.");
                preparationHosts.Add(old with { CommandUrl = active.CommandUrl,
                    TokenEnvironmentVariable = active.TokenEnvironmentVariable,
                    InstallationDirectory = remote.Directory, ConfigurationDirectory = remote.ConfigurationDirectory });
            }
            var specification = JsonNode.Parse(previous.SpecificationJson)!.AsObject();
            ClusterConversionService.AddLocalSharedStorage(specification);
            var request = previous with { InputsSha256 = hash, SpecificationJson = specification.ToJsonString(Json),
                Hosts = preparationHosts.ToArray() };
            if (catalog.GetCluster(clusterId)?.PackageSelection != cluster.PackageSelection
                || catalog.GetCluster(clusterId)?.DependencyManifestSha256 != cluster.DependencyManifestSha256)
                throw new InvalidOperationException("Package inputs changed during transfer. Prepare again.");
            var result = await deployments.PrepareAsync(clusterId, request, Guid.NewGuid().ToString("N"), actor, token);
            if (result.State == ClusterOperationState.Succeeded)
            {
                var deployment = result.Result!.Value.Deserialize<ClusterDeploymentRequest>(Json)
                    ?? throw new InvalidDataException("Host preparation returned no deployment.");
                await catalog.RecordSelectedReleasePreparationAsync(cluster, deployment, token,
                    cluster.PreparedProfile?.ProfileId, cluster.PreparedProfile?.ProfileSha256);
            }
            return result;
        }
        finally
        {
            if (Directory.Exists(workspace)) Directory.Delete(workspace, recursive: true);
        }
    }
}
