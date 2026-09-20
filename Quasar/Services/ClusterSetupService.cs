using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Magnetar.Protocol.Runtime;
using Quasar.ClusterDeployment;
using Quasar.Models;
using Admin = CometWorks.ClusterGateway.AdminContract.V1;
using HostContract = Quasar.Host.Contract.V1;

namespace Quasar.Services;

public sealed record ClusterSetupPlacement(string HostId, int RegularNodes);
public sealed record ClusterSetupRequest(string UniqueName, string DisplayName, string WorldTemplateId,
    string ConfigProfileId, string GatewayHost, int PlayerPort, ClusterSetupPlacement[] Machines);
public sealed record ClusterSetupStatus(ClusterSetupRequest Request, string Phase, string? Error, DateTimeOffset UpdatedAt);

/// <summary>Durable, copy-based creation using the same verified preparation and activation as conversion.</summary>
public sealed class ClusterSetupService(ClusterCatalog catalog, ClusterHostCatalog machines, ClusterHostClient hosts,
    ClusterCredentialStore credentials, ClusterPackageService packages, ClusterDependencyService dependencies,
    ManagedDedicatedServerRuntimeResolver runtime, DedicatedServerRuntimePreparer preparer,
    QuasarWorldTemplateCatalog worlds, QuasarConfigProfileCatalog profiles, ClusterDeploymentService deployments,
    ClusterOperationStore operations, DedicatedServerCatalog servers, ILogger<ClusterSetupService>? logger = null)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> gates = new();
    private static string Workspace(string id)
    {
        ValidateId(id);
        return Path.Combine(MagnetarPaths.GetQuasarDirectory(), "ClusterSetup", id);
    }
    public ClusterSetupStatus? GetStatus(string id)
    {
        string path = Path.Combine(Workspace(id), "status.json");
        return File.Exists(path) ? JsonSerializer.Deserialize<ClusterSetupStatus>(File.ReadAllBytes(path), Json) : null;
    }
    public async Task<ClusterOperation> RunAsync(ClusterSetupRequest request, string key, string actor, CancellationToken token)
    {
        ValidateId(request.UniqueName);
        var gate = gates.GetOrAdd(request.UniqueName, _ => new(1, 1));
        await gate.WaitAsync(token);
        try
        {
            return await operations.ExecuteAsync<ClusterSetupRequest, ClusterSetupStatus>(request.UniqueName, "cluster.setup", key, actor, request, async ct =>
            {
                string work = Workspace(request.UniqueName);
                bool bound = false;
                try
                {
                    var selected = Validate(request, machines.GetAll());
                    CheckReservedPorts(request, selected);
                    var gateway = selected.Single(h => h.Id == request.GatewayHost);
                    string origin = new UriBuilder("http", gateway.Address, request.PlayerPort + 16).Uri.AbsoluteUri.TrimEnd('/');
                    Directory.CreateDirectory(work); ClusterWorldFiles.Private(work);
                    string identity = Path.Combine(work, "request.json");
                    if (File.Exists(identity))
                    {
                        if (await File.ReadAllTextAsync(identity, ct) != JsonSerializer.Serialize(request, Json))
                            throw new InvalidOperationException("This setup is already bound to another request. Resume its original settings or choose a new cluster ID.");
                        if (catalog.GetCluster(request.UniqueName)?.ActiveDeployment is not null && GetStatus(request.UniqueName)?.Phase == "Ready to start")
                            return Envelope(GetStatus(request.UniqueName)!);
                    }
                    else
                    {
                        if (catalog.GetCluster(request.UniqueName) is not null) throw new InvalidOperationException("This cluster ID already belongs to an existing registration. Choose a new ID.");
                        var profile = profiles.GetProfile(request.ConfigProfileId) ?? throw new InvalidOperationException("Choose a configuration profile.");
                        if (worlds.GetTemplate(request.WorldTemplateId) is null) throw new InvalidOperationException("Choose a world template.");
                        try { ClusterConversionService.ValidateAdmission(profile); }
                        catch (InvalidDataException error) { throw new InvalidOperationException("Configuration profile cannot be used for a cluster: " + error.Message); }
                        await WriteAsync(Path.Combine(work, "profile.json"), profile, ct);
                        await WriteAsync(identity, request, ct);
                    }
                    bound = true;
                    string adminReference = credentials.Create("cluster:" + request.UniqueName, "admin");
                    await catalog.CreateAsync(new(request.UniqueName, request.DisplayName, origin, adminReference), ct);
                    return await catalog.WithLifecycleAsync(request.UniqueName, async cluster =>
                    {
                        if (cluster.GoalState != DedicatedServerGoalState.Off) throw new InvalidOperationException("Setup requires the cluster to remain stopped.");
                        string activationFile = Path.Combine(work, "activation.json");
                        if (File.Exists(activationFile))
                        {
                            var resume = Read<ClusterDeploymentRequest>(activationFile);
                            if (cluster.ActiveDeployment?.Revision != resume.Revision) await deployments.ActivateCoreAsync(cluster, resume, ct);
                            return Envelope(await StageAsync(request, "Ready to start", null, ct));
                        }
                        if (cluster.ActiveDeployment is not null || cluster.Gateway is not null)
                            throw new InvalidOperationException("This registration already has a deployment. Use its update controls.");
                        await StageAsync(request, "Checking enrolled machines", null, ct);
                        foreach (var machine in selected)
                        {
                            var status = (await hosts.GetStatusAsync(Target(cluster, machine), ct)).Data;
                            if (status.HostId != machine.Id) throw new InvalidDataException("Connected Host identity differs from its registration.");
                            if (!status.GatewayStopFencing) throw new InvalidOperationException("Update the Host to the matching Quasar release before setup.");
                        }
                        if (cluster.PackageSelection is null)
                        {
                            await StageAsync(request, "Downloading the verified cluster release", null, ct);
                            var release = await packages.GetReleaseAsync(null, ct);
                            var installation = await packages.StageAsync(new(release.Version, release.Sha256), ct);
                            await catalog.SelectPackageAsync(cluster.UniqueName, 0, installation, "guided-setup", ct);
                            cluster = catalog.GetCluster(cluster.UniqueName)!;
                        }
                        var savedProfile = Read<QuasarConfigProfile>(Path.Combine(work, "profile.json"));
                        ClusterConversionService.ValidateAdmission(savedProfile);
                        string seed = Path.Combine(work, "source-world");
                        string sourceReceipt = Path.Combine(work, "source-world.json");
                        if (!File.Exists(sourceReceipt))
                        {
                            if (Directory.Exists(seed)) Directory.Delete(seed, true); // Unpublished copy; never the template.
                            await ClusterWorldFiles.CopyAsync(worlds.GetWorldDirectory(request.WorldTemplateId), seed, ct);
                            await WorldSandboxConfigEditor.WriteProfileAsync(Path.Combine(seed, "Sandbox.sbc"), savedProfile, request.DisplayName, ct);
                            if (File.Exists(Path.Combine(seed, "Sandbox_config.sbc")))
                                await WorldSandboxConfigEditor.WriteProfileAsync(Path.Combine(seed, "Sandbox_config.sbc"), savedProfile, request.DisplayName, ct);
                            await WriteAsync(sourceReceipt, await ClusterDeploymentFiles.InspectAsync(seed, ct), ct);
                        }
                        await ClusterWorldFiles.VerifyAsync(seed, Read<Dictionary<string, HostContract.DeploymentFile>>(sourceReceipt), ct);
                        string exported = Path.Combine(work, "plugins");
                        string exportReceipt = Path.Combine(work, "plugin-export.json");
                        if (!File.Exists(exportReceipt))
                        {
                            await StageAsync(request, "Provisioning Dedicated Server and Magnetar", null, ct);
                            var ready = await runtime.EnsureManagedRuntimeReadyAsync(cancellationToken: ct);
                            if (!ready.IsReady) throw new InvalidOperationException(ready.FailureMessage);
                            await StageAsync(request, "Preparing identical plugins and canonical configuration", null, ct);
                            await ExportPluginsAsync(request, savedProfile, work, exported, ct);
                            await WriteAsync(exportReceipt, await ClusterDeploymentFiles.InspectAsync(exported, ct), ct);
                        }
                        await ClusterWorldFiles.VerifyAsync(exported, Read<Dictionary<string, HostContract.DeploymentFile>>(exportReceipt), ct);
                        var pluginData = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(exported, "preparation.json"), ct))!.AsObject();
                        if (pluginData["schemaVersion"]?.GetValue<int>() != 1) throw new InvalidDataException("Unsupported Magnetar preparation format.");
                        if (cluster.DependencyManifestSha256 is null)
                        {
                            await StageAsync(request, "Freezing the common runtime snapshot", null, ct);
                            string ds = runtime.ResolveInstalledDedicatedServer64Path();
                            var sources = new Dictionary<string, string> { ["DedicatedServer/DedicatedServer64"] = ds,
                                ["DedicatedServer/Content"] = Path.Combine(Path.GetDirectoryName(ds)!, "Content"),
                                ["Magnetar"] = runtime.ResolveInstalledMagnetarInstallDirectory(),
                                ["CommonPlugins"] = Path.Combine(exported, "CommonPlugins"), ["DirectTransport"] = Path.Combine(exported, "DirectTransport") };
                            string sdk = Path.Combine(sources["Magnetar"], "Libraries/MagnetarInterim/PluginSdk.dll");
                            if (ClusterDeploymentFiles.Hash(await File.ReadAllBytesAsync(sdk, ct)) != pluginData["pluginSdkSha256"]?.GetValue<string>())
                                throw new InvalidDataException("Magnetar changed after plugin preparation. Preserve this setup and use a new ID to prepare against the updated runtime.");
                            var candidate = await dependencies.InspectAsync(cluster, sources, ct);
                            var selection = new ClusterDependencyRequest(candidate.ManifestSha256, cluster.PackageSelection!.Revision, null);
                            await dependencies.StageAsync(cluster, selection, sources, ct);
                            await catalog.SelectDependenciesAsync(cluster.UniqueName, selection, ct);
                            cluster = catalog.GetCluster(cluster.UniqueName)!;
                        }
                        var inputs = await dependencies.GetDeploymentInputsAsync(cluster, ct);
                        byte[] inputBytes = JsonSerializer.SerializeToUtf8Bytes(inputs, Json);
                        var installed = await ClusterDeploymentFiles.PrepareAsync(inputBytes, ClusterDeploymentFiles.Hash(inputBytes), Path.Combine(work, "installation"), ct);
                        await StageAsync(request, "Converting a copy of the world", null, ct);
                        string world = Path.Combine(work, "split-world");
                        await ClusterConversionService.ConvertOnceAsync(installed.Directory, "to-split", seed, world, [], ct);
                        string worldArchive = Path.Combine(work, "world.tar"), installationArchive = Path.Combine(work, "installation.tar");
                        string worldHash = await ClusterWorldFiles.PackAsync(world, worldArchive, ct);
                        using (var output = File.Create(installationArchive)) await ClusterDeploymentFiles.ExportAsync(installed.Directory, output, ct);
                        string joinReference = credentials.Create("cluster:" + cluster.UniqueName, "join");
                        var executorSecrets = selected.ToDictionary(h => h.Id, h => credentials.Resolve(credentials.Create("cluster:" + cluster.UniqueName, "executor:" + h.Id))!);
                        var secretSet = new HostContract.HostManagedCredentials(cluster.UniqueName, credentials.Resolve(adminReference)!, credentials.Resolve(joinReference)!, executorSecrets);
                        var conversionHosts = selected.Select(h => new ClusterConversionHost(h.Id, h.CommandUrl, h.CredentialReference,
                            HostContract.ManagedCredentialReference.Cluster(cluster.UniqueName, "executor:" + h.Id), h.Address,
                            request.Machines.Single(m => m.HostId == h.Id).RegularNodes)).ToArray();
                        var topology = new ServerToClusterRequest(GuidFrom(cluster.UniqueName), "", "", pluginData["gameVersion"]!.GetValue<string>(),
                            gateway.Id, request.PlayerPort, joinReference, HostContract.ManagedCredentialReference.Cluster(cluster.UniqueName, "token-file"),
                            selected.Select(h => h.Address + (IPAddress.Parse(h.Address).AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? "/32" : "/128")).ToArray(),
                            conversionHosts, cluster.DependencyManifestSha256, cluster.PackageSelection!.Revision, request.PlayerPort + 100);
                        ClusterConversionService.ValidateTopology(cluster, topology);
                        await StageAsync(request, "Distributing verified files and generated credentials", null, ct);
                        var paths = new Dictionary<string, HostContract.HostConversionPaths>();
                        var preparationHosts = new List<ClusterPreparationHost>();
                        foreach (var machine in selected)
                        {
                            var target = Target(cluster, machine);
                            await hosts.InstallCredentialsAsync(target, secretSet, ct);
                            var remote = await hosts.TransferConversionInputAsync(target, topology.Id, "installation", installationArchive, installed.InputsSha256, ct);
                            var remoteWorld = await hosts.TransferConversionInputAsync(target, topology.Id, "world", worldArchive, worldHash, ct);
                            if (remote.HostId != machine.Id || remoteWorld.HostId != machine.Id) throw new InvalidDataException("Transfer returned another Host identity.");
                            paths.Add(machine.Id, remoteWorld);
                            preparationHosts.Add(new(machine.Id, machine.CommandUrl, machine.CredentialReference,
                                HostContract.ManagedCredentialReference.Cluster(cluster.UniqueName, "executor:" + machine.Id), remote.Directory, remoteWorld.Directory, remoteWorld.ConfigurationDirectory));
                        }
                        var source = new DedicatedServerDefinition { DisplayName = request.DisplayName, InGameServerName = request.DisplayName };
                        var specification = JsonNode.Parse(await ClusterConversionService.SpecificationAsync(cluster, source, savedProfile, null, topology, world, paths, ct))!;
                        specification["pluginConfigurations"] = pluginData["pluginConfigurations"]?.DeepClone() ?? throw new InvalidDataException("Magnetar did not export canonical plugin configurations.");
                        await StageAsync(request, "Preparing and activating the stopped cluster", null, ct);
                        var preparation = new ClusterPreparationRequest(installed.InputsSha256, specification.ToJsonString(), origin, preparationHosts.ToArray());
                        var result = await deployments.PrepareAsync(cluster.UniqueName, preparation, "setup-prepare-" + key, actor, ct);
                        if (result.State != ClusterOperationState.Succeeded) throw new InvalidOperationException(result.Error?.Message ?? "Host preparation failed.");
                        var activation = result.Result!.Value.Deserialize<ClusterDeploymentRequest>(Json)!;
                        savedProfile.ConfigProfileId = "cluster-" + GuidFrom(cluster.UniqueName).ToString("N");
                        savedProfile.Name = request.DisplayName + " (cluster)";
                        await profiles.UpsertAsync(savedProfile, ct);
                        await catalog.RecordConversionProfileAsync(cluster, savedProfile.ConfigProfileId, ct, request.WorldTemplateId);
                        cluster = catalog.GetCluster(cluster.UniqueName)!;
                        await WriteAsync(activationFile, activation, ct);
                        await deployments.ActivateCoreAsync(cluster, activation, ct);
                        return Envelope(await StageAsync(request, "Ready to start", null, ct));
                    }, ct);
                }
                // InvalidDataException covers the world converter, Magnetar export and identity checks above;
                // uncaught it left the status on its last phase and ended the operator's Blazor circuit.
                catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException or ArgumentException or KeyNotFoundException or HttpRequestException or OperationCanceledException
                    or ClusterHostException or ClusterPackageException or JsonException or System.Xml.XmlException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
                {
                    if (bound) await StageAsync(request, "Setup interrupted", error.Message, CancellationToken.None);
                    throw new ClusterOperationConflictException(409, "setup_failed", error.Message);
                }
            }, token);
        }
        finally { gate.Release(); }
    }
    private static Admin.AdminEnvelope<ClusterSetupStatus> Envelope(ClusterSetupStatus state) => new(1, DateTimeOffset.UtcNow, state);
    private static Task WriteAsync<T>(string path, T value, CancellationToken token) => AtomicFileWriter.WriteTextAsync(path, JsonSerializer.Serialize(value, Json), token);
    private static T Read<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllBytes(path), Json)!;
    private static Guid GuidFrom(string id) => new(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("setup:" + id)).AsSpan(0, 16));
    private async Task<ClusterSetupStatus> StageAsync(ClusterSetupRequest request, string phase, string? error, CancellationToken token)
    {
        var state = new ClusterSetupStatus(request, phase, error, DateTimeOffset.UtcNow);
        await WriteAsync(Path.Combine(Workspace(request.UniqueName), "status.json"), state, token); return state;
    }
    private static ClusterDefinition Target(ClusterDefinition cluster, EnrolledClusterHost host)
    { var copy = cluster.Clone(); copy.HostCommandUrl = host.CommandUrl; copy.HostCommandTokenEnvironmentVariable = host.CredentialReference; return copy; }
    private static void ValidateId(string id)
    { if (string.IsNullOrWhiteSpace(id) || !Regex.IsMatch(id, "^[a-zA-Z0-9_-]{1,64}$")) throw new ArgumentException("Use a cluster ID with 1–64 letters, numbers, hyphens or underscores."); }
    private void CheckReservedPorts(ClusterSetupRequest request, EnrolledClusterHost[] selected)
    {
        var addresses = selected.Select(h => IPAddress.Parse(h.Address)).ToHashSet();
        var local = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .SelectMany(n => n.GetIPProperties().UnicastAddresses).Select(a => a.Address).ToHashSet();
        if (addresses.Any(a => IPAddress.IsLoopback(a) || local.Contains(a)) && servers.GetServers()
            .Any(s => s.ServerPort >= request.PlayerPort && s.ServerPort <= request.PlayerPort + 264))
            throw new InvalidOperationException("This port range overlaps a local standalone server. Choose a different player port.");
        foreach (var other in catalog.GetClusters().Where(c => !c.UniqueName.Equals(request.UniqueName, StringComparison.OrdinalIgnoreCase)))
        {
            bool gatewayHere = selected.Any(h => h.CommandUrl == other.HostCommandUrl)
                || Uri.TryCreate(other.GatewayUrl, UriKind.Absolute, out var gateway) && IPAddress.TryParse(gateway.Host.Trim('[', ']'), out var address) && addresses.Contains(address);
            if (gatewayHere && other.Gateway?.Ports.Any(port => port >= request.PlayerPort && port <= request.PlayerPort + 264) == true)
                throw new InvalidOperationException($"This port range overlaps Gateway '{other.DisplayName}'. Choose a different player port.");
            if (GetStatus(other.UniqueName)?.Request is { } setup && setup.Machines.Any(m => selected.Any(h => h.Id == m.HostId))
                && request.PlayerPort <= setup.PlayerPort + 264 && setup.PlayerPort <= request.PlayerPort + 264)
                throw new InvalidOperationException($"This port range overlaps cluster '{other.DisplayName}' on a selected machine. Choose a player port at least 265 ports away.");
            if (other.Preparation is not { } preparation) continue;
            var specification = JsonNode.Parse(preparation.SpecificationJson)!;
            var endpoints = specification["nodes"]!.AsArray().SelectMany(n => new[] { n!["backend"]!.GetValue<string>(), n["control"]!.GetValue<string>() })
                .Append(specification["gatewayControl"]!.GetValue<string>());
            if (endpoints.Any(e => IPEndPoint.TryParse(e, out var endpoint) && addresses.Contains(endpoint.Address)
                && endpoint.Port >= request.PlayerPort && endpoint.Port <= request.PlayerPort + 264))
                throw new InvalidOperationException($"This port range overlaps cluster '{other.DisplayName}'. Choose a different player port.");
        }
    }
    internal static EnrolledClusterHost[] Validate(ClusterSetupRequest request, IReadOnlyList<EnrolledClusterHost> enrolled)
    {
        ValidateId(request.UniqueName);
        if (string.IsNullOrWhiteSpace(request.DisplayName) || request.PlayerPort is < 1024 or > 65000 || request.Machines is null || request.Machines.Length is < 1 or > 64
            || request.Machines.Any(m => m is null || m.RegularNodes is < 0 or > 32) || request.Machines.Sum(m => (long)m.RegularNodes) is < 2 or > 254
            || request.Machines.Select(m => m.HostId).Distinct().Count() != request.Machines.Length)
            throw new ArgumentException("Choose a display name, player port 1024–65000, and at least two nodes across enrolled machines (maximum 32 per machine).");
        var selected = request.Machines.Select(m => enrolled.SingleOrDefault(h => h.Id == m.HostId) ?? throw new ArgumentException("Enroll every selected machine first.")).ToArray();
        if (!selected.Any(h => h.Id == request.GatewayHost) || selected.Select(h => IPAddress.Parse(h.Address)).Distinct().Count() != selected.Length
            || selected.Length > 1 && selected.Any(h => IPAddress.IsLoopback(IPAddress.Parse(h.Address))))
            throw new ArgumentException("Choose the Gateway machine and distinct LAN/VPN addresses. Remote placements cannot use loopback addresses.");
        return selected;
    }

    private async Task ExportPluginsAsync(ClusterSetupRequest request, QuasarConfigProfile profile, string work, string destination, CancellationToken token)
    {
        string magnetar = runtime.GetInstalledVersions().MagnetarPath;
        await RequirePreparationCommandAsync(magnetar, token);
        var selectedProfile = JsonSerializer.Deserialize<QuasarConfigProfile>(JsonSerializer.Serialize(profile, Json), Json)!;
        if (!selectedProfile.Plugins.Any(p => p.PluginId == "direct-transport")) selectedProfile.Plugins.Add(new() { PluginId = "direct-transport", DisplayName = "Direct Transport" });
        // The ordinary runtime preparer edits world configuration. Keep those edits out of the pinned seed.
        string preparationWorld = Path.Combine(work, "preparation/world");
        if (Directory.Exists(preparationWorld)) Directory.Delete(preparationWorld, true);
        await ClusterWorldFiles.CopyAsync(Path.Combine(work, "source-world"), preparationWorld, token);
        var source = new DedicatedServerDefinition { UniqueName = "setup-" + request.UniqueName, DisplayName = request.DisplayName,
            InGameServerName = request.DisplayName, ConfigProfileId = request.ConfigProfileId, ServerPort = request.PlayerPort,
            DedicatedServerAppDataPath = Path.Combine(work, "preparation/DedicatedServer"), MagnetarAppDataPath = Path.Combine(work, "preparation/Magnetar"),
            WorldPath = Path.Combine(work, "preparation"), WorldSaveName = "world" };
        var prepared = await preparer.PrepareAsync(source, runtime.ResolveInstalledDedicatedServer64Path(), MagnetarLaunchArgumentStyle.Current, token, selectedProfile);
        PrepareAgentMetadata(prepared.MagnetarAppDataPath);
        var excluded = ExcludeLocalPluginsWithoutProvenance(prepared.MagnetarAppDataPath);
        if (excluded.Count != 0)
        {
            string names = string.Join(", ", excluded);
            logger?.LogWarning("Cluster {Cluster} setup leaves out local plugins without provenance metadata: {Plugins}. Cluster nodes run only plugins with a pinned source.", request.UniqueName, names);
            await StageAsync(request, "Preparing identical plugins and canonical configuration (left out, no provenance metadata: " + names + ")", null, token);
        }
        if (Directory.Exists(destination)) Directory.Delete(destination, true); // Export has no committed receipt yet.
        await RunMagnetarAsync(magnetar, ["-prepareManaged", destination, "-config", prepared.MagnetarAppDataPath,
            "-profile", Path.Combine(prepared.MagnetarAppDataPath, "Profiles/Current.xml"),
            "-ds64", prepared.DedicatedServer64Path, "-consent", "deny", "-noupdate"], prepared.GitHubToken, token);
    }
    internal static void PrepareAgentMetadata(string config)
    {
        string local = Path.Combine(config, "Local"), assembly = Path.Combine(local, "Quasar.Agent.dll");
        if (!File.Exists(assembly)) throw new InvalidDataException("The Quasar release is missing its Agent plugin.");
        // Agent version attributes are deliberately omitted for content-based drift detection.
        // The worker and its bundled Agent are built from the same release source checkout.
        string version = System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(
            typeof(ClusterSetupService).Assembly)?.InformationalVersion ?? "";
        var commit = Regex.Match(version, "(?:\\+|\\.)[a-fA-F0-9]{40}(?:$|[^a-fA-F0-9])");
        if (!commit.Success) throw new InvalidDataException("The Quasar build has no source commit provenance. Install a published Quasar build before guided setup.");
        string sha = Regex.Match(commit.Value, "[a-fA-F0-9]{40}").Value;
        string folder = Path.Combine(local, "Quasar.Agent"); Directory.CreateDirectory(folder);
        foreach (string file in new[] { "Quasar.Agent.dll", "Magnetar.Protocol.dll", "0Harmony.dll" })
            File.Copy(Path.Combine(local, file), Path.Combine(folder, file), true);
        XNamespace xsi = "http://www.w3.org/2001/XMLSchema-instance";
        new XDocument(new XElement("PluginData", new XAttribute(XNamespace.Xmlns + "xsi", xsi), new XAttribute(xsi + "type", "GitHubPlugin"),
            new XElement("Id", "quasar-agent"), new XElement("FriendlyName", "Quasar Agent"), new XElement("RepoId", "CometWorks/quasar"),
            new XElement("Commit", sha), new XElement("Runtimes", "CoreCLR"), new XElement("Platforms", "Linux"))).Save(Path.Combine(folder, "Quasar.Agent.xml"));
        File.Delete(assembly);
        var current = XDocument.Load(Path.Combine(config, "Profiles/Current.xml"));
        foreach (var item in current.Root!.Element("Local")!.Elements().Where(e => e.Value == "Quasar.Agent.dll")) item.Value = "quasar-agent";
        current.Save(Path.Combine(config, "Profiles/Current.xml"));
    }
    // Managed preparation accepts a local binary only with GitHubPlugin provenance metadata next to it
    // (<name>.xml or <name>.dll.xml). Quasar UI-plugin companion DLLs have none, and Magnetar fails the
    // whole preparation on the first one. They are left out of the cluster profile and named to the operator.
    internal static IReadOnlyList<string> ExcludeLocalPluginsWithoutProvenance(string config)
    {
        string local = Path.Combine(config, "Local"), profile = Path.Combine(config, "Profiles/Current.xml");
        var current = XDocument.Load(profile);
        var excluded = new List<string>();
        foreach (var item in current.Root!.Element("Local")?.Elements().ToArray() ?? [])
        {
            string name = item.Value.Trim();
            if (!name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || name != Path.GetFileName(name)) continue;
            string assembly = Path.Combine(local, name);
            if (File.Exists(Path.ChangeExtension(assembly, ".xml")) || File.Exists(assembly + ".xml")) continue;
            item.Remove();
            excluded.Add(Path.GetFileNameWithoutExtension(name));
        }
        if (excluded.Count != 0) current.Save(profile);
        return excluded;
    }
    internal static async Task RequirePreparationCommandAsync(string executable, CancellationToken token)
    {
        string help = await RunMagnetarAsync(executable, ["-help"], null, token);
        if (!help.Contains("-prepareManaged", StringComparison.Ordinal))
            throw new InvalidOperationException("The installed Magnetar release does not support managed plugin preparation. Update Magnetar to a release providing -prepareManaged, then resume this setup. No game server was started.");
    }
    private static async Task<string> RunMagnetarAsync(string executable, string[] arguments, string? githubToken, CancellationToken token)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in arguments) start.ArgumentList.Add(arg);
        foreach (string name in start.Environment.Keys.Where(k => k.StartsWith("CLUSTER_", StringComparison.Ordinal) || k.StartsWith("QSR_MANAGED_", StringComparison.Ordinal)).ToArray()) start.Environment.Remove(name);
        if (!string.IsNullOrEmpty(githubToken)) start.Environment["PULSAR_GITHUB_TOKEN"] = githubToken;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromMinutes(15));
        using var process = Process.Start(start) ?? throw new IOException("Could not start Magnetar preparation.");
        var output = DrainAsync(process.StandardOutput, deadline.Token); var error = DrainAsync(process.StandardError, deadline.Token);
        try { await process.WaitForExitAsync(deadline.Token); }
        catch { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(CancellationToken.None); } throw; }
        string text = await output, failure = await error;
        if (process.ExitCode != 0) throw new InvalidOperationException("Magnetar preparation failed: " + failure + text);
        return text;
    }
    private static async Task<string> DrainAsync(StreamReader reader, CancellationToken token)
    {
        char[] buffer = new char[8192]; string tail = ""; int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), token)) != 0)
        { tail += new string(buffer, 0, count); if (tail.Length > 16384) tail = tail[^16384..]; }
        return tail;
    }
}
