namespace Quasar.Client.Clusters;

public sealed record MaintenanceBackup(Guid SnapshotId, DateTimeOffset CreatedAt, string Consistency, bool Online);
public sealed record MaintenanceContent(string Name, string Kind, string TargetVersion);
public sealed record MaintenanceUpdate(Guid Id, string Phase, bool HasBackup, string? LastError);
public sealed record MaintenanceJob(Guid Id, string Action, string Message, bool Complete, bool Failed, string? Document = null);
public sealed record ClusterMaintenance(string? ActiveRevision, bool Latest, string? LatestVersion, bool HasPreparation,
    bool HasSelection, bool HasFrozenDependencies, string? ConfigProfile, string? PreparedActivation, bool Prepared,
    bool Stopped, bool HasGateway, bool CleanStop, bool PendingActivation, bool RollbackAvailable,
    bool MayEditConfigs, MaintenanceContent[]? Queued, MaintenanceUpdate? Update, MaintenanceBackup[] Backups, MaintenanceJob? Job);
