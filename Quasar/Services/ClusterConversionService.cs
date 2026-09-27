using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Magnetar.Protocol.Runtime;
using Quasar.ClusterDeployment;
using Quasar.Models;
using Quasar.Services.Backup;
using Quasar.Services.PluginSdk;
using Admin = CometWorks.ClusterGateway.AdminContract.V1;
using HostContract = global::Quasar.Host.Contract.V1;

namespace Quasar.Services;

public sealed record ClusterConversionHost(string HostId, string CommandUrl, string TokenEnvironmentVariable,
    string ExecutorTokenEnvironmentVariable, string Address, int RegularNodes = 1);
public sealed record ServerToClusterRequest(Guid Id, string Server, string SourceRevision, string BinaryVersion,
    string GatewayHost, int SteamPort, string JoinTokenEnvironmentVariable, string AdminTokensFileEnvironmentVariable,
    string[] InternalNetworks, ClusterConversionHost[] Hosts, string? DependencySha256 = null, long? PackageRevision = null,
    int? NodePortBase = null);
public sealed record ClusterToServerRequest(Guid Id, string UniqueName, string DisplayName, int Port,
    string ConfigProfileId, string SourceLifecycle);
public sealed record ClusterConversionReview(string SourceRevision, string ProfileName, string[] Plugins,
    DateTimeOffset? PluginConfigurationCapturedAt, int ConfigurablePlugins, string[] Warnings);
public sealed record ClusterConversionStatus(Guid Id, string Cluster, string Direction, string Phase, string? Destination,
    string? Error, DateTimeOffset UpdatedAt);

/// <summary>Copy-based offline conversion. Source lifecycle locks cover snapshot and destination publication.</summary>
public sealed class ClusterConversionService(ClusterCatalog clusters, DedicatedServerCatalog servers,
    DedicatedServerSupervisor supervisor, ServerRestoreCoordinator stoppedServers, QuasarConfigProfileCatalog profiles,
    PluginConfigService pluginConfigs, ClusterDependencyService dependencies, ClusterHostClient hosts,
    ClusterDeploymentService deployments, ClusterBackupService clusterBackups, QuasarBackupService serverBackups,
    ClusterOperationStore operations, WebServiceOptions options)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly ConcurrentDictionary<Guid, ClusterConversionStatus> progress = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> gates = new();
    private string Root => Path.Combine(options.BackupDirectory, "Conversions");
    public event Action? Changed;
    private string Workspace(Guid id) => Path.Combine(Root, id.ToString("N"));

    public ClusterConversionStatus? GetStatus(Guid id)
    {
        if (progress.TryGetValue(id, out var value)) return value;
        string path = Path.Combine(Workspace(id), "progress.json");
        return File.Exists(path) ? JsonSerializer.Deserialize<ClusterConversionStatus>(File.ReadAllBytes(path), Json) : null;
    }

    public (string Cluster, string Direction, JsonElement Request) GetRequest(Guid id)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Workspace(id), "request.json")));
        var root = document.RootElement;
        return (root.GetProperty("cluster").GetString()!, root.GetProperty("direction").GetString()!, root.GetProperty("request").Clone());
    }

    public async Task<string[]> ReviewPluginsAsync(string clusterId, CancellationToken token)
    {
        var cluster = clusters.GetCluster(clusterId) ?? throw new KeyNotFoundException("Cluster was not found.");
        var inputs = await dependencies.GetDeploymentInputsAsync(cluster, token);
        return ReadPluginPins(Path.Combine(inputs.DependencyDirectory, "payload/CommonPlugins"))
            .Select(p => p.Key + " @ " + p.Value).Order(StringComparer.Ordinal).ToArray();
    }

    internal static Dictionary<string, string> ReadPluginPins(string common) => Directory.EnumerateDirectories(common)
        .Select(folder => XDocument.Load(Path.Combine(folder, Path.GetFileName(folder) + ".xml")).Root!)
        .ToDictionary(data => data.Element("Id")!.Value, data => data.Element("Commit")!.Value, StringComparer.OrdinalIgnoreCase);

    public ClusterConversionReview ReviewServer(string name)
    {
        var server = servers.GetServer(name) ?? throw new KeyNotFoundException("Server was not found.");
        var profile = profiles.GetProfile(server.ConfigProfileId) ?? throw new InvalidOperationException("Source server needs a configuration profile.");
        var snapshot = pluginConfigs.GetLastKnownConfigsForServer(name);
        var warnings = new List<string>();
        if (!string.IsNullOrEmpty(profile.RootSettings.ServerPassword) || profile.RootSettings.GroupId != 0
            || profile.RootSettings.Banned.Count != 0 || profile.RootSettings.Reserved.Count != 0)
            warnings.Add("This cluster package cannot transfer password, group whitelist, bans or reserved slots. Conversion is blocked to preserve access restrictions.");
        if (profile.SessionSettings.OnlineMode != 1)
            warnings.Add("This cluster package does not transfer non-public online modes. Conversion is blocked to preserve access restrictions.");
        if (profile.Plugins.Count > 0 && snapshot is null)
            warnings.Add("No recorded plugin settings. Connect the source Agent once before stopping the server, then review again.");
        if (snapshot?.Plugins.Any(p => string.IsNullOrWhiteSpace(p.ConfigType)
            || (p.AdditionalConfigurations ?? []).Any(c => string.IsNullOrWhiteSpace(c.ConfigType))) == true)
            warnings.Add("A plugin configuration has no unambiguous SDK type. Custom providers or conflicting live configuration copies require review before automatic transfer.");
        return new(Hash(new { server, profile, snapshot }), profile.Name,
            profile.Plugins.Select(p => p.DisplayName + " (" + p.PluginId + ", " + p.SelectedVersion + ")").ToArray(),
            snapshot?.CapturedAtUtc, snapshot?.Plugins.Length ?? 0, warnings.ToArray());
    }

    public Task<ClusterOperation> ToClusterAsync(string clusterId, ServerToClusterRequest request, string key, string actor, CancellationToken token) =>
        RunAsync(clusterId, request.Id, "to-cluster", key, actor, request, async ct =>
        {
            if (!stoppedServers.TryBeginRestore(request.Server, out var reservation))
                throw new InvalidOperationException("Source server has another offline operation in progress.");
            using (reservation)
            return await clusters.WithLifecycleAsync(clusterId, async cluster =>
            {
                if (Completed(request.Id) is { } done) return done;
                string activationReceipt = Path.Combine(Workspace(request.Id), "activation.json");
                if (File.Exists(activationReceipt))
                {
                    var resume = JsonSerializer.Deserialize<ClusterDeploymentRequest>(await File.ReadAllBytesAsync(activationReceipt, ct), Json)!;
                    RequireStopped(servers.GetServer(request.Server) ?? throw new InvalidOperationException("Source server was removed."));
                    if (cluster.ActiveDeployment?.Revision != resume.Revision)
                        await deployments.ActivateCoreAsync(cluster, resume, ct);
                    return await CompleteAsync(request.Id, clusterId, "to-cluster", "/clusters/" + Uri.EscapeDataString(clusterId), ct);
                }
                RequireEmpty(cluster);
                var source = servers.GetServer(request.Server) ?? throw new KeyNotFoundException("Source server was not found.");
                RequireStopped(source);
                var review = ReviewServer(source.UniqueName);
                if (review.SourceRevision != request.SourceRevision) throw new InvalidOperationException("Source settings changed; review the conversion again.");
                if (review.Warnings.Length != 0) throw new InvalidOperationException(string.Join(" ", review.Warnings));
                ValidateTopology(cluster, request);
                if (request.DependencySha256 != cluster.DependencyManifestSha256 || request.PackageRevision != cluster.PackageSelection?.Revision)
                    throw new InvalidOperationException("Destination release or dependency snapshot changed; review it again.");
                var profile = profiles.GetProfile(source.ConfigProfileId)!;
                ValidateAdmission(profile);
                var snapshot = pluginConfigs.GetLastKnownConfigsForServer(source.UniqueName);
                string work = Workspace(request.Id);
                await StageAsync(request.Id, clusterId, "to-cluster", "Verifying release and dependencies", ct);
                var installation = await InstallationAsync(cluster, work, ct);
                VerifyPluginSelection(profile, installation.Directory);
                await StageAsync(request.Id, clusterId, "to-cluster", "Backing up the stopped source", ct);
                string backupReceipt = Path.Combine(work, "source-backups.json");
                if (!File.Exists(backupReceipt))
                {
                    var serverArchive = await serverBackups.WriteServerBackupFileAsync(source.UniqueName, DateTimeOffset.UtcNow, cancellationToken: ct);
                    var worldArchive = await serverBackups.WriteWorldBackupFileAsync(source.UniqueName, DateTimeOffset.UtcNow, cancellationToken: ct);
                    await WriteAsync(backupReceipt, new { serverArchive, worldArchive, server = source, profile, snapshot }, ct);
                }
                string seed = Path.Combine(work, "source-world");
                await ClusterWorldFiles.CopyAsync(source.GetWorldSavePath(), seed, ct);
                string effective = Path.Combine(work, "effective-world");
                if (!Directory.Exists(effective)) await ClusterWorldFiles.CopyAsync(seed, effective, ct);
                await WorldSandboxConfigEditor.WriteProfileAsync(Path.Combine(effective, "Sandbox.sbc"), profile, source.InGameWorldName, ct);
                if (File.Exists(Path.Combine(effective, "Sandbox_config.sbc")))
                    await WorldSandboxConfigEditor.WriteProfileAsync(Path.Combine(effective, "Sandbox_config.sbc"), profile, source.InGameWorldName, ct);
                await StageAsync(request.Id, clusterId, "to-cluster", "Converting the world", ct);
                string converted = Path.Combine(work, "split-world");
                await ConvertOnceAsync(installation.Directory, "to-split", effective, converted, [], ct);
                string worldArchivePath = Path.Combine(work, "split-world.tar");
                string worldHash = await ClusterWorldFiles.PackAsync(converted, worldArchivePath, ct);
                string installationArchive = Path.Combine(work, "installation.tar");
                using (var output = new FileStream(installationArchive, FileMode.Create))
                    await ClusterDeploymentFiles.ExportAsync(installation.Directory, output, ct);
                await StageAsync(request.Id, clusterId, "to-cluster", "Distributing verified inputs to Hosts", ct);
                var preparationHosts = new List<ClusterPreparationHost>();
                var paths = new Dictionary<string, HostContract.HostConversionPaths>();
                foreach (var host in request.Hosts)
                {
                    var target = Target(cluster, host);
                    if (host.HostId == request.GatewayHost)
                        await ClusterSteamClientLibrary.ProvisionAsync(hosts, target, ClusterTestFrontend.FromEnvironment(), null, ct);
                    var installed = await hosts.TransferConversionInputAsync(target, request.Id, "installation", installationArchive, installation.InputsSha256, ct);
                    var world = await hosts.TransferConversionInputAsync(target, request.Id, "world", worldArchivePath, worldHash, ct);
                    if (installed.HostId != host.HostId || world.HostId != host.HostId) throw new InvalidDataException("Conversion Host identity mismatch.");
                    paths.Add(host.HostId, world);
                    preparationHosts.Add(new(host.HostId, host.CommandUrl, host.TokenEnvironmentVariable,
                        host.ExecutorTokenEnvironmentVariable, installed.Directory, world.Directory, world.ConfigurationDirectory));
                }
                string specification = await SpecificationAsync(cluster, source, profile, snapshot, request, converted, paths, ct);
                var preparation = new ClusterPreparationRequest(installation.InputsSha256, specification, cluster.GatewayUrl, preparationHosts.ToArray());
                await StageAsync(request.Id, clusterId, "to-cluster", "Preparing and validating the destination", ct);
                // Preparation owns immutable files only; activation below owns publication under this lifecycle lock.
                var prepared = await deployments.PrepareAsync(clusterId, preparation, "convert-prepare-" + request.Id + "-" + key, actor, ct);
                if (prepared.State != ClusterOperationState.Succeeded) throw new InvalidOperationException(prepared.Error?.Message ?? "Host preparation failed.");
                var activation = prepared.Result!.Value.Deserialize<ClusterDeploymentRequest>(Json)!;
                RequireStopped(servers.GetServer(source.UniqueName) ?? throw new InvalidOperationException("Source server was removed."));
                if (ReviewServer(source.UniqueName).SourceRevision != request.SourceRevision) throw new InvalidOperationException("Source settings changed during conversion.");
                profile.ConfigProfileId = "converted-" + request.Id.ToString("N");
                profile.Name = cluster.DisplayName + " (converted)";
                await profiles.UpsertAsync(profile, ct);
                await clusters.RecordConversionProfileAsync(cluster, profile.ConfigProfileId, ct);
                cluster = clusters.GetCluster(clusterId)!;
                await ClusterWorldFiles.VerifyAsync(source.GetWorldSavePath(), await ClusterDeploymentFiles.InspectAsync(seed, ct), ct);
                await WriteAsync(activationReceipt, activation, ct);
                await deployments.ActivateCoreAsync(cluster, activation, ct);
                return await CompleteAsync(request.Id, clusterId, "to-cluster", "/clusters/" + Uri.EscapeDataString(clusterId), ct);
            }, ct);
        }, token);

    public Task<ClusterOperation> ToServerAsync(string clusterId, ClusterToServerRequest request, string key, string actor, CancellationToken token) =>
        RunAsync(clusterId, request.Id, "to-server", key, actor, request, ct => clusters.WithLifecycleAsync(clusterId, async cluster =>
        {
            if (Completed(request.Id) is { } done) return done;
            if (cluster.GetLifecycleId() != request.SourceLifecycle || cluster.GoalState != DedicatedServerGoalState.Off
                || cluster.ShutdownProof?.LifecycleId != cluster.GetLifecycleId() || cluster.ActiveDeployment is null)
                throw new InvalidOperationException("Conversion requires the reviewed, cleanly stopped cluster.");
            if (request.Port is < 1 or > 65535 || !Regex.IsMatch(request.UniqueName, "^[a-zA-Z0-9_-]{1,64}$"))
                throw new InvalidDataException("Choose a valid destination server name and port.");
            var published = servers.GetServer(request.UniqueName);
            string publishedMarker = Path.Combine(MagnetarPaths.GetQuasarDirectory(), "Magnetars", request.UniqueName, ".conversion-id");
            if (published is not null && File.Exists(publishedMarker) && await File.ReadAllTextAsync(publishedMarker, ct) == request.Id.ToString("D"))
                return await CompleteAsync(request.Id, clusterId, "to-server", "/", ct);
            if (servers.GetServers().Any(s => s.ServerPort == request.Port)) throw new InvalidOperationException("Destination port is already assigned.");
            string work = Workspace(request.Id);
            var profile = profiles.GetProfile(request.ConfigProfileId) ?? throw new InvalidOperationException("Choose a destination configuration profile.");
            await StageAsync(request.Id, clusterId, "to-server", "Verifying the converter", ct);
            var installation = await InstallationAsync(cluster, work, ct);
            await StageAsync(request.Id, clusterId, "to-server", "Backing up every stopped Host", ct);
            var backup = await clusterBackups.CaptureCoreAsync(cluster, new(request.Id), ct);
            await StageAsync(request.Id, clusterId, "to-server", "Verifying and assembling the saved world", ct);
            var roots = await clusterBackups.ExtractConversionSnapshotAsync(backup, Path.Combine(work, "snapshot"), ct);
            var gatewayHost = cluster.ActiveDeployment.Hosts.Single(h => h.Deployment.Gateway is not null).HostId;
            string seed = Path.Combine(roots[gatewayHost], "world");
            string[] data = FindSaveStores(roots.Values);
            string world = Path.Combine(work, "vanilla-world");
            await ConvertOnceAsync(installation.Directory, "to-vanilla", seed, world, data, ct);
            var imported = WorldSandboxConfigEditor.ReadConfigProfile(Path.Combine(world, "Sandbox.sbc"), includeOnlineMode: true);
            profile.SessionSettings = imported.Profile.SessionSettings;
            profile.Mods = imported.Profile.Mods;
            profile.Plugins.RemoveAll(p => p.PluginId is "cluster-node" or "cluster-wa" or "direct-transport");
            profile.ConfigProfileId = "converted-" + request.Id.ToString("N");
            profile.Name = request.DisplayName + " (converted)";
            await profiles.UpsertAsync(profile, ct);
            await WriteAsync(Path.Combine(work, "destination-profile.json"), profile, ct);
            await StageAsync(request.Id, clusterId, "to-server", "Creating the stopped standalone server", ct);
            var existing = servers.GetServer(request.UniqueName);
            string marker = Path.Combine(MagnetarPaths.GetQuasarDirectory(), "Magnetars", request.UniqueName, ".conversion-id");
            if (existing is null)
            {
                await servers.CreateExactAsync(new DedicatedServerDefinition { UniqueName = request.UniqueName,
                    DisplayName = request.DisplayName, InGameServerName = request.DisplayName, WorldSaveName = request.UniqueName,
                    ConfigProfileId = profile.ConfigProfileId, ServerPort = request.Port }, async (destination, cancellation) =>
                {
                    string root = DedicatedServerPathResolver.Resolve(destination).ServerRoot;
                    ClusterWorldFiles.Private(root);
                    await ClusterWorldFiles.CopyAsync(world, destination.GetWorldSavePath(), cancellation);
                    // Preserve canonical settings as reviewable data, never pretend per-node/shared stores can be merged.
                    if (cluster.Preparation is { } sourcePreparation)
                    {
                        using var spec = JsonDocument.Parse(sourcePreparation.SpecificationJson);
                        if (spec.RootElement.TryGetProperty("pluginConfigurations", out var configurations))
                            await WriteAsync(Path.Combine(root, "cluster-plugin-configurations.json"), configurations, cancellation);
                    }
                    await File.WriteAllTextAsync(Path.Combine(root, ".conversion-id"), request.Id.ToString("D"), cancellation);
                }, ct);
            }
            else if (!File.Exists(marker) || await File.ReadAllTextAsync(marker, ct) != request.Id.ToString("D"))
                throw new InvalidOperationException("A different server already uses this name.");
            return await CompleteAsync(request.Id, clusterId, "to-server", "/", ct);
        }, ct), token);

    private async Task<ClusterOperation> RunAsync<T>(string cluster, Guid id, string direction, string key, string actor,
        T request, Func<CancellationToken, Task<ClusterConversionStatus>> action, CancellationToken token)
    {
        if (id == Guid.Empty) throw new InvalidDataException("Conversion ID is required.");
        var gate = gates.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(token);
        try { return await operations.ExecuteAsync(cluster, "cluster.convert." + direction, key, actor, request, async ct =>
        {
            string work = Workspace(id); Directory.CreateDirectory(work); ClusterWorldFiles.Private(work);
            string identity = Path.Combine(work, "request.json");
            string content = JsonSerializer.Serialize(new { cluster, direction, request }, Json);
            if (File.Exists(identity) && await File.ReadAllTextAsync(identity, ct) != content)
                throw new ClusterOperationConflictException(409, "conversion_identity_conflict", "Conversion ID is already bound to another request.");
            if (!File.Exists(identity)) await AtomicFileWriter.WriteTextAsync(identity, content, ct);
            try { return new Admin.AdminEnvelope<ClusterConversionStatus>(1, DateTimeOffset.UtcNow, await action(ct)); }
            catch (Exception error)
            {
                var failed = new ClusterConversionStatus(id, cluster, direction, "Failed", null, error.Message, DateTimeOffset.UtcNow);
                await WriteAsync(Path.Combine(work, "progress.json"), failed, CancellationToken.None);
                progress[id] = failed; Changed?.Invoke();
                if (error is InvalidOperationException or IOException or ArgumentException or KeyNotFoundException)
                    throw new ClusterOperationConflictException(409, "conversion_failed", error.Message);
                throw;
            }
        }, token); }
        finally { gate.Release(); }
    }

    private async Task<HostContract.PreparedClusterDeployment> InstallationAsync(ClusterDefinition cluster, string work, CancellationToken token)
    {
        var inputs = await dependencies.GetDeploymentInputsAsync(cluster, token);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(inputs, Json);
        string hash = ClusterDeploymentFiles.Hash(bytes);
        if (cluster.ActiveDeployment is { } active && (cluster.Preparation is not { } preparation
            || preparation.InputsSha256 != hash
            || ClusterDeploymentFiles.Hash(Encoding.UTF8.GetBytes(ClusterDeploymentFiles.Hash(Encoding.UTF8.GetBytes(preparation.SpecificationJson)) + hash)) != active.Revision))
            throw new InvalidOperationException("Select the release and dependencies used by the active deployment before conversion.");
        return await ClusterDeploymentFiles.PrepareAsync(bytes, hash, Path.Combine(work, "installation"), token);
    }

    internal static async Task ConvertOnceAsync(string installation, string command, string source, string destination, string[] roots, CancellationToken token)
    {
        string receipt = destination + ".json";
        if (File.Exists(receipt))
        {
            var pins = JsonSerializer.Deserialize<Dictionary<string, HostContract.DeploymentFile>>(await File.ReadAllBytesAsync(receipt, token), Json)!;
            await ClusterWorldFiles.VerifyAsync(destination, pins, token); return;
        }
        if (Directory.Exists(destination)) Directory.Delete(destination, true); // Uncommitted output only.
        await ClusterWorldConverter.RunAsync(installation, command, source, destination, roots, token);
        await WriteAsync(receipt, await ClusterDeploymentFiles.InspectAsync(destination, token), token);
    }

    private async Task StageAsync(Guid id, string cluster, string direction, string phase, CancellationToken token)
    {
        var state = new ClusterConversionStatus(id, cluster, direction, phase, null, null, DateTimeOffset.UtcNow);
        await WriteAsync(Path.Combine(Workspace(id), "progress.json"), state, token);
        progress[id] = state; Changed?.Invoke();
    }
    private ClusterConversionStatus? Completed(Guid id)
    {
        string path = Path.Combine(Workspace(id), "completed.json");
        return File.Exists(path) ? JsonSerializer.Deserialize<ClusterConversionStatus>(File.ReadAllBytes(path), Json) : null;
    }
    private async Task<ClusterConversionStatus> CompleteAsync(Guid id, string cluster, string direction, string destination, CancellationToken token)
    {
        var state = new ClusterConversionStatus(id, cluster, direction, "Complete — destination remains stopped", destination, null, DateTimeOffset.UtcNow);
        await WriteAsync(Path.Combine(Workspace(id), "completed.json"), state, token);
        await WriteAsync(Path.Combine(Workspace(id), "progress.json"), state, token);
        progress[id] = state; Changed?.Invoke(); return state;
    }
    private void RequireStopped(DedicatedServerDefinition server)
    {
        if (server.GoalState != DedicatedServerGoalState.Off || supervisor.IsServerProcessActive(server.UniqueName))
            throw new InvalidOperationException("Stop the source server and wait for its process to exit before converting.");
    }
    private static void RequireEmpty(ClusterDefinition cluster)
    {
        if (cluster.GoalState != DedicatedServerGoalState.Off || cluster.ActiveDeployment is not null || cluster.Gateway is not null
            || cluster.PendingDeploymentHash is not null || cluster.PendingRestoreHash is not null)
            throw new InvalidOperationException("Choose an empty stopped cluster with a verified release and dependency snapshot.");
    }
    private static ClusterDefinition Target(ClusterDefinition cluster, ClusterConversionHost host)
    { var target = cluster.Clone(); target.HostCommandUrl = host.CommandUrl; target.HostCommandTokenEnvironmentVariable = host.TokenEnvironmentVariable; return target; }
    private static string Hash(object value) => ClusterDeploymentFiles.Hash(JsonSerializer.SerializeToUtf8Bytes(value, Json));
    private static Task WriteAsync<T>(string path, T value, CancellationToken token) => AtomicFileWriter.WriteTextAsync(path, JsonSerializer.Serialize(value, Json), token);

    internal static string[] FindSaveStores(IEnumerable<string> runtimes) => runtimes
        .Where(root => Directory.Exists(Path.Combine(root, "nodes")))
        .SelectMany(root => Directory.EnumerateDirectories(Path.Combine(root, "nodes")))
        .SelectMany(node => new[] { Path.Combine(node, "data/cluster/saves"), Path.Combine(node, "data/saves") })
        .Where(path => Directory.Exists(path) && (Directory.EnumerateDirectories(path, "P-*").Any()
            || Directory.Exists(Path.Combine(path, "GLOBAL"))))
        .Distinct(StringComparer.Ordinal).ToArray();

    internal static void ValidateTopology(ClusterDefinition cluster, ServerToClusterRequest request)
    {
        if (request.Hosts is null || request.Hosts.Any(h => h is null || string.IsNullOrWhiteSpace(h.HostId) || string.IsNullOrWhiteSpace(h.CommandUrl)) || request.Hosts.Length is < 1 or > 64 || request.Hosts.Sum(h => (long)h.RegularNodes) is < 2 or > 254
            || request.Hosts.Select(h => h.HostId).Distinct().Count() != request.Hosts.Length
            || request.Hosts.Select(h => h.CommandUrl.TrimEnd('/')).Distinct(StringComparer.OrdinalIgnoreCase).Count() != request.Hosts.Length
            || !request.Hosts.Any(h => h.HostId == request.GatewayHost) || request.SteamPort is < 1 or > 65535
            || string.IsNullOrWhiteSpace(request.BinaryVersion) || request.InternalNetworks is null || request.InternalNetworks.Length == 0
            || request.NodePortBase is < 1024 or > 65300)
            throw new InvalidDataException("Choose a Gateway Host, at least two regular nodes, game build and internal networks.");
        foreach (var host in request.Hosts)
        {
            if (!Regex.IsMatch(host.HostId, "^[a-zA-Z0-9_-]{1,64}$") || !IPAddress.TryParse(host.Address, out _)
                || host.RegularNodes is < 0 or > 32 || !Uri.TryCreate(host.CommandUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
                throw new InvalidDataException("Host identity, IP address or command URL is invalid.");
            foreach (string variable in new[] { host.TokenEnvironmentVariable, host.ExecutorTokenEnvironmentVariable }) Variable(variable);
        }
        Variable(request.JoinTokenEnvironmentVariable); Variable(request.AdminTokensFileEnvironmentVariable);
        Variable(cluster.GatewayAdminTokenEnvironmentVariable);
        if (!Uri.TryCreate(cluster.GatewayUrl, UriKind.Absolute, out var gateway) || !IPAddress.TryParse(gateway.Host.Trim('[', ']'), out _))
            throw new InvalidDataException("Cluster Gateway URL must use a routable IP address for conversion.");
    }
    private static void Variable(string variable)
    { if (variable is null || !Regex.IsMatch(variable, "^[a-zA-Z_][a-zA-Z0-9_]{0,127}$")) throw new InvalidDataException("Credentials must name environment variables."); }

    internal static void VerifyPluginSelection(QuasarConfigProfile profile, string installation)
    {
        string common = Path.Combine(installation, "Dependencies/payload/CommonPlugins");
        var pins = ReadPluginPins(common);
        var ids = pins.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (profile.Plugins.Any(p => !ids.Contains(p.PluginId)))
            throw new InvalidDataException("Frozen dependency snapshot does not contain every selected source plugin.");
        if (profile.Plugins.Any(p => p.SelectedVersion.Length == 40 && p.SelectedVersion.All(Uri.IsHexDigit)
            && !pins[p.PluginId].Equals(p.SelectedVersion, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Frozen plugin commit differs from an explicitly pinned source plugin.");
        var selected = profile.Plugins.Select(p => p.PluginId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (ids.Any(id => id is not ("linux-compat" or "dotnet-compat" or "quasar-agent") && !selected.Contains(id!)))
            throw new InvalidDataException("Frozen dependency snapshot contains additional common plugins; align it with the reviewed source selection.");
    }

    // The Gateway reads admission.json strictly: administrators are SteamID64 numbers and memberLimit is at least 2.
    internal static ulong[] Administrators(QuasarConfigProfile profile) => (profile.RootSettings.Administrators ?? [])
        .Where(a => !string.IsNullOrWhiteSpace(a)).Select(a =>
            ulong.TryParse(a.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out ulong id) && id >> 52 == 0x11
                ? id : throw new InvalidDataException($"Administrator '{a.Trim()}' is not a SteamID64; a cluster accepts only numeric Steam IDs."))
        .Distinct().ToArray();

    internal static int MemberLimit(QuasarConfigProfile profile) => profile.SessionSettings.MaxPlayers >= 2 ? profile.SessionSettings.MaxPlayers
        : throw new InvalidDataException($"Max players is {profile.SessionSettings.MaxPlayers}; a cluster needs at least 2.");

    internal static void ValidateAdmission(QuasarConfigProfile profile) { Administrators(profile); MemberLimit(profile); }

    internal static async Task<string> SpecificationAsync(ClusterDefinition cluster, DedicatedServerDefinition source,
        QuasarConfigProfile profile, LastKnownPluginConfigSnapshot? snapshot, ServerToClusterRequest request,
        string world, Dictionary<string, HostContract.HostConversionPaths> paths, CancellationToken token,
        ClusterTestFrontend? testFrontend = null)
    {
        var specification = JsonNode.Parse(await ProductionSpecificationAsync(cluster, source, profile, snapshot, request, world, paths, token))!.AsObject();
        AddLocalSharedStorage(specification);
        (testFrontend ?? ClusterTestFrontend.FromEnvironment()).Apply(specification);
        return specification.ToJsonString(Json);
    }

    // One Host already has a durable runtime root shared by all its nodes and included in Host snapshots.
    // Different Hosts need an operator-provided shared filesystem; a matching path alone proves nothing.
    internal static void AddLocalSharedStorage(JsonObject specification)
    {
        if (specification["sharedStorageRoot"] is not null || specification["hosts"] is not JsonArray { Count: 1 } hosts)
            return;
        string root = hosts[0]?["runRoot"]?.GetValue<string>()
            ?? throw new InvalidDataException("The Host runtime root is missing from the deployment specification.");
        if (!root.StartsWith('/') || root.Length <= 1)
            throw new InvalidDataException("The Host runtime root must be an absolute Linux path.");
        specification["sharedStorageRoot"] = root.TrimEnd('/') + "/plugin-shared";
    }

    private static async Task<string> ProductionSpecificationAsync(ClusterDefinition cluster, DedicatedServerDefinition source,
        QuasarConfigProfile profile, LastKnownPluginConfigSnapshot? snapshot, ServerToClusterRequest request,
        string world, Dictionary<string, HostContract.HostConversionPaths> paths, CancellationToken token)
    {
        var nodes = new List<object>(); int slot = 1;
        static string Endpoint(string address, int port) => new IPEndPoint(IPAddress.Parse(address), port).ToString();
        foreach (var host in request.Hosts)
        {
            for (int i = 0; i < host.RegularNodes; i++) nodes.Add(new { slotKey = host.HostId + "-node-" + i,
                nodeId = host.HostId + "-node-" + i, host = host.HostId, role = "regular", catalogSlot = slot++,
                backend = Endpoint(host.Address, (request.NodePortBase ?? 28417) + i), control = Endpoint(host.Address, (request.NodePortBase is { } port ? port + 100 : 29417) + i) });
            if (host.HostId == request.GatewayHost) nodes.Add(new { slotKey = "world-authority", nodeId = "world-authority",
                host = host.HostId, role = "WA", catalogSlot = request.Hosts.Sum(h => h.RegularNodes) + 1,
                backend = Endpoint(host.Address, request.NodePortBase is { } waPort ? waPort + 64 : 28700),
                control = Endpoint(host.Address, request.NodePortBase is { } waControl ? waControl + 164 : 29700) });
        }
        var gateway = new Uri(cluster.GatewayUrl);
        return JsonSerializer.Serialize(new { schemaVersion = 1, clusterId = cluster.UniqueName, worldId = cluster.UniqueName,
            serverName = string.IsNullOrWhiteSpace(source.InGameServerName) ? source.DisplayName : source.InGameServerName,
            binaryVersion = request.BinaryVersion, memberLimit = MemberLimit(profile),
            administrators = Administrators(profile),
            hosts = request.Hosts.Select(h => new { id = h.HostId, runRoot = paths[h.HostId].RuntimeDirectory,
                executorTokenEnvironmentVariable = h.ExecutorTokenEnvironmentVariable }),
            gatewayHost = request.GatewayHost, gatewayControl = Endpoint(gateway.Host.Trim('[', ']'), gateway.Port),
            steamListen = Endpoint("0.0.0.0", request.SteamPort), internalNetworks = request.InternalNetworks,
            joinTokenEnvironmentVariable = request.JoinTokenEnvironmentVariable,
            adminTokenEnvironmentVariable = cluster.GatewayAdminTokenEnvironmentVariable,
            adminTokensFileEnvironmentVariable = request.AdminTokensFileEnvironmentVariable,
            worldFiles = (await ClusterDeploymentFiles.InspectAsync(world, token)).ToDictionary(p => p.Key, p => p.Value.Sha256), nodes,
            pluginConfigurations = (snapshot?.Plugins ?? []).ToDictionary(p => p.PluginId,
                p => (p.AdditionalConfigurations?.Length ?? 0) == 0
                    ? (object)new { configType = p.ConfigType, configuration = JsonNode.Parse(p.ConfigJson) }
                    : new { configurations = new[] { new Magnetar.Protocol.Model.PluginConfigurationData {
                            ConfigType = p.ConfigType, ConfigJson = p.ConfigJson } }
                        .Concat(p.AdditionalConfigurations!).Select(c => new {
                            configType = c.ConfigType, configuration = JsonNode.Parse(c.ConfigJson) }).ToArray() }) }, Json);
    }
}

/// <summary>Test-only client frontends of a managed cluster, taken from the environment of the Quasar process.
/// Production clusters publish the Steam frontend only. A test cluster may add the Gateway's Direct Transport
/// frontend, which admits every client, so headless clients can join it; and it may drop Steam, so the Gateway
/// Host needs no steamclient.so (the cluster bench runs this way). Needs a cluster release that knows
/// <c>directListen</c>.</summary>
public sealed record ClusterTestFrontend(string? DirectListen, bool DisableSteam)
{
    public const string DirectListenVariable = "QUASAR_CLUSTER_TEST_DIRECT_LISTEN", DisableSteamVariable = "QUASAR_CLUSTER_TEST_DISABLE_STEAM";

    public bool UsesSteam => !DisableSteam;

    public static ClusterTestFrontend FromEnvironment() => Create(Environment.GetEnvironmentVariable(DirectListenVariable),
        Environment.GetEnvironmentVariable(DisableSteamVariable));

    internal static ClusterTestFrontend Create(string? directListen, string? disableSteam)
    {
        directListen = string.IsNullOrWhiteSpace(directListen) ? null : directListen.Trim();
        bool noSteam = string.Equals(disableSteam?.Trim(), "true", StringComparison.OrdinalIgnoreCase);
        if (directListen is not null && (!IPEndPoint.TryParse(directListen, out var endpoint) || endpoint.Port is < 1024 or > 65535
            || !ClusterHostCatalog.IsClusterAddress(endpoint.Address.ToString())))
            throw new InvalidDataException($"{DirectListenVariable} must be IP:PORT on loopback or a private network; the Direct Transport frontend admits every client.");
        if (noSteam && directListen is null)
            throw new InvalidDataException($"{DisableSteamVariable} needs {DirectListenVariable}: a cluster without any client frontend cannot be joined.");
        return new(directListen, noSteam);
    }

    internal void Apply(System.Text.Json.Nodes.JsonObject specification)
    {
        if (DirectListen is not null) specification["directListen"] = DirectListen;
        if (DisableSteam) specification.Remove("steamListen");
    }
}
