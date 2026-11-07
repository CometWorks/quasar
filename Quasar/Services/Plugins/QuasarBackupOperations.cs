using Quasar.Models;
using Quasar.Plugin.Abstractions.Operations;
using Quasar.Services.Auth;
using Quasar.Services.Backup;

namespace Quasar.Services.Plugins;

/// <summary>World and server backups. Configuration backups hold secrets and stay on the Backup page.</summary>
public static class QuasarBackupOperations
{
    public sealed record BackupListRequest(string? UniqueName = null);

    /// <param name="Action"><c>latest</c> backs up the latest save on disk; <c>save</c> saves the running world first.</param>
    public sealed record WorldBackupRequest(string UniqueName = "", string Action = "latest");

    public sealed record ServerBackupRequest(string UniqueName = "");

    public sealed record BackupItem(
        string Name,
        string Kind,
        string? Server,
        long SizeBytes,
        DateTimeOffset CreatedAtUtc,
        bool Automatic
    );

    public sealed record BackupQueued(string UniqueName, Guid JobId, string Message);

    public static void AddQuasarBackupOperations(this IServiceCollection services)
    {
        const string plugin = QuasarCoreOperations.PluginId;
        services.AddQuasarOperation<BackupListRequest, IReadOnlyList<BackupItem>>(
            plugin,
            "backups.list",
            [QuasarPolicyNames.CanControlServers],
            (caller, input, _) =>
                Task.FromResult<IReadOnlyList<BackupItem>>(
                    caller
                        .Services.GetRequiredService<QuasarBackupService>()
                        .ListBackups()
                        .Where(backup =>
                            backup.Kind != QuasarBackupKind.Configuration
                            && (
                                string.IsNullOrWhiteSpace(input.UniqueName)
                                || string.Equals(
                                    backup.ServerUniqueName,
                                    input.UniqueName.Trim(),
                                    StringComparison.OrdinalIgnoreCase
                                )
                            )
                        )
                        .Select(backup => new BackupItem(
                            backup.Name,
                            backup.Kind.ToString(),
                            backup.ServerUniqueName,
                            backup.SizeBytes,
                            backup.CreatedAtUtc,
                            backup.Automatic
                        ))
                        .ToList()
                ),
            new()
            {
                Effect = QuasarOperationEffect.Read,
                Description =
                    "Lists the world and server backups, newest first, optionally of one server: file name, kind, server, size, time and whether a schedule made it.",
            }
        );
        services.AddQuasarOperation<WorldBackupRequest, BackupQueued>(
            plugin,
            "backups.world.create",
            [QuasarPolicyNames.CanControlServers],
            (caller, input, _) =>
            {
                var action = input.Action?.Trim().ToLowerInvariant() switch
                {
                    null or "" or "latest" => WorldBackupAction.LatestSave,
                    "save" => WorldBackupAction.SaveAndBackUp,
                    _ => throw new QuasarOperationException(400, "Action must be latest or save."),
                };
                var server = Track(caller, input.UniqueName);
                var supervisor = caller.Services.GetRequiredService<DedicatedServerSupervisor>();
                var state = supervisor
                    .GetSnapshots()
                    .FirstOrDefault(snapshot =>
                        snapshot.UniqueName.Equals(
                            server.UniqueName,
                            StringComparison.OrdinalIgnoreCase
                        )
                    )
                    ?.State;
                if (
                    action == WorldBackupAction.SaveAndBackUp
                    && state != DedicatedServerProcessState.Running
                )
                    throw new QuasarOperationException(
                        409,
                        "Save and back up needs a running server."
                    );
                if (
                    supervisor.IsServerProcessActive(server.UniqueName)
                    && state != DedicatedServerProcessState.Running
                )
                    throw new QuasarOperationException(
                        409,
                        "Wait for the server to finish starting, stopping, or restarting before backing up its world."
                    );
                var job = caller
                    .Services.GetRequiredService<AutomaticBackupService>()
                    .QueueWorldBackup(server.UniqueName, action);
                return Task.FromResult(
                    new BackupQueued(server.UniqueName, job, "World backup queued.")
                );
            },
            new()
            {
                Reversal = QuasarReversal.NothingToReverse,
                Description =
                    "Queues a world backup of a standalone server: of the latest save on disk (latest), or after a fresh save of the running world (save). Returns the job ID; the backup runs in the background.",
            }
        );
        services.AddQuasarOperation<ServerBackupRequest, BackupQueued>(
            plugin,
            "backups.server.create",
            [QuasarPolicyNames.CanControlServers],
            (caller, input, _) =>
            {
                var server = Track(caller, input.UniqueName);
                var job = caller
                    .Services.GetRequiredService<AutomaticBackupService>()
                    .QueueServerBackup(server.UniqueName);
                return Task.FromResult(
                    new BackupQueued(server.UniqueName, job, "Server backup queued.")
                );
            },
            new()
            {
                Reversal = QuasarReversal.NothingToReverse,
                Description =
                    "Queues a backup of a standalone server's runtime state and settings, without the world. Returns the job ID; the backup runs in the background.",
            }
        );
    }

    private static DedicatedServerDefinition Track(QuasarOperationContext caller, string uniqueName)
    {
        var server = QuasarCoreOperations.RequireServer(caller.Services, uniqueName);
        caller.Undo!.Server = server.UniqueName;
        return server;
    }
}
