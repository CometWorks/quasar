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
    ClusterSetupService setup, ClusterWorkshopModStager modStager, QuasarWorkshopModResolver workshop,
    ManagedDedicatedServerRuntimeResolver runtime)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const string PluginSdkPath = "Libraries/MagnetarInterim/PluginSdk.dll";

    public Task<ClusterOperation> StageProfileSelectionAsync(string clusterId, string profileId,
        string actor, CancellationToken token, IProgress<string>? progress = null) => catalog.WithLifecycleAsync(clusterId, async cluster =>
        {
            if (cluster.ActiveDeployment is null || cluster.Update is { Phase: not ClusterUpdatePhase.Complete }
                || cluster.PendingDeploymentHash is not null || cluster.PendingRestoreHash is not null)
                throw new InvalidOperationException("Finish the current setup, update or activation before changing profiles.");
            var activeDeployment = cluster.ActiveDeployment;
            var queued = cluster.QueuedContentUpdates;
            if (queued is not null && queued.DeploymentRevision != activeDeployment.Revision)
                throw new InvalidOperationException("Queued content belongs to another deployment. Check and queue content updates again.");
            var current = string.IsNullOrEmpty(cluster.ConfigProfileId) ? new QuasarConfigProfile()
                : profiles.GetProfile(cluster.ConfigProfileId)
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
                progress?.Report("Verifying the selected release and dependency pins...");
                var inputs = await dependencies.GetDeploymentInputsAsync(cluster, token);
                ClusterDeploymentFiles.RequireProfileUpdates(inputs.PackageDirectory);
                // The retained seed/topology can belong to the old release. This path rebuilds
                // content and canonical plugin configuration against the selected installation.
                if (activeDeployment.Provenance is null)
                    await catalog.RecordActiveProvenanceAsync(cluster, token);

                progress?.Report("Resolving the profile's Workshop dependencies...");
                var requestedMods = selected.Mods.ToList();
                // Magnetar adds its Steam companion implicitly. Pin it with the rest of
                // the managed payload so profile migration cannot drop it or download at launch.
                if (!selected.RootSettings.CrossPlatform && requestedMods.All(m => m.WorkshopId != 3750200326))
                    requestedMods.Add(new() { WorkshopId = 3750200326, DisplayName = "MagnetarMod" });
                var resolved = await workshop.ResolveDependenciesAsync(requestedMods, sortLoadOrder: true, cancellationToken: token);
                var unresolved = resolved.Warnings.Where(w => !w.StartsWith("Reordered dependency ", StringComparison.Ordinal)).ToArray();
                if (unresolved.Length != 0)
                    throw new InvalidOperationException("Resolve Workshop dependencies before staging: " + string.Join(" ", unresolved));
                var effective = JsonSerializer.Deserialize<QuasarConfigProfile>(JsonSerializer.Serialize(selected, Json), Json)!;
                effective.Mods = resolved.Mods.ToList();
                var observations = await workshop.CheckUpdatesAsync(effective.Mods.Select(m => m.WorkshopId), token);
                if (queued is not null) ValidateQueuedMods(queued, effective.Mods, observations);
                string mods = await modStager.StageAsync(effective.Mods, work, token, progress, observations);
                var afterDownload = await workshop.CheckUpdatesAsync(effective.Mods.Select(m => m.WorkshopId), token);
                foreach (var mod in effective.Mods)
                    if (ClusterWorkshopModStager.ObservedVersion(mod.WorkshopId, observations)
                        != ClusterWorkshopModStager.ObservedVersion(mod.WorkshopId, afterDownload))
                        throw new InvalidOperationException($"Workshop item {mod.WorkshopId} changed during preparation. Check updates and stage again.");
                var modFiles = (await ClusterDeploymentFiles.InspectAsync(mods, token))
                    .ToDictionary(p => p.Key, p => p.Value.Sha256, StringComparer.Ordinal);
                string modArchive = Path.Combine(work, "mods.tar");

                string sourceWorld = Path.Combine(work, "profile-source");
                Directory.CreateDirectory(sourceWorld);
                string checkpoint = Path.Combine(sourceWorld, "Sandbox.sbc");
                await File.WriteAllTextAsync(checkpoint, new XDocument(new XElement("MyObjectBuilder_Checkpoint",
                    new XElement("Settings"), new XElement("Mods"))).ToString(), token);
                await WorldSandboxConfigEditor.WriteProfileAsync(checkpoint, effective, cluster.DisplayName, token);
                string patch = XDocument.Load(checkpoint).ToString(SaveOptions.DisableFormatting);
                string exported = Path.Combine(work, "exported");
                progress?.Report("Preparing pinned plugins and their configuration...");
                var excluded = await setup.ExportProfilePluginsAsync(cluster.UniqueName, cluster.DisplayName,
                    selected.ConfigProfileId, sourceWorld, effective, work, exported, token);
                if (excluded.Count != 0)
                    throw new InvalidOperationException("Selected plugins lack pinned provenance: " + string.Join(", ", excluded));
                var sources = await dependencies.ReusableSourcesAsync(cluster, token);
                var exportedData = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(exported, "preparation.json"), token))!.AsObject();
                if (queued is not null) ValidateQueuedPlugins(queued, exportedData);
                string? exporterSdk = exportedData["pluginSdkSha256"]?.GetValue<string>();
                sources["Magnetar"] = ExportingMagnetar(inputs.PackageDirectory, sources["Magnetar"],
                    runtime.ResolveInstalledMagnetarInstallDirectory(), exporterSdk);
                sources["CommonPlugins"] = Path.Combine(exported, "CommonPlugins");
                sources["DirectTransport"] = Path.Combine(exported, "DirectTransport");
                var candidate = await dependencies.InspectAsync(cluster, sources, token);
                await dependencies.StageAsync(cluster, new(candidate.ManifestSha256, cluster.PackageSelection!.Revision, null), sources, token);
                var stagedCluster = cluster.Clone();
                stagedCluster.DependencyManifestSha256 = candidate.ManifestSha256;
                inputs = await dependencies.GetDeploymentInputsAsync(stagedCluster, token);
                // The exported plugins must be frozen with the Magnetar that built them, whichever was chosen.
                if (inputs.Files["Dependencies/payload/Magnetar/" + PluginSdkPath].Sha256 != exporterSdk)
                    throw new InvalidOperationException("Magnetar changed during plugin preparation. Stage again.");
                byte[] inputBytes = JsonSerializer.SerializeToUtf8Bytes(inputs, Json);
                string inputsHash = ClusterDeploymentFiles.Hash(inputBytes);
                string archive = Path.Combine(work, "installation.tar");

                var spec = JsonNode.Parse(request.SpecificationJson)!.AsObject();
                spec["memberLimit"] = ClusterConversionService.MemberLimit(effective);
                spec["administrators"] = JsonSerializer.SerializeToNode(ClusterConversionService.Administrators(effective), Json);
                spec["selectedConfigProfileId"] = effective.ConfigProfileId;
                spec["profileSettingsXml"] = patch;
                spec["modFiles"] = JsonSerializer.SerializeToNode(modFiles, Json);
                spec["modVersions"] = JsonSerializer.SerializeToNode(effective.Mods.ToDictionary(m =>
                    m.WorkshopId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    m => ClusterWorkshopModStager.ObservedVersion(m.WorkshopId, observations)), Json);
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
                    var installation = await TransferInstallationAsync(target, old.HostId, request.InputsSha256,
                        transfer, inputBytes, inputsHash, archive, token, progress);
                    string previousModsHash = Path.GetFileName(old.ModDirectory) ?? "";
                    var previousMods = await hosts.GetModInputsAsync(target, previousModsHash, token);
                    string modHash = await ClusterWorldFiles.PackAsync(mods, modArchive, token, requireCheckpoint: false, previous: previousMods);
                    progress?.Report($"Transferring {new FileInfo(modArchive).Length / 1048576d:N1} MiB of changed mod files to {old.HostId}...");
                    var payload = await hosts.TransferConversionInputAsync(target, transfer, "mods", modArchive, modHash, token,
                        previousMods is null ? null : previousModsHash);
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
                progress?.Report("Preparing and validating the combined deployment on every Host...");
                var result = await deployments.PrepareAsync(clusterId, request, Guid.NewGuid().ToString("N"), actor, token);
                if (result.State == ClusterOperationState.Succeeded)
                {
                    if (profiles.GetProfile(profileId) is not { } latest || ProfileHash(latest) != selectedHash)
                        throw new InvalidOperationException("The selected profile changed during preparation. Stage it again.");
                    var deployment = result.Result!.Value.Deserialize<ClusterDeploymentRequest>(Json)
                        ?? throw new InvalidDataException("Host preparation returned no deployment.");
                    await catalog.RecordSelectedReleasePreparationAsync(cluster, deployment, token, selected.ConfigProfileId, selectedHash,
                        preparedContentUpdates: queued);
                }
                return result;
            }
            finally { if (Directory.Exists(work)) Directory.Delete(work, true); }
        }, token);

    /// <summary>
    /// The Magnetar to freeze with plugins exported by the Magnetar whose PluginSdk hashes to
    /// <paramref name="exporterSdkSha256"/>: the snapshot's own when it is that Magnetar. After managed
    /// Magnetar updated itself the installed one is the exporter, and it replaces the snapshot's when
    /// both it and the cluster release declare a PluginSdk contract and its contract satisfies the release's.
    /// </summary>
    internal static string ExportingMagnetar(string package, string snapshotMagnetar, string installedMagnetar,
        string? exporterSdkSha256)
    {
        static string SdkHash(string magnetar) => ClusterDeploymentFiles.Hash(File.ReadAllBytes(Path.Combine(magnetar, PluginSdkPath)));
        if (exporterSdkSha256 == SdkHash(snapshotMagnetar)) return snapshotMagnetar;
        const string different = "Magnetar's plugin exporter and the selected dependency snapshot use different PluginSdk builds.";
        if (exporterSdkSha256 != SdkHash(installedMagnetar))
            throw new InvalidOperationException(different + " Magnetar changed during plugin preparation. Stage again.");
        string sdk = Path.Combine(installedMagnetar, PluginSdkPath);
        if (ClusterDeploymentFiles.GetPluginSdkPin(package).Contract is null || PluginSdkContract.Read(sdk) is null)
            throw new InvalidOperationException(different + " Stage a matching release first.");
        if (ClusterDeploymentFiles.PluginSdkMismatch(package, sdk) is { } mismatch)
            throw new InvalidOperationException($"{different} The installed Magnetar cannot replace the snapshot's: {mismatch}");
        return installedMagnetar;
    }

    internal static void ValidateQueuedMods(ClusterContentUpdateQueue queued, IReadOnlyList<QuasarModSelection> mods,
        IReadOnlyDictionary<long, WorkshopUpdateObservation> observations)
    {
        foreach (var target in queued.Items)
        {
            if (target.Kind == "plugin") continue;
            if (target.Kind != "mod" || !long.TryParse(target.Id, out long id)
                || !mods.Any(m => m.WorkshopId == id)
                || !observations.TryGetValue(id, out var observed) || observed.Error is not null
                || observed.UpdatedTimestamp?.ToString(System.Globalization.CultureInfo.InvariantCulture) != target.TargetVersion)
                throw new InvalidOperationException($"Queued content {target.Name} is unavailable or changed. Check and queue content updates again before staging.");
        }
    }

    internal static void ValidateQueuedPlugins(ClusterContentUpdateQueue queued, JsonObject exported)
    {
        foreach (var target in queued.Items.Where(t => t.Kind == "plugin"))
        {
            var plugin = (exported["plugins"] as JsonArray)?.OfType<JsonObject>()
                .SingleOrDefault(p => p["id"]?.GetValue<string>() == target.Id);
            if (!ClusterPackageService.IsHash(target.TargetVersion, 40)
                || !string.Equals(plugin?["commit"]?.GetValue<string>(), target.TargetVersion, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Exported plugin {target.Name} does not match its queued commit. Check the selected profile version and queue content updates again.");
        }
    }

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
        currentRoot.Remove("administrators");
        selectedRoot.Remove("administrators");
        if (!JsonNode.DeepEquals(currentRoot, selectedRoot))
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

    public async Task<ClusterOperation> PrepareAsync(string clusterId, string actor, CancellationToken token,
        IProgress<string>? progress = null, bool allowQueuedContent = false)
    {
        using var storage = await ClusterStorageActivity.BeginAsync(token);
        var cluster = catalog.GetCluster(clusterId) ?? throw new KeyNotFoundException(clusterId);
        if (cluster.QueuedContentUpdates is not null)
        {
            if (!allowQueuedContent)
                throw new InvalidOperationException("Configuration editing permission is required to stage queued content with a release update.");
            return await StageProfileSelectionAsync(clusterId, cluster.ConfigProfileId, actor, token, progress);
        }
        var previous = cluster.Preparation ?? throw new InvalidOperationException("No saved preparation exists. Import a deployment request first.");
        if (cluster.ActiveDeployment is null || cluster.PackageSelection is null || cluster.DependencyManifestSha256 is null)
            throw new InvalidOperationException("Select a cluster package and freeze its dependencies first.");
        if (cluster.Update is { Phase: not ClusterUpdatePhase.Complete }
            || cluster.PendingDeploymentHash is not null || cluster.PendingRestoreHash is not null)
            throw new InvalidOperationException("Finish the current cluster update or activation first.");
        if (previous.Hosts.Length != cluster.ActiveDeployment.Hosts.Length
            || previous.Hosts.Any(host => cluster.ActiveDeployment.Hosts.All(active => active.HostId != host.HostId)))
            throw new InvalidOperationException("Saved preparation has a different Host inventory. Import a current deployment request.");

        progress?.Report("Verifying the selected release and dependency pins...");
        var inputs = await dependencies.GetDeploymentInputsAsync(cluster, token);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(inputs, Json);
        string hash = ClusterDeploymentFiles.Hash(bytes);
        string workspace = Path.Combine(MagnetarPaths.GetQuasarDirectory(), "ClusterUpdates", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(workspace);
            ClusterWorldFiles.Private(workspace);
            string archive = Path.Combine(workspace, "installation.tar");
            var transfer = Guid.NewGuid();
            var preparationHosts = new List<ClusterPreparationHost>();
            foreach (var old in previous.Hosts)
            {
                var active = cluster.ActiveDeployment.Hosts.Single(host => host.HostId == old.HostId);
                var target = cluster.Clone();
                target.HostCommandUrl = active.CommandUrl;
                target.HostCommandTokenEnvironmentVariable = active.TokenEnvironmentVariable;
                var remote = await TransferInstallationAsync(target, old.HostId, previous.InputsSha256,
                    transfer, bytes, hash, archive, token, progress);
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
            progress?.Report("Preparing and validating the selected deployment on every Host...");
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

    private async Task<global::Quasar.Host.Contract.V1.HostConversionPaths> TransferInstallationAsync(
        ClusterDefinition target, string hostId, string previousHash, Guid transfer, byte[] inputs, string hash,
        string archive, CancellationToken token, IProgress<string>? progress)
    {
        progress?.Report($"Checking reusable deployment files on {hostId}...");
        var previous = await hosts.GetDeploymentInputsAsync(target, previousHash, token);
        await using (var output = File.Create(archive))
            await ClusterDeploymentFiles.ExportInputsAsync(inputs, hash, output, token, previous?.Files, progress);
        long bytes = new FileInfo(archive).Length;
        progress?.Report($"Transferring {bytes / 1048576d:N1} MiB of new or changed files to {hostId}...");
        return await hosts.TransferConversionInputAsync(target, transfer, "installation", archive, hash, token,
            previous is null ? null : previousHash);
    }
}
