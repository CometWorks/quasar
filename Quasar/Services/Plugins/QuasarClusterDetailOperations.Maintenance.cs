using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Quasar.Client.Clusters;
using Quasar.Models;
using Quasar.Plugin.Abstractions.Operations;
using Quasar.Services.Auth;

namespace Quasar.Services.Plugins;

internal static partial class QuasarClusterDetailOperations
{
    internal sealed record MaintenanceInput(string UniqueName, string? ArtifactId = null, Guid SnapshotId = default,
        string[]? Dirty = null, string? Revision = null, Guid? UpdateId = null);
    private static readonly JsonSerializerOptions MaintenanceJson = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    internal static void AddQuasarClusterMaintenance(this IServiceCollection services)
    {
        services.AddSingleton<ClusterMaintenanceJobs>();
        services.AddQuasarRead<MaintenanceInput>(Plugin, "cluster.maintenance.status", Manage, async (caller, input, _) =>
        {
            var cluster = Cluster(caller, input.UniqueName);
            var sp = caller.Services;
            var latest = sp.GetRequiredService<ClusterReleaseMonitor>().GetSnapshot().Release;
            var state = new ClusterMaintenance(cluster.ActiveDeployment?.Revision, ClusterReleaseMonitor.IsOnLatestRelease(cluster, latest), latest?.Version,
                cluster.Preparation is not null, cluster.PackageSelection is not null, cluster.DependencyManifestSha256 is not null, cluster.ConfigProfileId,
                cluster.PreparedDeployment is {} prepared ? JsonSerializer.Serialize(prepared, MaintenanceJson) : null, cluster.HasPreparedUpdate,
                cluster.GoalState == DedicatedServerGoalState.Off, cluster.Gateway is not null, cluster.IsProvenStopped(),
                cluster.PendingDeploymentHash is not null || cluster.PendingRestoreHash is not null, cluster.PreviousDeployment is not null,
                await MayEditConfigs(caller), cluster.QueuedContentUpdates?.Items.Select(i => new MaintenanceContent(i.Name, i.Kind, i.TargetVersion)).ToArray(),
                cluster.Update is {} update ? new(update.Id, update.Phase.ToString(), update.BackupRequired || update.Backup is not null, update.LastError) : null,
                sp.GetRequiredService<ClusterBackupService>().List(cluster.UniqueName).Select(b => new MaintenanceBackup(b.SnapshotId, b.CreatedAt, b.Consistency.ToString(), b.Online)).ToArray(),
                sp.GetRequiredService<ClusterMaintenanceJobs>().Get(cluster.UniqueName));
            return MaintenanceRead(state);
        }, Read with { Description = "Returns deployment state and latest maintenance job progress." });
        services.AddQuasarRead<MaintenanceInput>(Plugin, "cluster.maintenance.preparation", Manage, (caller, input, _) =>
        {
            var cluster = Cluster(caller, input.UniqueName);
            return Task.FromResult(MaintenanceRead(cluster.Preparation ?? throw new QuasarOperationException(404, "No generated preparation is available."), cluster.UniqueName + "-preparation.json"));
        }, Read with { Description = "Returns the prepared activation document for the selected cluster." });
        services.AddQuasarOperation<MaintenanceInput, string[]>(Plugin, "cluster.maintenance.dirty", Manage, (caller, input, token) =>
            caller.Services.GetRequiredService<ClusterUpdateService>().GetRecordedDirtyShutdownAsync(Cluster(caller, input.UniqueName).UniqueName, token), Read with { Description = "Lists dirty-shutdown evidence for the selected cluster." });

        foreach (var action in new[] { "archive", "backup", "online-backup", "prepare-restore", "stage-latest", "stage-content",
            "clear-content", "select-release", "freeze", "stage-selected", "apply", "apply-dirty", "rollback", "abandon", "recover", "recover-stopped" })
        {
            var policies = action == "stage-content" ? [.. Manage, QuasarPolicyNames.CanEditConfigs] : Manage;
            services.AddQuasarOperation<MaintenanceInput, MaintenanceJob>(Plugin, "cluster.maintenance." + action, policies,
                (caller, input, _) => BeginMaintenance(caller, input, action), Change with { Description = $"Starts the {action} maintenance job on the selected cluster and returns its status." });
        }
        foreach (var action in new[] { "restore", "prepare", "activate", "update-json" })
        {
            services.AddQuasarUpload<MaintenanceInput, MaintenanceJob>(Plugin, "cluster.maintenance." + action, Manage, 24L * 1024 * 1024,
                (caller, input, staged, _) =>
                {
                    Cluster(caller, input.UniqueName);
                    return Task.FromResult(new QuasarUpload<MaintenanceJob>(async token =>
                    {
                        using var parsed = await JsonDocument.ParseAsync(staged, cancellationToken: token);
                        await caller.EnsureAuthorizedAsync(token);
                        return await BeginMaintenance(caller, input, action, parsed.RootElement.Clone());
                    }));
                }, Change with { Description = $"Starts the {action} maintenance job on the selected cluster and returns its status." });
        }
    }

    private static async Task<MaintenanceJob> BeginMaintenance(QuasarOperationContext caller, MaintenanceInput input, string action, JsonElement document = default)
    {
        var cluster = Cluster(caller, input.UniqueName);
        var allowed = await MayEditConfigs(caller);
        if (action is "stage-selected" or "stage-latest" && cluster.QueuedContentUpdates is not null && !allowed)
            throw new QuasarOperationException(403, "Configuration editing permission is required to stage queued content.");
        if (action is "apply" or "apply-dirty" && cluster.PreparedDeployment?.Revision != input.Revision)
            throw new QuasarOperationException(409, "The prepared deployment changed. Review it again.");
        if (action == "abandon" && cluster.Update?.Id != input.UpdateId)
            throw new QuasarOperationException(409, "The active update changed. Review it again.");
        var actor = caller.User.Identity?.Name ?? "web-ui";
        var jobs = caller.Services.GetRequiredService<ClusterMaintenanceJobs>();
        return jobs.Start(cluster.UniqueName, action, (services, progress, token) =>
            RunMaintenance(services, cluster, input, action, actor, allowed, document, progress, token));
    }

    private static async Task<bool> MayEditConfigs(QuasarOperationContext caller) =>
        (await caller.Services.GetRequiredService<IAuthorizationService>().AuthorizeAsync(caller.User, null, QuasarPolicyNames.CanEditConfigs)).Succeeded;

    private static QuasarRead MaintenanceRead<T>(T value, string? name = null)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, MaintenanceJson);
        return new(_ => Task.FromResult<Stream>(new MemoryStream(bytes, writable: false)), "application/json", name, bytes.LongLength);
    }

    private static async Task<string?> RunMaintenance(IServiceProvider services, ClusterDefinition cluster, MaintenanceInput input, string action,
        string actor, bool mayEdit, JsonElement document, IProgress<string> progress, CancellationToken token)
    {
        var name = cluster.UniqueName;
        var key = Guid.NewGuid().ToString("N");
        var deployments = services.GetRequiredService<ClusterDeploymentService>();
        var preparation = services.GetRequiredService<ClusterUpdatePreparationService>();
        var backups = services.GetRequiredService<ClusterBackupService>();
        var updates = services.GetRequiredService<ClusterUpdateService>();
        ClusterOperation? result;
        switch (action)
        {
            case "archive": result = await backups.ArchiveExportAsync(name, input.ArtifactId ?? "", key, actor, token); break;
            case "backup":
            case "online-backup":
                var online = action == "online-backup";
                result = await backups.CaptureAsync(name, new(Guid.NewGuid(), AllowCrashConsistent: online, Online: online), key, actor, token);
                break;
            case "restore": result = await backups.RestoreAsync(name, Required<ClusterRestoreRequest>(document), key, actor, token); break;
            case "prepare-restore": result = await backups.PrepareRestoreAsync(name, input.SnapshotId, Guid.NewGuid(), key, actor, token); break;
            case "prepare":
                var request = Required<ClusterPreparationRequest>(document);
                using (var spec = JsonDocument.Parse(request.SpecificationJson))
                    if (request.Hosts is not { Length: > 0 } || !spec.RootElement.TryGetProperty("clusterId", out var id) || id.GetString() != name)
                        throw new InvalidDataException("Preparation must identify this cluster and at least one Host.");
                result = await deployments.PrepareAsync(name, request, key, actor, token);
                break;
            case "stage-latest": result = await preparation.StageLatestReleaseAsync(name, actor, token, progress, mayEdit); break;
            case "stage-selected": result = await preparation.PrepareAsync(name, actor, token, progress, mayEdit); break;
            case "stage-content": result = await preparation.StageProfileSelectionAsync(name, cluster.ConfigProfileId, actor, token, progress); break;
            case "clear-content": await services.GetRequiredService<ClusterCatalog>().ClearQueuedContentUpdatesAsync(name, token); return null;
            case "select-release": await preparation.SelectReleaseAsync(name, null, null, token); return null;
            case "freeze": await preparation.FreezeDependenciesAsync(name, null, token); return null;
            case "apply":
            case "apply-dirty":
                result = await updates.BeginAsync(name, new(Guid.NewGuid(), cluster.PreparedDeployment ?? throw new InvalidDataException("Prepare a deployment first."),
                    AcceptDirtyShutdown: action == "apply-dirty" ? input.Dirty ?? throw new InvalidDataException("Confirm the recorded dirty shutdown.") : null), key, actor, token);
                break;
            case "update-json": result = await updates.BeginAsync(name, new(Guid.NewGuid(), Required<ClusterDeploymentRequest>(document)), key, actor, token); break;
            case "rollback": result = await updates.BeginAsync(name, new(Guid.NewGuid(), Rollback: true), key, actor, token); break;
            case "abandon": result = await updates.AbandonAsync(name, key, actor, token); break;
            case "activate": result = await deployments.ActivateAsync(name, Required<ClusterDeploymentRequest>(document), key, actor, token); break;
            case "recover": result = await deployments.RecoverGatewayAsync(name, key, actor, token); break;
            case "recover-stopped": result = await deployments.RecoverClusterAsync(name, Guid.NewGuid(), key, actor, token); break;
            default: throw new InvalidOperationException("Unknown cluster maintenance action.");
        }
        if (result.State != ClusterOperationState.Succeeded) throw new InvalidOperationException(result.Error?.Message ?? $"{result.State}: {result.OperationId}");
        return action is "prepare-restore" or "prepare" ? result.Result?.GetRawText() : null;
    }

    private static T Required<T>(JsonElement value) where T : class =>
        value.Deserialize<T>(MaintenanceJson) ?? throw new InvalidDataException("A request is required.");
}

internal sealed class ClusterMaintenanceJobs(IServiceScopeFactory scopes, IHostApplicationLifetime lifetime, ILogger<ClusterMaintenanceJobs> logger)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, MaintenanceJob> _last = new(StringComparer.OrdinalIgnoreCase);
    public MaintenanceJob? Get(string cluster) { lock (_gate) return _last.GetValueOrDefault(cluster); }

    public MaintenanceJob Start(string cluster, string action, Func<IServiceProvider, IProgress<string>, CancellationToken, Task<string?>> run)
    {
        lock (_gate)
        {
            if (_last.GetValueOrDefault(cluster) is { Complete: false }) throw new QuasarOperationException(409, "Cluster maintenance is already running.");
            var job = new MaintenanceJob(Guid.NewGuid(), action, "Starting cluster maintenance…", false, false);
            _last[cluster] = job;
            _ = Task.Run(async () =>
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    var progress = new Progress<string>(message =>
                    {
                        lock (_gate) if (_last[cluster] is { Complete: false } current && current.Id == job.Id)
                            _last[cluster] = current with { Message = message };
                    });
                    var result = await run(scope.ServiceProvider, progress, lifetime.ApplicationStopping);
                    lock (_gate) _last[cluster] = job with { Complete = true, Message = "Completed.", Document = result };
                }
                catch (Exception error)
                {
                    logger.LogWarning(error, "Cluster {Cluster} maintenance {Action} failed", cluster, action);
                    lock (_gate) _last[cluster] = job with { Complete = true, Failed = true, Message = error.Message };
                }
            });
            return job;
        }
    }
}
