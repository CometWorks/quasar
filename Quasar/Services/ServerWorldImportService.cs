using Quasar.Models;
using Quasar.Services.Backup;

namespace Quasar.Services;

/// <summary>Commits a complete staged world to an existing stopped standalone server.</summary>
public sealed class ServerWorldImportService(
    DedicatedServerCatalog servers,
    QuasarConfigProfileCatalog profiles,
    DedicatedServerSupervisor supervisor,
    ServerRestoreCoordinator maintenance,
    ILogger<ServerWorldImportService> logger)
{
    public bool CanImport(DedicatedServerDefinition definition) =>
        definition.GoalState == DedicatedServerGoalState.Off
        && !supervisor.IsServerProcessActive(definition.UniqueName)
        && !maintenance.IsFileOperationInProgress(definition.UniqueName);

    internal async Task<StagedWorld> StageFolderAsync(string source, string parent,
        IProgress<WorldImportProgress>? progress = null, CancellationToken token = default)
    {
        source = Path.GetFullPath(source);
        var scopes = new List<IDisposable>();
        try
        {
            foreach (var server in servers.GetServers())
            {
                string world = server.GetWorldSavePath();
                if (string.IsNullOrWhiteSpace(world) || !DedicatedServerPathResolver.IsPathWithinRoot(source, world)) continue;
                if (!maintenance.TryBeginWorldImport(server.UniqueName, out var scope))
                    throw new InvalidOperationException($"Source server '{server.UniqueName}' is busy with a backup, restore or import.");
                scopes.Add(scope);
                // Claim before checking, following the same start-exclusion ordering as restores.
                if (server.GoalState != DedicatedServerGoalState.Off || supervisor.IsServerProcessActive(server.UniqueName))
                    throw new InvalidOperationException($"Stop source server '{server.UniqueName}' and set its goal Off before copying its world.");
            }
            return await WorldSaveFiles.FromFolderAsync(source, parent, progress, token);
        }
        finally { foreach (var scope in scopes) scope.Dispose(); }
    }

    // Authorization belongs to the UI action/API boundary; keep raw mutation internal.
    internal async Task<DedicatedServerDefinition> ImportAsync(DedicatedServerDefinition expected,
        StagedWorld world, string saveName, bool importSettings, CancellationToken token = default)
    {
        WorldSaveFiles.ValidateSaveName(saveName);
        if (!maintenance.TryBeginWorldImport(expected.UniqueName, out var scope))
            throw new InvalidOperationException("A world backup, restore or import is already in progress for this server.");
        using (scope)
        {
            if (expected.GoalState != DedicatedServerGoalState.Off || supervisor.IsServerProcessActive(expected.UniqueName))
                throw new InvalidOperationException("Stop the server and set its goal Off before importing a world.");
            var current = servers.GetServer(expected.UniqueName)
                ?? throw new InvalidOperationException("The target server no longer exists.");
            if (current.UpdatedAtUtc != expected.UpdatedAtUtc || current.GoalState != DedicatedServerGoalState.Off)
                throw new InvalidOperationException("The server changed. Reopen Import World and try again.");
            string saves = DedicatedServerPathResolver.Resolve(expected).SavesPath;
            WorldSaveFiles.EnsureNoLinks(saves);
            // Staging on this filesystem makes publish an atomic rename rather than a partial copy.
            if (!DedicatedServerPathResolver.PathsEqual(Directory.GetParent(Directory.GetParent(world.WorldPath)!.FullName)!.FullName, saves))
                throw new InvalidOperationException("World must be staged in the target server's saves directory.");
            string target = Path.Combine(saves, saveName);
            if (Directory.EnumerateFileSystemEntries(saves).Any(path => Path.GetFileName(path).Equals(saveName, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("A save with that name already exists. Choose a new save folder name.");
            var profile = profiles.GetProfile(expected.ConfigProfileId)
                ?? throw new InvalidOperationException("The server's configuration profile no longer exists.");
            string? createdProfileId = null;
            bool published = false;
            bool committed = false;
            try
            {
                if (importSettings)
                {
                    WorldSandboxConfigEditor.ApplyWorldToProfile(Path.Combine(world.WorldPath, WorldSandboxConfigEditor.SandboxConfigFileName), profile);
                    profile.ConfigProfileId = "world-" + Guid.NewGuid().ToString("N");
                    profile.SourceWorldTemplateId = string.Empty;
                    profile.Name = $"{saveName} Config";
                    profile.Description = $"World settings imported for server '{expected.UniqueName}'.";
                    profile.RootSettings.AutodetectDependencies = true;
                    createdProfileId = profile.ConfigProfileId;
                    await profiles.UpsertAsync(profile, token);
                }
                token.ThrowIfCancellationRequested();
                var updated = await servers.ActivateImportedWorldAsync(expected, saveName, profile.ConfigProfileId,
                    () => { world.Publish(target); published = true; }, token);
                committed = true;
                logger.LogInformation("Imported world {Save} for server {Server}; previous save {PreviousSave} retained",
                    saveName, expected.UniqueName, expected.WorldSaveName);
                return updated;
            }
            finally
            {
                if (!committed)
                {
                    if (published) WorldSaveFiles.Cleanup(target);
                    if (createdProfileId is not null)
                    {
                        try { await profiles.DeleteAsync(createdProfileId, CancellationToken.None); }
                        catch (Exception exception) { logger.LogWarning(exception, "Could not remove unselected imported profile {Profile}", createdProfileId); }
                    }
                }
            }
        }
    }
}
