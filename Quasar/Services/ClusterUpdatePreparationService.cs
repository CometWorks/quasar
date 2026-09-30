using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Magnetar.Protocol.Runtime;
using Quasar.ClusterDeployment;
using Quasar.Models;

namespace Quasar.Services;

/// <summary>Builds a candidate with the selected installation and the cluster's retained world seed and topology.</summary>
public sealed class ClusterUpdatePreparationService(ClusterCatalog catalog, ClusterDependencyService dependencies,
    ClusterHostClient hosts, ClusterDeploymentService deployments, QuasarConfigProfileCatalog profiles,
    ClusterSetupService setup, ClusterWorkshopModStager modStager, QuasarWorkshopModResolver workshop)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public Task<ClusterOperation> StageProfileSelectionAsync(string clusterId, string profileId,
        string actor, CancellationToken token) => catalog.WithLifecycleAsync(clusterId, async cluster =>
        {
            if (cluster.ActiveDeployment is null || cluster.Update is { Phase: not ClusterUpdatePhase.Complete }
                || cluster.PendingDeploymentHash is not null || cluster.PendingRestoreHash is not null)
                throw new InvalidOperationException("Finish the current setup, update or activation before changing profiles.");
            var activeDeployment = cluster.ActiveDeployment;
            var current = profiles.GetProfile(cluster.ConfigProfileId)
                ?? throw new InvalidOperationException("The active profile is unavailable; its deployed settings cannot be checked.");
            var selected = profiles.GetProfile(profileId)
                ?? throw new InvalidOperationException("Choose an existing configuration profile.");
            EnsureCompatibleProfileSelection(current, selected);
            string selectedHash = ProfileHash(selected);
            var request = cluster.Preparation ?? throw new InvalidOperationException("Complete guided setup before changing profiles.");
            string work = Path.Combine(MagnetarPaths.GetQuasarDirectory(), "ClusterUpdates", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);
            ClusterWorldFiles.Private(work);
            try
            {
                var inputs = await dependencies.GetDeploymentInputsAsync(cluster, token);
                ClusterDeploymentFiles.RequireProfileUpdates(inputs.PackageDirectory);
                if (ClusterDeploymentFiles.Hash(JsonSerializer.SerializeToUtf8Bytes(inputs, Json)) != request.InputsSha256)
                    throw new InvalidOperationException("Stage the selected release before changing profiles.");

                var resolved = await workshop.ResolveDependenciesAsync(selected.Mods, sortLoadOrder: true, cancellationToken: token);
                var unresolved = resolved.Warnings.Where(w => !w.StartsWith("Reordered dependency ", StringComparison.Ordinal)).ToArray();
                if (unresolved.Length != 0)
                    throw new InvalidOperationException("Resolve Workshop dependencies before staging: " + string.Join(" ", unresolved));
                var effective = JsonSerializer.Deserialize<QuasarConfigProfile>(JsonSerializer.Serialize(selected, Json), Json)!;
                effective.Mods = resolved.Mods.ToList();
                string mods = await modStager.StageAsync(effective.Mods, work, token);
                var modFiles = (await ClusterDeploymentFiles.InspectAsync(mods, token))
                    .ToDictionary(p => p.Key, p => p.Value.Sha256, StringComparer.Ordinal);
                string modArchive = Path.Combine(work, "mods.tar");
                string modHash = await ClusterWorldFiles.PackAsync(mods, modArchive, token, requireCheckpoint: false);

                string sourceWorld = Path.Combine(work, "profile-source");
                Directory.CreateDirectory(sourceWorld);
                string checkpoint = Path.Combine(sourceWorld, "Sandbox.sbc");
                await File.WriteAllTextAsync(checkpoint, new XDocument(new XElement("MyObjectBuilder_Checkpoint",
                    new XElement("Settings"), new XElement("Mods"))).ToString(), token);
                await WorldSandboxConfigEditor.WriteProfileAsync(checkpoint, effective, cluster.DisplayName, token);
                string patch = XDocument.Load(checkpoint).ToString(SaveOptions.DisableFormatting);
                string exported = Path.Combine(work, "exported");
                var excluded = await setup.ExportProfilePluginsAsync(cluster.UniqueName, cluster.DisplayName,
                    selected.ConfigProfileId, sourceWorld, effective, work, exported, token);
                if (excluded.Count != 0)
                    throw new InvalidOperationException("Selected plugins lack pinned provenance: " + string.Join(", ", excluded));
                var sources = await dependencies.ReusableSourcesAsync(cluster, token);
                var exportedData = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(exported, "preparation.json"), token))!.AsObject();
                string sdk = Path.Combine(sources["Magnetar"], "Libraries/MagnetarInterim/PluginSdk.dll");
                if (exportedData["pluginSdkSha256"]?.GetValue<string>() !=
                    ClusterDeploymentFiles.Hash(await File.ReadAllBytesAsync(sdk, token)))
                    throw new InvalidOperationException("Magnetar's plugin exporter and the selected dependency snapshot use different PluginSdk builds. Stage a matching release first.");
                sources["CommonPlugins"] = Path.Combine(exported, "CommonPlugins");
                sources["DirectTransport"] = Path.Combine(exported, "DirectTransport");
                var candidate = await dependencies.InspectAsync(cluster, sources, token);
                await dependencies.StageAsync(cluster, new(candidate.ManifestSha256, cluster.PackageSelection!.Revision, null), sources, token);
                var stagedCluster = cluster.Clone();
                stagedCluster.DependencyManifestSha256 = candidate.ManifestSha256;
                inputs = await dependencies.GetDeploymentInputsAsync(stagedCluster, token);
                byte[] inputBytes = JsonSerializer.SerializeToUtf8Bytes(inputs, Json);
                string inputsHash = ClusterDeploymentFiles.Hash(inputBytes);
                var installed = await ClusterDeploymentFiles.PrepareAsync(inputBytes, inputsHash, Path.Combine(work, "installation"), token);
                string archive = Path.Combine(work, "installation.tar");
                await using (var output = File.Create(archive))
                    await ClusterDeploymentFiles.ExportAsync(installed.Directory, output, token);

                var spec = JsonNode.Parse(request.SpecificationJson)!.AsObject();
                spec["memberLimit"] = ClusterConversionService.MemberLimit(effective);
                spec["administrators"] = JsonSerializer.SerializeToNode(ClusterConversionService.Administrators(effective), Json);
                spec["selectedConfigProfileId"] = effective.ConfigProfileId;
                spec["profileSettingsXml"] = patch;
                spec["modFiles"] = JsonSerializer.SerializeToNode(modFiles, Json);
                var canonical = exportedData["pluginConfigurations"]?.DeepClone() as JsonObject
                    ?? throw new InvalidDataException("Magnetar did not export plugin configuration.");
                PreservePluginValues(spec["pluginConfigurations"] as JsonObject, canonical);
                spec["pluginConfigurations"] = canonical;
                string json = spec.ToJsonString();
                string hash = ClusterDeploymentFiles.Hash(System.Text.Encoding.UTF8.GetBytes(json));
                var transfer = Guid.NewGuid();
                var preparationHosts = new List<ClusterPreparationHost>();
                foreach (var old in request.Hosts)
                {
                    var active = activeDeployment.Hosts.Single(h => h.HostId == old.HostId);
                    var target = cluster.Clone();
                    target.HostCommandUrl = active.CommandUrl;
                    target.HostCommandTokenEnvironmentVariable = active.TokenEnvironmentVariable;
                    var installation = await hosts.TransferConversionInputAsync(target, transfer, "installation", archive, inputsHash, token);
                    var payload = await hosts.TransferConversionInputAsync(target, transfer, "mods", modArchive, modHash, token);
                    if (installation.HostId != old.HostId || payload.HostId != old.HostId)
                        throw new InvalidDataException("Transfer returned another Host identity.");
                    preparationHosts.Add(old with { CommandUrl = active.CommandUrl,
                        TokenEnvironmentVariable = active.TokenEnvironmentVariable, InstallationDirectory = installation.Directory,
                        ModDirectory = payload.Directory,
                        ConfigurationDirectory = Path.Combine(Path.GetDirectoryName(old.ConfigurationDirectory)!, "config-" + hash) });
                }
                request = request with { InputsSha256 = inputsHash, SpecificationJson = json, Hosts = preparationHosts.ToArray() };
                await catalog.SelectDependenciesAsync(clusterId,
                    new(candidate.ManifestSha256, cluster.PackageSelection.Revision, cluster.DependencyManifestSha256), token);
                cluster = catalog.GetCluster(clusterId)!;
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
            }
            finally { if (Directory.Exists(work)) Directory.Delete(work, true); }
        }, token);

    internal static string ProfileHash(QuasarConfigProfile profile) =>
        ClusterDeploymentFiles.Hash(JsonSerializer.SerializeToUtf8Bytes(profile, Json));

    internal static bool RequiresProfileMigration(ClusterDefinition cluster, string revision)
    {
        if (cluster.Preparation is not { } preparation
            || ClusterDependencyService.DeploymentRevision(preparation) != revision) return false;
        using var spec = JsonDocument.Parse(preparation.SpecificationJson);
        return spec.RootElement.TryGetProperty("profileSettingsXml", out _)
            || spec.RootElement.TryGetProperty("modFiles", out _);
    }

    internal static void EnsureCompatibleProfileSelection(QuasarConfigProfile current, QuasarConfigProfile selected)
    {
        ClusterConversionService.ValidateAdmission(selected);
        static JsonObject Settings(object value) => JsonSerializer.SerializeToNode(value, Json)!.AsObject();
        var currentRoot = Settings(current.RootSettings);
        var selectedRoot = Settings(selected.RootSettings);
        var supportedRoot = Settings(new QuasarWorldRootSettings());
        currentRoot.Remove("administrators");
        selectedRoot.Remove("administrators");
        supportedRoot.Remove("administrators");
        if (!JsonNode.DeepEquals(currentRoot, selectedRoot) || !JsonNode.DeepEquals(selectedRoot, supportedRoot))
            throw new InvalidOperationException("Cluster network and dedicated-server root settings cannot change through a preserved-world profile update. Change session settings, administrators, mods or plugins instead.");
    }

    private static void PreservePluginValues(JsonObject? previous, JsonObject canonical)
    {
        if (previous is null) return;
        foreach (var (id, plugin) in canonical)
        {
            if (plugin is not JsonObject next || previous[id] is not JsonObject old) continue;
            static IEnumerable<JsonObject> Configs(JsonObject value) => value["configurations"] is JsonArray array
                ? array.OfType<JsonObject>() : [value];
            foreach (var config in Configs(next))
            {
                string? type = config["configType"]?.GetValue<string>();
                var saved = Configs(old).FirstOrDefault(c => c["configType"]?.GetValue<string>() == type);
                if (saved?["configuration"]?["values"] is JsonObject values && config["configuration"] is JsonObject target
                    && saved["configuration"]?["schema"] is JsonNode oldSchema && target["schema"] is JsonNode newSchema
                    && JsonNode.DeepEquals(oldSchema, newSchema))
                    target["values"] = values.DeepClone();
            }
        }
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
