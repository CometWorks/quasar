using Magnetar.Protocol.Runtime;
using Quasar.Client.Administration;
using Quasar.Models;
using Quasar.Plugin.Abstractions.Operations;
using Quasar.Services.Auth;
using Quasar.Services.Backup;

namespace Quasar.Services.Plugins;

internal static class QuasarBackupDashboardOperations
{
    internal sealed record FolderRequest(string? Path, bool Hidden = false);

    public static void AddQuasarBackupDashboard(this IServiceCollection services)
    {
        const string plugin = QuasarCoreOperations.PluginId;
        string[] security = [QuasarPolicyNames.CanManageSecurity];
        var write = new QuasarOperationMetadata { RequireDefaultPolicies = true, AgentAccess = QuasarAgentAccess.Denied };
        var read = write with { Effect = QuasarOperationEffect.Read };

        services.AddQuasarOperation<QuasarCoreOperations.EmptyRequest, BackupDashboard>(plugin, "backup.dashboard", security,
            (caller, _, _) =>
            {
                var provider = caller.Services;
                var settings = provider.GetRequiredService<QuasarBackupSettingsService>();
                var runtime = provider.GetRequiredService<DedicatedServerSupervisor>().GetSnapshots()
                    .ToDictionary(server => server.UniqueName, StringComparer.OrdinalIgnoreCase);
                var servers = provider.GetRequiredService<DedicatedServerCatalog>().GetServers()
                    .OrderBy(server => server.DisplayName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(server => server.UniqueName, StringComparer.OrdinalIgnoreCase)
                    .Select(server =>
                    {
                        var state = runtime.GetValueOrDefault(server.UniqueName)?.State;
                        return new BackupServer(server.UniqueName, server.DisplayName,
                            state is DedicatedServerProcessState.Starting or DedicatedServerProcessState.Running
                                or DedicatedServerProcessState.Restarting or DedicatedServerProcessState.Stopping,
                            state == DedicatedServerProcessState.Running);
                    }).ToArray();
                return Task.FromResult(new BackupDashboard(provider.GetRequiredService<WebServiceOptions>().Version,
                    settings.GetSettings(), settings.GetBackupDirectorySettings(), servers,
                    provider.GetRequiredService<QuasarBackupService>().ListBackups(),
                    provider.GetRequiredService<AutomaticBackupService>().GetRecentJobs()));
            }, read with { Description = "Returns backup configuration, stored archives and recent jobs." });
        services.AddQuasarOperation<QuasarBackupSettings, bool>(plugin, "backup.schedule.save", security,
            async (caller, input, token) =>
            {
                var settings = QuasarBackupSettings.Normalize(input);
                foreach (var rule in new[] { settings.World, settings.Server, settings.Configuration })
                {
                    if (!Enum.IsDefined(rule.Frequency) || !Enum.IsDefined(rule.DayOfWeek))
                        throw new QuasarOperationException(400, "Invalid schedule.");
                    rule.LastBackupUtc = null;
                }
                await caller.Services.GetRequiredService<QuasarBackupSettingsService>().SaveAsync(settings, token);
                return true;
            }, write with { Description = "Saves automatic backup preferences." });
        services.AddQuasarOperation<QuasarCoreOperations.EmptyRequest, Guid>(plugin, "backup.schedule.run", security,
            (caller, _, _) => Task.FromResult(caller.Services.GetRequiredService<AutomaticBackupService>().QueueEnabledBackupsNow()), write with { Description = "Queues a manual run of the configured backup." });
        services.AddQuasarOperation<FolderRequest, bool>(plugin, "backup.directory.save", security,
            async (caller, input, token) =>
            {
                await caller.Services.GetRequiredService<QuasarBackupSettingsService>().SaveBackupDirectoryAsync(input.Path, token);
                return true;
            }, write with { Description = "Changes where the worker stores Quasar backups." });
        services.AddQuasarOperation<FolderRequest, BackupFolder>(plugin, "backup.directory.browse", security,
            (caller, input, _) =>
            {
                var directory = caller.Services.GetRequiredService<QuasarBackupService>().BackupDirectory;
                var path = WebServiceOptions.ResolveBackupDirectory(string.IsNullOrWhiteSpace(input.Path) ? directory : input.Path);
                var browser = caller.Services.GetRequiredService<FileBrowserService>();
                if (!Directory.Exists(path)) path = Directory.GetParent(path) is { Exists: true } parent ? parent.FullName : directory;
                var shortcuts = browser.GetShortcuts().Select(s => new FolderItem(s.Label, s.FullPath))
                    .Append(new("Default backups", MagnetarPaths.GetQuasarBackupsDirectory())).Append(new("Quasar data", MagnetarPaths.GetQuasarDirectory()));
                return Task.FromResult(new BackupFolder(path,
                    browser.ListDirectories(path, input.Hidden).Select(s => new FolderItem(s.Name, s.FullPath)).ToArray(),
                    FileBrowserService.GetBreadcrumbs(path).Select(s => new FolderItem(s.Label, s.FullPath)).ToArray(), shortcuts.ToArray()));
            }, read with { Description = "Lists subdirectories in the worker's backup directory picker." });
        services.AddQuasarOperation<QuasarCoreOperations.ServerRequest, Guid>(plugin, "backup.world.stop", security,
            (caller, input, _) =>
            {
                var server = QuasarCoreOperations.RequireServer(caller.Services, input.UniqueName);
                return Task.FromResult(caller.Services.GetRequiredService<AutomaticBackupService>().QueueWorldBackup(server.UniqueName, WorldBackupAction.StopAndBackUp));
            }, write with { Description = "Queues a world backup after stopping the selected server." });
        services.AddQuasarOperation<QuasarCoreOperations.BackupRequest, QuasarRestoreReport>(plugin, "backup.stored.restore", security,
            (caller, input, token) => caller.Services.GetRequiredService<QuasarBackupService>().RestoreFromFileAsync(input.Name ?? "", token),
            write with { Description = "Restores the selected Quasar backup archive.", Deadline = QuasarOperationMetadata.MaximumDeadline });
        services.AddQuasarOperation<QuasarCoreOperations.BackupRequest, bool>(plugin, "backup.stored.delete", security,
            (caller, input, _) => Task.FromResult(caller.Services.GetRequiredService<QuasarBackupService>().DeleteBackup(input.Name ?? "")), write with { Description = "Deletes a selected stored Quasar backup." });
    }
}
