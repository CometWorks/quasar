using System.Text.Json;
using System.Text.Json.Nodes;
using Magnetar.Protocol.Runtime;
using Quasar.ClusterDeployment;
using Quasar.Models;

namespace Quasar.Services;

/// <summary>Builds a candidate with the selected installation and the cluster's retained world seed and topology.</summary>
public sealed class ClusterUpdatePreparationService(ClusterCatalog catalog, ClusterDependencyService dependencies,
    ClusterHostClient hosts, ClusterDeploymentService deployments, QuasarConfigProfileCatalog profiles,
    ClusterSetupService setup, ClusterWorkshopModStager modStager,
    ManagedDedicatedServerRuntimeResolver runtime, ClusterPackageService packages,
    ILogger<ClusterUpdatePreparationService> logger)
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
            var request = BasePreparation(cluster, progress) ?? throw new InvalidOperationException("Complete guided setup before changing profiles.");
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

                var staged = await modStager.StageProfileAsync(selected, work, token, progress, queued);
                var effective = staged.Profile;
                string mods = staged.Directory;
                string modArchive = Path.Combine(work, "mods.tar");

                string sourceWorld = Path.Combine(work, "profile-source");
                // Specifications from before SE-755 have no worldName; their nodes showed the cluster id.
                string worldName = JsonNode.Parse(request.SpecificationJson)?["worldName"]?.GetValue<string>() ?? cluster.DisplayName;
                string patch = await ClusterWorkshopModStager.ProfileSettingsXmlAsync(sourceWorld, effective, worldName, token);
                string exported = Path.Combine(work, "exported");
                progress?.Report("Preparing pinned plugins and their configuration...");
                var excluded = await setup.ExportProfilePluginsAsync(cluster.UniqueName, cluster.DisplayName,
                    selected.ConfigProfileId, sourceWorld, effective, work, exported, token);
                if (excluded.Count != 0)
                    throw new InvalidOperationException("Selected plugins lack pinned provenance: " + string.Join(", ", excluded));
                if (ClusterSetupService.DevBuilds(exported, Path.Combine(work, "preparation/Magnetar")) is { Length: > 0 } devBuilds)
                    progress?.Report("Dev overrides, built from dev folders instead of their hub pins: " + string.Join("; ", devBuilds));
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
                spec["worldName"] = worldName;
                spec["messageOfTheDay"] = effective.RootSettings.MessageOfTheDay ?? "";
                spec["messageOfTheDayUrl"] = effective.RootSettings.MessageOfTheDayUrl ?? "";
                ClusterWorkshopModStager.AddToSpecification(spec, staged, patch);
                var canonical = exportedData["pluginConfigurations"]?.DeepClone() as JsonObject
                    ?? throw new InvalidDataException("Magnetar did not export plugin configuration.");
                PreservePluginValues(spec["pluginConfigurations"] as JsonObject, canonical);
                spec["pluginConfigurations"] = canonical;
                string json = spec.ToJsonString();
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
                        ConfigurationDirectory = ConfigurationDirectory(old.ConfigurationDirectory, json, inputsHash) });
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
            var request = BasePreparation(cluster) ?? throw new InvalidOperationException("Complete guided setup before changing plugin configuration.");
            var inputs = await dependencies.GetDeploymentInputsAsync(cluster, token);
            if (ClusterDeploymentFiles.Hash(JsonSerializer.SerializeToUtf8Bytes(inputs, Json)) != request.InputsSha256)
                throw new InvalidOperationException("Stage the selected release before changing its plugin configuration.");
            var spec = JsonNode.Parse(request.SpecificationJson)!;
            SetPluginValues(spec, pluginId, configType, values);
            return await PrepareSpecificationAsync(cluster, request, spec, actor, token);
        }, token);

    /// <summary>Stages a deployment with <paramref name="hotSpares"/> hot spares. A spare is an extra regular slot of
    /// the approved inventory, so the change takes a new preparation and applies through the stopped-fleet update.</summary>
    public Task<ClusterOperation> StageHotSparesAsync(string clusterId, int hotSpares, string actor, CancellationToken token) =>
        catalog.WithLifecycleAsync(clusterId, async cluster =>
        {
            if (cluster.ActiveDeployment is null || cluster.Update is { Phase: not ClusterUpdatePhase.Complete }
                || cluster.PendingDeploymentHash is not null || cluster.PendingRestoreHash is not null)
                throw new InvalidOperationException("Finish the current setup, update or activation before changing hot spares.");
            var request = BasePreparation(cluster) ?? throw new InvalidOperationException("Complete guided setup before changing hot spares.");
            var inputs = await dependencies.GetDeploymentInputsAsync(cluster, token);
            if (ClusterDeploymentFiles.Hash(JsonSerializer.SerializeToUtf8Bytes(inputs, Json)) != request.InputsSha256)
                throw new InvalidOperationException("Stage the selected release before changing hot spares.");
            if (hotSpares > 0) ClusterDeploymentFiles.RequireHotSpares(inputs.PackageDirectory);
            var spec = JsonNode.Parse(request.SpecificationJson)!.AsObject();
            SetHotSpares(spec, hotSpares);
            return await PrepareSpecificationAsync(cluster, request, spec, actor, token);
        }, token);

    /// <summary>Stages a deployment with new in-game server and world names, the cluster's identity unchanged. Applies
    /// through the stopped-fleet update like any other preparation; a blank name stays as it is. The names stay staged
    /// until the next activation, whatever is selected or staged before it.</summary>
    public Task<ClusterOperation> StageNamesAsync(string clusterId, string? serverName, string? worldName, string actor,
        CancellationToken token) =>
        catalog.WithLifecycleAsync(clusterId, async cluster =>
        {
            if (cluster.ActiveDeployment is null || cluster.Update is { Phase: not ClusterUpdatePhase.Complete }
                || cluster.PendingDeploymentHash is not null || cluster.PendingRestoreHash is not null)
                throw new InvalidOperationException("Finish the current setup, update or activation before renaming.");
            if (string.IsNullOrWhiteSpace(serverName) && string.IsNullOrWhiteSpace(worldName))
                throw new ArgumentException("Give a server name, a world name or both.");
            var request = BasePreparation(cluster) ?? throw new InvalidOperationException("Complete guided setup before renaming.");
            var inputs = await dependencies.GetDeploymentInputsAsync(cluster, token);
            if (ClusterDeploymentFiles.Hash(JsonSerializer.SerializeToUtf8Bytes(inputs, Json)) != request.InputsSha256)
                throw new InvalidOperationException("Stage the selected release before renaming.");
            var spec = JsonNode.Parse(request.SpecificationJson)!.AsObject();
            string? server = string.IsNullOrWhiteSpace(serverName) ? null : serverName.Trim();
            string? world = string.IsNullOrWhiteSpace(worldName) ? null : worldName.Trim();
            SetNames(spec, server, world);
            return await PrepareSpecificationAsync(cluster, request, spec, actor, token,
                new(server ?? cluster.StagedNames?.ServerName, world ?? cluster.StagedNames?.WorldName));
        }, token);

    /// <summary>Sets the names in a specification, including the world's SessionName in its profile patch. A specification
    /// from before profile patches has none, so its world keeps the old name until the profile is staged.</summary>
    internal static void SetNames(JsonObject specification, string? serverName, string? worldName)
    {
        if (serverName is not null) specification["serverName"] = serverName;
        if (worldName is null) return;
        specification["worldName"] = worldName;
        if (specification["profileSettingsXml"]?.GetValue<string>() is { } patch)
            specification["profileSettingsXml"] = ClusterWorkshopModStager.RenameProfileSettingsXml(patch, worldName);
    }

    internal const int MaxWorldFiles = 1000;
    internal const long MaxWorldFileBytes = 16L * 1024 * 1024;

    /// <summary>The world seed's <c>Storage/</c> files (mod world storage) the next re-prepare builds on: path to SHA-256.</summary>
    public IReadOnlyDictionary<string, string> GetWorldFiles(string clusterId)
    {
        var cluster = catalog.GetCluster(clusterId) ?? throw new KeyNotFoundException(clusterId);
        var request = BasePreparation(cluster) ?? throw new InvalidOperationException("The cluster has no saved preparation.");
        return SeedPins(JsonNode.Parse(request.SpecificationJson)!.AsObject())
            .Where(p => p.Key.StartsWith("Storage/", StringComparison.Ordinal))
            .ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
    }

    /// <summary>
    /// Stages world <c>Storage/</c> files, for example a mod's configuration (MES), as a new revision of the cluster's
    /// world seed (#1313). Each Host receives only the changed files on top of its copy of the seed. The managed update
    /// that activates the candidate copies the files whose seed pins changed into the Gateway's world and every node's
    /// preserved world; later updates keep the seed, so they carry the files without copying them again.
    /// </summary>
    public Task<ClusterOperation> StageWorldFilesAsync(string clusterId, IReadOnlyDictionary<string, byte[]> files,
        string actor, CancellationToken token) =>
        catalog.WithLifecycleAsync(clusterId, async cluster =>
        {
            if (cluster.ActiveDeployment is null || cluster.Update is { Phase: not ClusterUpdatePhase.Complete }
                || cluster.PendingDeploymentHash is not null || cluster.PendingRestoreHash is not null)
                throw new InvalidOperationException("Finish the current setup, update or activation before changing world files.");
            ValidateWorldFiles(files);
            var request = BasePreparation(cluster) ?? throw new InvalidOperationException("Complete guided setup before changing world files.");
            var inputs = await dependencies.GetDeploymentInputsAsync(cluster, token);
            if (ClusterDeploymentFiles.Hash(JsonSerializer.SerializeToUtf8Bytes(inputs, Json)) != request.InputsSha256)
                throw new InvalidOperationException("Stage the selected release before changing world files.");
            var spec = JsonNode.Parse(request.SpecificationJson)!.AsObject();
            var seed = SeedPins(spec);
            if (files.All(f => seed.TryGetValue(f.Key, out var pin) && pin == ClusterDeploymentFiles.Hash(f.Value)))
                throw new InvalidOperationException("The world already has these files.");
            string work = Path.Combine(MagnetarPaths.GetQuasarDirectory(), "ClusterUpdates", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);
            ClusterWorldFiles.Private(work);
            try
            {
                var transfer = Guid.NewGuid();
                var preparationHosts = new List<ClusterPreparationHost>();
                string? worldHash = null;
                foreach (var old in request.Hosts)
                {
                    var active = cluster.ActiveDeployment.Hosts.Single(h => h.HostId == old.HostId);
                    var target = cluster.Clone();
                    target.HostCommandUrl = active.CommandUrl;
                    target.HostCommandTokenEnvironmentVariable = active.TokenEnvironmentVariable;
                    string previousHash = Path.GetFileName(old.WorldDirectory);
                    var previous = await hosts.GetWorldInputsAsync(target, previousHash, token)
                        ?? throw new InvalidOperationException($"Host {old.HostId} cannot update world files: update its Host executor first.");
                    if (previous.Count != seed.Count || previous.Any(p => !seed.TryGetValue(p.Key, out var pin) || pin != p.Value.Sha256))
                        throw new InvalidDataException($"Host {old.HostId}'s world seed differs from the saved preparation.");
                    string archive = Path.Combine(work, "world.tar");
                    string hash = await ClusterWorldFiles.PackOverlayAsync(previous, files, archive, token);
                    if (worldHash is not null && hash != worldHash) throw new InvalidDataException("Hosts hold different world seeds.");
                    worldHash = hash;
                    var payload = await hosts.TransferConversionInputAsync(target, transfer, "world", archive, hash, token, previousHash);
                    if (payload.HostId != old.HostId) throw new InvalidDataException("Transfer returned another Host identity.");
                    preparationHosts.Add(old with { WorldDirectory = payload.Directory });
                }
                foreach (var (name, bytes) in files) seed[name] = ClusterDeploymentFiles.Hash(bytes);
                spec["worldFiles"] = JsonSerializer.SerializeToNode(seed, Json);
                return await PrepareSpecificationAsync(cluster, request with { Hosts = preparationHosts.ToArray() }, spec, actor, token);
            }
            finally { if (Directory.Exists(work)) Directory.Delete(work, true); }
        }, token);

    private static SortedDictionary<string, string> SeedPins(JsonObject spec) =>
        new(spec["worldFiles"]?.Deserialize<Dictionary<string, string>>(Json)
            ?? throw new InvalidDataException("The saved preparation has no world seed pins."), StringComparer.Ordinal);

    /// <summary>World files must be mod world storage, <c>Storage/&lt;mod folder&gt;/...</c>: the seed's checkpoint and
    /// sectors are the world itself, and session settings and mods come from the profile.</summary>
    internal static void ValidateWorldFiles(IReadOnlyDictionary<string, byte[]> files)
    {
        if (files.Count is 0 or > MaxWorldFiles)
            throw new ArgumentException($"Choose 1 to {MaxWorldFiles} world files.");
        if (files.Values.Sum(f => (long)f.Length) > MaxWorldFileBytes)
            throw new ArgumentException($"World files exceed {MaxWorldFileBytes / 1048576} MiB.");
        foreach (string name in files.Keys)
        {
            var parts = name.Split('/');
            if (parts.Length < 3 || parts[0] != "Storage" || name.Contains('\\') || name.Contains(':')
                || parts.Any(p => p is "" or "." or ".." || p.Any(char.IsControl)))
                throw new ArgumentException("World files must be under Storage/<mod folder>/: " + name);
        }
    }

    /// <summary>
    /// Adds or removes spare slots until <paramref name="specification"/> has <paramref name="count"/> hot spares, placed
    /// the way setup places them. Quasar names a host's slots <c>host-node-i</c> on consecutive ports, the spares after the
    /// regular nodes; a removed spare is the last slot of its host, and a new one takes the next slot and catalog slot.
    /// </summary>
    internal static void SetHotSpares(JsonObject specification, int count)
    {
        var nodes = specification["nodes"]!.AsArray();
        static bool Spare(JsonNode node) => node["spare"]?.GetValue<bool>() == true;
        static string HostOf(JsonNode node) => node["host"]!.GetValue<string>();
        var regular = specification["hosts"]!.AsArray().Select(h => h!["id"]!.GetValue<string>())
            .Select(host => (host, nodes.Count(n => HostOf(n!) == host && n!["role"]!.GetValue<string>() == "regular" && !Spare(n)))).ToArray();
        int current = nodes.Count(n => Spare(n!));
        if (count == current) throw new InvalidOperationException($"The cluster already has {count} hot spare(s).");
        var wanted = ClusterConversionService.PlaceHotSpares(regular, specification["gatewayHost"]!.GetValue<string>(), count);
        if (count < 0 || wanted.Values.Sum() != count)
            throw new InvalidDataException("Choose 0 or more hot spares that fit next to the regular nodes, 32 nodes per host machine.");
        int catalogSlot = nodes.Max(n => n!["catalogSlot"]!.GetValue<int>());
        foreach (var (host, regularNodes) in regular)
        {
            int spares = nodes.Count(n => HostOf(n!) == host && Spare(n!));
            JsonNode Slot(int i) => nodes.SingleOrDefault(n => n!["slotKey"]!.GetValue<string>() == $"{host}-node-{i}" && HostOf(n) == host)
                ?? throw new InvalidOperationException("This cluster's nodes were not laid out by Quasar's setup, so its hot spares cannot be changed here.");
            for (int i = 0; i < regularNodes + spares; i++)
                if (Spare(Slot(i)) != i >= regularNodes)
                    throw new InvalidOperationException("This cluster's nodes were not laid out by Quasar's setup, so its hot spares cannot be changed here.");
            for (int i = regularNodes + spares - 1; i >= regularNodes + wanted[host]; i--) nodes.Remove(Slot(i));
            var backend = System.Net.IPEndPoint.Parse(Slot(0)["backend"]!.GetValue<string>());
            var control = System.Net.IPEndPoint.Parse(Slot(0)["control"]!.GetValue<string>());
            for (int i = regularNodes + spares; i < regularNodes + wanted[host]; i++)
                nodes.Insert(nodes.IndexOf(Slot(i - 1)) + 1, new JsonObject { ["slotKey"] = $"{host}-node-{i}", ["nodeId"] = $"{host}-node-{i}",
                    ["host"] = host, ["role"] = "regular", ["catalogSlot"] = ++catalogSlot,
                    ["backend"] = new System.Net.IPEndPoint(backend.Address, backend.Port + i).ToString(),
                    ["control"] = new System.Net.IPEndPoint(control.Address, control.Port + i).ToString(), ["spare"] = true });
        }
    }

    private async Task<ClusterOperation> PrepareSpecificationAsync(ClusterDefinition cluster, ClusterPreparationRequest request,
        JsonNode spec, string actor, CancellationToken token, ClusterStagedNames? stagedNames = null)
    {
        string json = spec.ToJsonString();
        request = request with { SpecificationJson = json, Hosts = request.Hosts.Select(h => h with {
            ConfigurationDirectory = ConfigurationDirectory(h.ConfigurationDirectory, json, request.InputsSha256) }).ToArray() };
        var result = await deployments.PrepareAsync(cluster.UniqueName, request, Guid.NewGuid().ToString("N"), actor, token);
        if (result.State == ClusterOperationState.Succeeded)
        {
            var deployment = result.Result!.Value.Deserialize<ClusterDeploymentRequest>(Json)
                ?? throw new InvalidDataException("Host preparation returned no deployment.");
            await catalog.RecordSelectedReleasePreparationAsync(cluster, deployment, token,
                cluster.PreparedProfile?.ProfileId, cluster.PreparedProfile?.ProfileSha256, stagedNames: stagedNames);
        }
        return result;
    }

    /// <summary>
    /// The generated configuration directory next to <paramref name="current"/> for a specification and installation
    /// inputs, keyed like the deployment revision: SHA-256 of the specification's hash plus the inputs hash. Keying on the
    /// specification alone gave a rebuilt dev-folder plugin (new inputs, same specification) the active deployment's
    /// directory, which the Host refuses to overwrite (#1359).
    /// </summary>
    internal static string ConfigurationDirectory(string current, string specificationJson, string inputsSha256) =>
        Path.Combine(Path.GetDirectoryName(current)!, "config-" + ClusterDeploymentFiles.Hash(System.Text.Encoding.UTF8.GetBytes(
            ClusterDeploymentFiles.Hash(System.Text.Encoding.UTF8.GetBytes(specificationJson)) + inputsSha256)));

    internal static void SetPluginValues(System.Text.Json.Nodes.JsonNode specification, string pluginId, string configType, string values)
    {
        var plugin = specification["pluginConfigurations"]?[pluginId]
            ?? throw new InvalidDataException($"The saved preparation has no configuration for plugin {pluginId}.");
        var config = plugin["configurations"] is System.Text.Json.Nodes.JsonArray configs
            ? configs.SingleOrDefault(c => c?["configType"]?.GetValue<string>() == configType)
                ?? throw new InvalidDataException($"Plugin {pluginId} has no configuration of type {configType}.") : plugin;
        config["configuration"]!["values"] = JsonNode.Parse(values) as JsonObject
            ?? throw new InvalidDataException("Plugin settings must be a JSON object.");
    }

    /// <summary>
    /// The preparation a re-prepare builds on. The saved <see cref="ClusterDefinition.Preparation"/> is whatever was
    /// prepared last, so it can be a failed restore's backup specification, a rolled-back update's, or a change that a
    /// later package or dependency selection discarded. It is the base only while it describes the active deployment or
    /// the change still pending from Quasar's own staging; otherwise the active deployment's recorded preparation is,
    /// and the switch is reported with any frontend it keeps. Staged names apply to either.
    /// </summary>
    internal ClusterPreparationRequest? BasePreparation(ClusterDefinition cluster, IProgress<string>? progress = null) =>
        WithStagedNames(UnnamedBasePreparation(cluster, progress), cluster.StagedNames);

    private static ClusterPreparationRequest? WithStagedNames(ClusterPreparationRequest? request, ClusterStagedNames? names)
    {
        if (request is null || names is null) return request;
        var spec = JsonNode.Parse(request.SpecificationJson)!.AsObject();
        if ((names.ServerName is null || spec["serverName"]?.GetValue<string>() == names.ServerName)
            && (names.WorldName is null || spec["worldName"]?.GetValue<string>() == names.WorldName)) return request;
        SetNames(spec, names.ServerName, names.WorldName);
        return request with { SpecificationJson = spec.ToJsonString() };
    }

    private ClusterPreparationRequest? UnnamedBasePreparation(ClusterDefinition cluster, IProgress<string>? progress)
    {
        var saved = cluster.Preparation;
        if (saved is null || cluster.ActiveDeployment?.Provenance?.Preparation is not { } active || saved == active) return saved;
        string revision = ClusterDependencyService.DeploymentRevision(saved);
        if (revision == cluster.ActiveDeployment.Revision
            || cluster.PreparedForSelectedRelease && revision == cluster.PreparedDeployment?.Revision) return saved;
        string message = $"The saved preparation (revision {revision[..Math.Min(8, revision.Length)]}) is not the active deployment's, "
            + "for example after a failed restore or a rollback. Building on the active deployment's preparation instead.";
        string frontends = string.Join(" ", FrontendDifferences(saved, active)
            .Select(d => $"{d.Name} stays {d.Active ?? "unset"} (the saved preparation had {d.Saved ?? "unset"})."));
        if (frontends.Length != 0) message += " " + frontends;
        logger.LogWarning("Cluster {Cluster}: {Message}", cluster.UniqueName, message);
        progress?.Report(message);
        return active;
    }

    internal static IEnumerable<(string Name, string? Saved, string? Active)> FrontendDifferences(
        ClusterPreparationRequest saved, ClusterPreparationRequest active)
    {
        var before = JsonNode.Parse(saved.SpecificationJson)!.AsObject();
        var after = JsonNode.Parse(active.SpecificationJson)!.AsObject();
        foreach (var (name, key) in new[] { ("Steam", "steamListen"), ("Direct Transport", "directListen") })
        {
            string? old = before[key]?.ToString(), current = after[key]?.ToString();
            if (old != current) yield return (name, old, current);
        }
    }

    /// <summary>
    /// <b>Stage latest release</b> for the UI and the API: selects the latest cluster release, freezes its dependencies
    /// and prepares every Host from the active deployment's world seed and topology. Freezing reuses the selected
    /// snapshot's DS, Magnetar, Direct Transport and common plugins when their PluginSdk satisfies the new release, so
    /// a cluster set up by guided setup needs no configured dependency sources.
    /// </summary>
    public async Task<ClusterOperation> StageLatestReleaseAsync(string clusterId, string actor, CancellationToken token,
        IProgress<string>? progress = null, bool allowQueuedContent = false)
    {
        using var storage = await ClusterStorageActivity.BeginAsync(token);
        var prior = catalog.GetCluster(clusterId) ?? throw new KeyNotFoundException(clusterId);
        if (prior.QueuedContentUpdates is not null && !allowQueuedContent)
            throw new InvalidOperationException("Configuration editing permission is required to stage queued content with a release update.");
        progress?.Report("Checking and staging the latest cluster release...");
        var release = await packages.GetReleaseAsync(null, token);
        bool changingPackage = prior.PackageSelection?.Version != release.Version || prior.PackageSelection.Sha256 != release.Sha256;
        var reusable = changingPackage && prior.DependencyManifestSha256 is not null
            ? await dependencies.ReusableSourcesAsync(prior, token) : null;
        if (!await SelectReleaseAsync(clusterId, release, reusable, token)) reusable = null;
        if (catalog.GetCluster(clusterId)!.DependencyManifestSha256 is null)
        {
            progress?.Report("Freezing the selected release's runtime dependencies...");
            await FreezeDependenciesAsync(clusterId, reusable, token);
        }
        progress?.Report("Transferring and preparing the selected release on all Hosts...");
        return await PrepareAsync(clusterId, actor, token, progress, allowQueuedContent);
    }

    /// <summary>Stages and selects <paramref name="release"/> (the latest one when null) unless it is selected already.
    /// Returns whether the <paramref name="reusable"/> snapshot sources fit its PluginSdk.</summary>
    public async Task<bool> SelectReleaseAsync(string clusterId, ClusterPackageRelease? release,
        Dictionary<string, string>? reusable, CancellationToken token)
    {
        release ??= await packages.GetReleaseAsync(null, token);
        var cluster = catalog.GetCluster(clusterId) ?? throw new KeyNotFoundException(clusterId);
        if (cluster.PackageSelection is { } selected && selected.Version == release.Version && selected.Sha256 == release.Sha256)
            return reusable is not null;
        var package = await packages.StageAsync(new(release.Version, release.Sha256), token);
        bool reuse = reusable is not null
            && ClusterDeploymentFiles.PluginSdkMismatch(package.PackagePath, Path.Combine(reusable["Magnetar"], PluginSdkPath)) is null;
        if (!reuse)
        {
            string installedSdk = Path.Combine(runtime.ResolveInstalledMagnetarInstallDirectory(), PluginSdkPath);
            if (ClusterDeploymentFiles.PluginSdkMismatch(package.PackagePath, installedSdk) is { } mismatch)
                throw new InvalidOperationException($"Cluster release {release.Version} cannot run on the installed Magnetar: {mismatch} A compatible Magnetar installation or existing dependency snapshot is required.");
        }
        await catalog.SelectPackageAsync(clusterId, cluster.PackageSelection?.Revision ?? 0, package,
            "release-" + Guid.NewGuid().ToString("N"), token);
        return reuse;
    }

    /// <summary>Freezes the selected release's dependencies from <paramref name="sources"/>, or from the configured
    /// dependency sources when null.</summary>
    public async Task FreezeDependenciesAsync(string clusterId, Dictionary<string, string>? sources, CancellationToken token)
    {
        using var storage = await ClusterStorageActivity.BeginAsync(token);
        var cluster = catalog.GetCluster(clusterId) ?? throw new KeyNotFoundException(clusterId);
        var candidate = await dependencies.InspectAsync(cluster, sources, token);
        var request = new ClusterDependencyRequest(candidate.ManifestSha256, cluster.PackageSelection!.Revision, cluster.DependencyManifestSha256);
        await dependencies.StageAsync(cluster, request, sources, token);
        await catalog.SelectDependenciesAsync(clusterId, request, token);
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
        var previous = BasePreparation(cluster, progress) ?? throw new InvalidOperationException("No saved preparation exists. Import a deployment request first.");
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
