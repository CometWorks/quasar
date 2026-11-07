using System.Globalization;
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

    public sealed record BackupRestoreRequest(string Name = "");

    public sealed record BackupRestored(
        string Name,
        string Kind,
        string? Server,
        int FilesRestored,
        string Message
    );

    /// <param name="TimeOfDay">Local time of day for daily and weekly rules.</param>
    public sealed record BackupRule(
        bool Enabled,
        string Frequency,
        string TimeOfDay,
        string DayOfWeek,
        int RetentionCount,
        DateTimeOffset? LastBackupUtc
    );

    public sealed record BackupSettings(
        string Directory,
        BackupRule Configuration,
        BackupRule Server,
        BackupRule World,
        int OnlineClusterMiBPerSecond,
        int DeletedClusterRetentionDays
    );

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
        services.AddQuasarOperation<QuasarCoreOperations.EmptyRequest, BackupSettings>(
            plugin,
            "backups.settings.read",
            [QuasarPolicyNames.CanControlServers],
            (caller, _, _) =>
            {
                var settings = caller
                    .Services.GetRequiredService<QuasarBackupSettingsService>()
                    .GetSettings();
                return Task.FromResult(
                    new BackupSettings(
                        caller.Services.GetRequiredService<QuasarBackupService>().BackupDirectory,
                        Rule(settings.Configuration),
                        Rule(settings.Server),
                        Rule(settings.World),
                        settings.OnlineClusterMiBPerSecond,
                        settings.DeletedClusterRetentionDays
                    )
                );
            },
            new()
            {
                Effect = QuasarOperationEffect.Read,
                Description =
                    "Returns the automatic backup rules for Quasar config, server and world backups (enabled, frequency, time, retention, last run), the backup folder and the cluster backup limits.",
            }
        );
        services.AddQuasarOperation<BackupRestoreRequest, BackupRestored>(
            plugin,
            "backups.restore",
            [QuasarPolicyNames.CanManageSecurity],
            RestoreAsync,
            new()
            {
                Deadline = QuasarOperationMetadata.MaximumDeadline,
                Description =
                    "Restores a stored world or server backup by its file name from backups.list. The server must be stopped: a world restore overwrites its save files, a server restore brings back its definition and app data and leaves it stopped. It cannot be undone, so back up the current world first.",
            }
        );
    }

    private static async Task<BackupRestored> RestoreAsync(
        QuasarOperationContext caller,
        BackupRestoreRequest input,
        CancellationToken token
    )
    {
        var name = input.Name?.Trim() ?? string.Empty;
        var backups = caller.Services.GetRequiredService<QuasarBackupService>();
        // Configuration backups hold secrets and merge into every catalog; they stay on the Backup page.
        var backup =
            backups
                .ListBackups()
                .FirstOrDefault(candidate =>
                    candidate.Kind != QuasarBackupKind.Configuration
                    && candidate.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
                )
            ?? throw new QuasarOperationException(404, $"Backup '{name}' not found.");
        caller.Undo!.Server = backup.ServerUniqueName;
        // The deadline ends the wait, never the restore itself.
        var report = await backups.RestoreFromFileAsync(backup.Name).WaitAsync(token);
        if (!report.Success)
            throw new QuasarOperationException(409, report.Message);
        return new BackupRestored(
            backup.Name,
            backup.Kind.ToString(),
            backup.ServerUniqueName,
            report.FilesRestored,
            report.Message
        );
    }

    private static BackupRule Rule(QuasarBackupRuleSettings rule) =>
        new(
            rule.Enabled,
            rule.Frequency.ToString(),
            rule.TimeOfDay.ToString("HH:mm", CultureInfo.InvariantCulture),
            rule.DayOfWeek.ToString(),
            rule.RetentionCount,
            rule.LastBackupUtc
        );

    private static DedicatedServerDefinition Track(QuasarOperationContext caller, string uniqueName)
    {
        var server = QuasarCoreOperations.RequireServer(caller.Services, uniqueName);
        caller.Undo!.Server = server.UniqueName;
        return server;
    }
}
