using System.Text.Json;
using System.Threading.Channels;
using Quasar.ClusterDeployment;
using Quasar.Models;

namespace Quasar.Services;

// Catalog and retained backup manifests own liveness. Operation history does not.
public sealed class ClusterStorageMaintenance(ClusterCatalog catalog, ClusterDependencyService dependencies,
    ClusterHostClient hosts, WebServiceOptions options, ILogger<ClusterStorageMaintenance> logger) : BackgroundService
{
    private readonly Channel<bool> changes = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
        { FullMode = BoundedChannelFullMode.DropWrite });
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
    private string? previousReferences;
    private long previousStorageRevision = -1;
    private DateTimeOffset retryAfter;
    private bool deferred;
    private void Changed() => changes.Writer.TryWrite(true);
    // Every lease ends in Idle, the reconciler's on every pass. A collection queued then takes the Writer
    // when the next lease is released, and the next TryBegin fails: the third cluster of every pass was
    // skipped for hours (#1423). Collect after an idle only when content changed or a collection is due.
    private void StorageIdle()
    {
        if (Volatile.Read(ref deferred) || ClusterStorageActivity.Revision != Volatile.Read(ref previousStorageRevision))
            Changed();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!OperatingSystem.IsLinux()) return;
        catalog.Changed += Changed;
        ClusterStorageActivity.Idle += StorageIdle;
        Changed();
        try
        {
            await foreach (var _ in changes.Reader.ReadAllAsync(stoppingToken))
            {
                if (DateTimeOffset.UtcNow < retryAfter) continue;
                try
                {
                    using var collection = await ClusterStorageActivity.CollectAsync(stoppingToken);
                    var definitions = catalog.GetClusters().ToList();
                    string backups = Path.Combine(options.BackupDirectory, "Clusters");
                    if (Directory.Exists(backups))
                        foreach (string path in Directory.EnumerateFiles(backups, "backup.json", SearchOption.AllDirectories))
                        {
                            // An unreadable backup aborts collection; it never implies no references.
                            var backup = JsonSerializer.Deserialize<ClusterBackup>(await File.ReadAllBytesAsync(path, stoppingToken), Json)
                                ?? throw new InvalidDataException("Unreadable backup references: " + path);
                            definitions.Add(backup.Deployment);
                        }
                    // Recovery/export backups can retain dependency and preparation roots
                    // without pretending to be an active deployment or a managed world backup.
                    if (Directory.Exists(options.BackupDirectory))
                        foreach (string path in Directory.EnumerateFiles(options.BackupDirectory, "deployment-references.json", SearchOption.AllDirectories))
                            definitions.AddRange(JsonSerializer.Deserialize<ClusterDefinition[]>(await File.ReadAllBytesAsync(path, stoppingToken), Json)
                                ?? throw new InvalidDataException("Unreadable recovery references: " + path));
                    var retained = DependencyReferences(definitions);
                    string identity = JsonSerializer.Serialize(definitions.Select(c => new {
                        c.UniqueName, c.DependencyManifestSha256, c.Preparation, c.ActiveDeployment,
                        c.PreviousDeployment, c.Update, c.PreparedDeployment }).ToArray(), Json);
                    if (identity == previousReferences && previousStorageRevision == ClusterStorageActivity.Revision)
                    { Volatile.Write(ref deferred, false); continue; }
                    CleanupWorkspaces(catalog.GetClusters());
                    await dependencies.CollectAsync(retained, stoppingToken);
                    await CollectModVersionsAsync(definitions.SelectMany(Preparations), stoppingToken);
                    foreach (var group in definitions.GroupBy(c => c.UniqueName))
                    {
                        var preparations = group.SelectMany(Preparations).Distinct().ToArray();
                        var endpoints = preparations.SelectMany(p => p.Hosts).DistinctBy(h => (h.HostId, h.CommandUrl));
                        foreach (var endpoint in endpoints)
                        {
                            var target = group.First().Clone();
                            target.HostCommandUrl = endpoint.CommandUrl;
                            target.HostCommandTokenEnvironmentVariable = endpoint.TokenEnvironmentVariable;
                            string[] hashes = preparations.Where(p => p.Hosts.Any(h => h.HostId == endpoint.HostId
                                    && h.CommandUrl == endpoint.CommandUrl)).Select(p => p.InputsSha256).Distinct().ToArray();
                            var inputs = preparations.SelectMany(p => p.Hosts).Where(h => h.HostId == endpoint.HostId
                                && h.CommandUrl == endpoint.CommandUrl).ToArray();
                            static string[] HashNames(IEnumerable<string?> paths) => paths.Select(Path.GetFileName)
                                .Where(h => ClusterPackageService.IsHash(h, 64)).Select(h => h!).Distinct().ToArray();
                            await hosts.RetainDeploymentInputsAsync(target, new(hashes,
                                HashNames(inputs.Select(h => h.ModDirectory)), HashNames(inputs.Select(h => h.WorldDirectory)),
                                inputs.Select(h => h.ConfigurationDirectory).Distinct().ToArray()), stoppingToken);
                        }
                    }
                    previousReferences = identity;
                    Volatile.Write(ref previousStorageRevision, ClusterStorageActivity.Revision);
                    Volatile.Write(ref deferred, false);
                    logger.LogInformation("Cluster content references reconciled; unused snapshot content pruned.");
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception error)
                {
                    retryAfter = DateTimeOffset.UtcNow.AddMinutes(1);
                    Volatile.Write(ref deferred, true);
                    logger.LogWarning(error, "Cluster content collection deferred; retained references could not be reconciled.");
                }
            }
        }
        finally { catalog.Changed -= Changed; ClusterStorageActivity.Idle -= StorageIdle; }
    }

    internal static HashSet<string> DependencyReferences(IEnumerable<ClusterDefinition> definitions)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var cluster in definitions)
        {
            if (cluster.DependencyManifestSha256 is { } selected) result.Add(selected);
            foreach (var deployment in Revisions(cluster))
            {
                if (deployment.Provenance is not { } provenance)
                    throw new InvalidDataException($"Cluster {cluster.UniqueName} has a retained revision without storage provenance.");
                result.Add(provenance.DependencyManifestSha256);
            }
        }
        return result;
    }

    private static IEnumerable<ClusterActiveRevision> Revisions(ClusterDefinition cluster) =>
        new[] { cluster.ActiveDeployment, cluster.PreviousDeployment,
            cluster.Update is { Phase: not ClusterUpdatePhase.Complete } update ? update.Previous : null }.OfType<ClusterActiveRevision>();

    internal static IEnumerable<ClusterPreparationRequest> Preparations(ClusterDefinition cluster) =>
        Revisions(cluster).Select(r => r.Provenance?.Preparation).Append(cluster.Preparation).OfType<ClusterPreparationRequest>();

    private static async Task CollectModVersionsAsync(IEnumerable<ClusterPreparationRequest> preparations, CancellationToken token)
    {
        string directory = ClusterWorkshopModStager.StorageDirectory;
        if (!Directory.Exists(directory)) return;
        var retained = new HashSet<string>(StringComparer.Ordinal);
        foreach (var preparation in preparations)
        {
            using var document = JsonDocument.Parse(preparation.SpecificationJson);
            if (document.RootElement.TryGetProperty("modVersions", out var versions))
                foreach (var version in versions.EnumerateObject()) retained.Add(version.Name + "-" + version.Value.GetInt64());
        }
        using var content = await ClusterContentStore.OpenAsync(directory, token);
        foreach (string path in Directory.EnumerateDirectories(directory))
        {
            string name = Path.GetFileName(path);
            if (name.StartsWith('.')) continue;
            ClusterWorldFiles.Private(path);
            if (!retained.Contains(name)) Directory.Delete(path, true);
        }
    }

    private static void CleanupWorkspaces(IEnumerable<ClusterDefinition> clusters)
    {
        string quasar = Magnetar.Protocol.Runtime.MagnetarPaths.GetQuasarDirectory();
        string updates = Path.Combine(quasar, "ClusterUpdates");
        if (Directory.Exists(updates))
            foreach (string directory in Directory.EnumerateDirectories(updates))
                if (Guid.TryParseExact(Path.GetFileName(directory), "N", out _))
                { ClusterWorldFiles.Private(directory); Directory.Delete(directory, true); }
        foreach (var cluster in clusters.Where(c => c.ActiveDeployment is not null))
        {
            string work = Path.Combine(quasar, "ClusterSetup", cluster.UniqueName);
            if (Directory.Exists(work)) ClusterSetupService.CleanupTransferFiles(work);
        }
    }
}
