using Magnetar.Protocol.Runtime;
using Quasar.ClusterDeployment;
using Quasar.Models;

namespace Quasar.Services;

public sealed record ClusterPurgeResult(string ClusterId, string Archive, string[] Archived, string[] Removed, string[] HostWorkspacesRemoved);

/// <summary>Deletes what a deleted cluster leaves behind, so its ID can be set up again from scratch. Ends the
/// deleted cluster's retention now (backups, setup workspace, Host data) and moves its records (deleted
/// definitions, operation records and the world template and configuration profiles it owns) to an archive
/// folder. Without it a new cluster with the same ID would replay the old setup and reuse its Host runtime.</summary>
public sealed class ClusterPurgeService(ClusterCatalog catalog, ClusterOperationStore operations, ClusterHostClient hosts,
    WebServiceOptions options, QuasarConfigProfileCatalog profiles, QuasarWorldTemplateCatalog templates,
    DedicatedServerCatalog servers, KnownPlayerCatalog players, ILogger<ClusterPurgeService> logger)
{
    public async Task<ClusterPurgeResult> PurgeAsync(string clusterId, CancellationToken token)
    {
        ClusterSetupService.ValidateId(clusterId);
        if (!operations.IsReady) throw new InvalidOperationException("Operation history is unavailable; the purge cannot be verified.");
        // The collection lease: retention of deleted clusters and every lifecycle operation wait for it.
        using var collection = await ClusterStorageActivity.CollectAsync(token);
        if (catalog.GetCluster(clusterId) is not null) throw new InvalidOperationException("Delete the cluster before purging it.");
        if (operations.HasPendingOperations(clusterId))
            throw new InvalidOperationException("Wait for pending cluster operations to finish before purging the cluster.");
        var deleted = catalog.GetDeletedClusters().Select(d => d.Cluster)
            .Where(c => c.UniqueName.Equals(clusterId, StringComparison.OrdinalIgnoreCase)).ToArray();
        string name = deleted.FirstOrDefault()?.UniqueName ?? clusterId;
        string quasar = MagnetarPaths.GetQuasarDirectory();
        string setup = Path.Combine(quasar, "ClusterSetup", name);
        // A template or profile named after the cluster belongs to it only when something else shows the cluster existed.
        bool known = deleted.Length > 0 || Directory.Exists(setup) || operations.GetRecent([name], 1).Count > 0;

        // Host data first: when a Host refuses, nothing here has changed yet and the purge can run again.
        var others = (await ClusterStorageMaintenance.RetainedDefinitionsAsync(catalog, options.BackupDirectory, token))
            .Where(c => !c.UniqueName.Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
        var kept = others.SelectMany(ClusterStorageMaintenance.Preparations)
            .SelectMany(p => ClusterStorageMaintenance.Workspaces(p)).ToHashSet(StringComparer.Ordinal);
        var workspaces = new List<string>();
        foreach (var cluster in deleted)
            foreach (var (url, variable) in ClusterStorageMaintenance.DeletedEndpoints(cluster))
            {
                var target = cluster.Clone();
                target.HostCommandUrl = url;
                target.HostCommandTokenEnvironmentVariable = variable;
                await hosts.RetainDeploymentInputsAsync(target, new([], [], [], []), token);
                workspaces.AddRange(await hosts.ReleaseDeletedClusterAsync(target, new(ClusterStorageMaintenance.Preparations(cluster)
                    .SelectMany(p => ClusterStorageMaintenance.Workspaces(p, url)).Distinct().Where(w => !kept.Contains(w)).ToArray()), token));
            }

        // What the end of the retention removes anyway.
        var removed = new List<string>();
        foreach (string directory in new[] { ClusterBackupService.Root(options.BackupDirectory, name), setup }.Where(Directory.Exists))
        {
            ClusterStagingSweep.DeleteTree(directory);
            removed.Add(directory);
        }

        string archive = catalog.PurgeArchive(name);
        var archived = new List<string>();
        int records = await operations.ArchiveClusterAsync(name, Path.Combine(archive, "Operations"), token);
        if (records > 0) archived.Add($"{records} operation records");
        // The cluster's own profile, and a template or profile named after the cluster, unless something else still uses it.
        var inUse = catalog.GetClusters().SelectMany(c => new[] { c.ConfigProfileId, c.WorldTemplateId })
            .Concat(servers.GetServers().SelectMany(s => new[] { s.ConfigProfileId, s.WorldTemplateId }))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        // Setup makes cluster-<guid>, a conversion converted-<guid>. The profile a setup copied is the operator's, unless named after the cluster.
        bool Named(string? id) => id is not null && id.Equals(name, StringComparison.OrdinalIgnoreCase);
        static bool Generated(string id) => id.StartsWith("cluster-", StringComparison.Ordinal) || id.StartsWith("converted-", StringComparison.Ordinal);
        var ownProfiles = deleted.Select(c => c.ConfigProfileId).Where(id => Named(id) || Generated(id))
            .Append(known ? name : null);
        foreach (string id in ownProfiles.OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).Where(id => !inUse.Contains(id)))
            if (Move(Path.Combine(quasar, "ConfigProfiles", id), Path.Combine(archive, "ConfigProfiles", id)))
            {
                await profiles.DeleteAsync(id, token);
                archived.Add("config profile " + id);
            }
        var ownTemplates = deleted.Select(c => c.WorldTemplateId).Where(Named).Append(known ? name : null);
        foreach (string id in ownTemplates.OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).Where(id => !inUse.Contains(id)))
            if (Move(MagnetarPaths.GetQuasarWorldTemplateDirectory(id), Path.Combine(archive, "WorldTemplates", id)))
            {
                await templates.DeleteAsync(id, token);
                archived.Add("world template " + id);
            }
        if (await catalog.ReleaseRemovedIdAsync(name, archive, token)) archived.Add("deleted cluster definitions");
        int knownPlayers = await players.CleanServerAsync(name, token, isCluster: true);
        if (knownPlayers > 0) removed.Add($"{knownPlayers} known player records");

        if (deleted.Length == 0 && removed.Count == 0 && archived.Count == 0)
            throw new KeyNotFoundException(clusterId);
        logger.LogInformation("Purged cluster {Cluster}: archived {Archived} in {Archive}; removed {Removed}; Hosts removed {Workspaces} workspaces.",
            name, string.Join(", ", archived), archive, string.Join(", ", removed), workspaces.Count);
        return new(name, archive, archived.ToArray(), removed.ToArray(), workspaces.ToArray());
    }

    private static bool Move(string source, string destination)
    {
        if (!Directory.Exists(source)) return false;
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        Directory.Move(source, destination);
        return true;
    }
}
