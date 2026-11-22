using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Quasar.Client.Fleet;
using Quasar.Models;
using Quasar.Plugin.Abstractions.Operations;
using Quasar.Services.Auth;
using static Quasar.Services.Plugins.QuasarCoreOperations;

namespace Quasar.Services.Plugins;

public static class QuasarWorldDashboardOperations
{
    private static readonly QuasarOperationMetadata Read = new()
    { Effect = QuasarOperationEffect.Read, RequireDefaultPolicies = true, AgentAccess = QuasarAgentAccess.Denied };
    private static readonly QuasarOperationMetadata Change = Read with { Effect = QuasarOperationEffect.Change };
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private sealed record DeleteInput(string Id, DateTimeOffset Revision);
    private sealed class CopyProgress(Action<WorldImportProgress> report) : IProgress<WorldImportProgress>
    { public void Report(WorldImportProgress value) => report(value); }
    private sealed class TemplateProgress(DashboardWorldStaging.Entry entry) : IProgress<WorldTemplateImportProgress>
    {
        public void Report(WorldTemplateImportProgress p) => entry.Progress = new("Copying template files", 0, p.CompletedFiles, p.TotalFiles);
    }

    public static void AddQuasarWorldDashboardOperations(this IServiceCollection services)
    {
        services.AddSingleton<DashboardWorldStaging>();
        services.AddQuasarOperation<EmptyRequest, WorldInventory>(PluginId, "worlds.inventory", [QuasarPolicyNames.CanView], async (caller, _, token) =>
        {
            var templates = caller.Services.GetRequiredService<QuasarWorldTemplateCatalog>();
            var profiles = caller.Services.GetRequiredService<QuasarConfigProfileCatalog>();
            var supervisor = caller.Services.GetRequiredService<DedicatedServerSupervisor>();
            var process = supervisor.GetSnapshots().ToDictionary(p => p.UniqueName, StringComparer.OrdinalIgnoreCase);
            var imports = caller.Services.GetRequiredService<ServerWorldImportService>();
            var servers = caller.Services.GetRequiredService<DedicatedServerCatalog>().GetServers().Select(s =>
                new WorldServer(s.UniqueName, string.IsNullOrWhiteSpace(s.DisplayName) ? s.UniqueName : s.DisplayName, s.WorldSaveName,
                    profiles.GetProfile(s.ConfigProfileId)?.Name ?? s.ConfigProfileId,
                    (process.GetValueOrDefault(s.UniqueName)?.State ?? DedicatedServerProcessState.Stopped).ToString(),
                    s.GoalState.ToString(), imports.CanImport(s), s.UpdatedAtUtc)).ToArray();
            var rows = templates.GetTemplates().Select(t =>
            {
                token.ThrowIfCancellationRequested();
                var directory = new DirectoryInfo(templates.GetWorldDirectory(t.WorldTemplateId));
                long bytes = directory.Exists ? directory.EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }).Sum(f => f.Length) : 0;
                return new WorldTemplate(t.WorldTemplateId, t.Name, t.Description, t.UpdatedAtUtc, directory.Exists, (long)Math.Round(bytes / 1048576d));
            }).ToArray();
            return new(servers, rows, profiles.GetProfiles().Select(p => new ConversionDestination(p.ConfigProfileId, p.Name)).ToArray(),
                await Allowed(caller, QuasarPolicyNames.CanEditServers), await Allowed(caller, QuasarPolicyNames.CanEditConfigs));
        }, Read with { Description = "Lists standalone saves, server state and reusable world templates for the Worlds page." });
        services.AddQuasarOperation<EmptyRequest, InstalledWorlds>(PluginId, "worlds.installed", [QuasarPolicyNames.CanEditConfigs], (caller, _, token) =>
        {
            var discovery = caller.Services.GetRequiredService<WorldTemplateImportLocationService>().DiscoverInstalledWorldTemplates(token);
            bool ready = caller.Services.GetRequiredService<ManagedRuntimeWarmupService>().GetSnapshot().Components.Any(c =>
                c.Component == ManagedRuntimeInstallComponent.DedicatedServer && c.State == ManagedRuntimeComponentState.Ready);
            return Task.FromResult(new InstalledWorlds(discovery.Templates.Select(w => new InstalledWorld(w.DisplayName, w.SourcePath, w.Category, w.SourceDisplayPath, w.Description)).ToArray(),
                discovery.Locations.ToArray(), discovery.Warnings.ToArray(), ready));
        }, Read with { Description = "Discovers predefined worlds and scenarios in the worker's managed Dedicated Server installations." });
        services.AddQuasarOperation<DeleteInput, bool>(PluginId, "worlds.delete", [QuasarPolicyNames.CanEditConfigs], async (caller, input, token) =>
        {
            var templates = caller.Services.GetRequiredService<QuasarWorldTemplateCatalog>();
            if (templates.GetTemplate(input.Id)?.UpdatedAtUtc != input.Revision) throw new QuasarOperationException(409, "The template changed. Refresh before deleting.");
            await templates.DeleteAsync(input.Id, token);
            return true;
        }, Change with { Description = "Deletes the selected world template and its stored files." });
        RegisterWorkflow(services, server: true);
        RegisterWorkflow(services, server: false);
    }

    private static void RegisterWorkflow(IServiceCollection services, bool server)
    {
        string prefix = server ? "worlds.server." : "worlds.template.";
        string[] policies = [server ? QuasarPolicyNames.CanEditServers : QuasarPolicyNames.CanEditConfigs];
        services.AddQuasarOperation<WorldFolderQuery, WorldFolders>(PluginId, prefix + "browse", policies, (caller, input, token) =>
        {
            var files = caller.Services.GetRequiredService<FileBrowserService>();
            var locations = caller.Services.GetRequiredService<WorldTemplateImportLocationService>();
            var shortcuts = files.GetShortcuts().Concat(locations.GetContentShortcuts()).DistinctBy(s => s.FullPath).Select(s => new WorldFolder(s.Label, s.FullPath)).ToArray();
            var path = FileBrowserService.ResolvePath(input.Path ?? locations.GetContentShortcuts().FirstOrDefault()?.FullPath ?? "");
            try
            {
                return Task.FromResult(new WorldFolders(path, files.ListDirectories(path, input.Hidden).Select(f => new WorldFolder(f.Name, f.FullPath, f.IsWorldFolder)).ToArray(),
                    FileBrowserService.GetBreadcrumbs(path).Select(f => new WorldFolder(f.Label, f.FullPath)).ToArray(), shortcuts, FileBrowserService.IsWorldFolder(path)));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { throw new QuasarOperationException(400, error.Message); }
        }, Read with { Description = "Browses directories on the Quasar machine to choose a completed world save." });
        // Folder and commit documents can trigger multi-gigabyte file operations. The SDK write
        // keeps them cancellable throughout the copy, and keeps paths and settings out of audit payloads.
        services.AddQuasarUpload<WorldTarget, ReadWorld>(PluginId, prefix + "folder", policies, QuasarSdkLimits.MaximumJsonBytes, (caller, input, body, _) =>
            Task.FromResult(new QuasarUpload<ReadWorld>(async token => await StageAsync(caller, input, server,
                await Document<WorldSource>(body, token), null, token))), Change with { Description = "Stages and inspects a completed server-side world folder for review." });
        services.AddQuasarUpload<WorldTarget, ReadWorld>(PluginId, prefix + "zip", policies, WorldSaveFiles.MaxUploadBytes, (caller, input, body, _) =>
            Task.FromResult(new QuasarUpload<ReadWorld>(token => StageAsync(caller, input, server, null, body, token))),
            Change with { Description = "Stages a streamed world ZIP and returns its inspected settings and mods for review." });
        services.AddQuasarUpload<WorldTarget, WorldSaved>(PluginId, prefix + "commit", policies, QuasarSdkLimits.MaximumJsonBytes, (caller, input, body, _) =>
            Task.FromResult(new QuasarUpload<WorldSaved>(async token => await CommitAsync(caller, input, server, await Document<WorldCommit>(body, token), token))),
            Change with { Description = "Publishes a previously reviewed world and applies its selected profile options." });
        services.AddQuasarOperation<WorldTarget, WorldTransferProgress?>(PluginId, prefix + "progress", policies, (caller, input, _) =>
        {
            CheckKind(input, server);
            return Task.FromResult(caller.Services.GetRequiredService<DashboardWorldStaging>().Status(caller.User, input));
        }, Read with { Description = "Reads progress from the caller's selected world import." });
        services.AddQuasarOperation<WorldTarget, bool>(PluginId, prefix + "discard", policies, (caller, input, _) =>
        {
            CheckKind(input, server);
            caller.Services.GetRequiredService<DashboardWorldStaging>().Discard(caller.User, input);
            return Task.FromResult(true);
        }, Change with { Description = "Discards the caller's unused world staging files." });
    }

    private static async Task<T> Document<T>(Stream body, CancellationToken token) =>
        await JsonSerializer.DeserializeAsync<T>(body, Json, token) ?? throw new QuasarOperationException(400, "A world import document is required.");

    private static void CheckKind(WorldTarget target, bool server)
    {
        if (target.Id == Guid.Empty || server != !string.IsNullOrWhiteSpace(target.Server) || server != target.Revision.HasValue)
            throw new QuasarOperationException(400, "Select a valid target for this import.");
    }

    private static DedicatedServerDefinition GetServer(QuasarOperationContext caller, WorldTarget target)
    {
        var definition = caller.Services.GetRequiredService<DedicatedServerCatalog>().GetServer(target.Server!);
        if (definition is null || definition.UpdatedAtUtc != target.Revision)
            throw new QuasarOperationException(409, "The selected server changed. Reopen the world import.");
        return definition;
    }

    private static async Task<ReadWorld> StageAsync(QuasarOperationContext caller, WorldTarget target, bool server, WorldSource? source,
        Stream? zip, CancellationToken token)
    {
        CheckKind(target, server);
        var expected = server ? GetServer(caller, target) : null;
        var imports = caller.Services.GetRequiredService<ServerWorldImportService>();
        if (expected is not null && !imports.CanImport(expected)) throw new QuasarOperationException(409, "Stop the server and set its goal Off before importing.");
        var staging = caller.Services.GetRequiredService<DashboardWorldStaging>();
        var entry = staging.Begin(caller.User, target);
        bool ready = false;
        try
        {
            var parent = expected is null ? Path.Combine(Path.GetTempPath(), "quasar-world-imports") : DedicatedServerPathResolver.Resolve(expected).SavesPath;
            var progress = new CopyProgress(p => entry.Progress = new(p.Phase, p.Bytes, p.CompletedFiles, p.TotalFiles));
            string name;
            if (source?.Template is { } existing && !server)
            {
                var templates = caller.Services.GetRequiredService<QuasarWorldTemplateCatalog>();
                var template = templates.GetTemplate(existing) ?? throw new QuasarOperationException(404, "World template no longer exists.");
                entry.Stage.Path = templates.GetWorldDirectory(template.WorldTemplateId);
                if (!Directory.Exists(entry.Stage.Path)) throw new QuasarOperationException(404, "Template files are missing.");
                entry.Stage.TemplateId = template.WorldTemplateId;
                name = template.Name;
            }
            else
            {
                if (zip is null && (source?.Stopped != true || string.IsNullOrWhiteSpace(source.Path)))
                    throw new QuasarOperationException(400, "Choose a stopped world or completed snapshot.");
                entry.Stage.Files = zip is not null ? await WorldSaveFiles.FromZipAsync(zip, parent, progress, token)
                    : await imports.StageFolderAsync(source!.Path!, parent, progress, token);
                entry.Stage.Path = entry.Stage.Files.WorldPath;
                name = entry.Stage.Files.SuggestedName;
            }
            await caller.EnsureAuthorizedAsync(token);
            ReadWorld result;
            try
            {
                var imported = ReadProfile(entry);
                result = new(name, imported.SessionSettingCount, imported.Profile.Mods.Select(m => new WorldMod(m.WorkshopId.ToString(), m.DisplayName)).ToArray(), null);
            }
            catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException or System.Xml.XmlException or UnauthorizedAccessException)
            { result = new(name, 0, [], error.Message); }
            entry.Progress = new("Ready to import", 0);
            ready = true;
            return result;
        }
        catch (Exception error) when (error is InvalidDataException or InvalidOperationException or ArgumentException or IOException or UnauthorizedAccessException)
        { throw new QuasarOperationException(400, error.Message); }
        finally { staging.Release(entry, !ready); }
    }

    private static WorldSandboxConfigEditor.ConfigProfileImport ReadProfile(DashboardWorldStaging.Entry entry) =>
        WorldSandboxConfigEditor.ReadConfigProfile(Path.Combine(entry.Stage.Path, WorldSandboxConfigEditor.SandboxConfigFileName));

    private static async Task<WorldSaved> CommitAsync(QuasarOperationContext caller, WorldTarget target, bool server, WorldCommit input, CancellationToken token)
    {
        CheckKind(target, server);
        var staging = caller.Services.GetRequiredService<DashboardWorldStaging>();
        var entry = staging.Claim(caller.User, target);
        bool discard = false;
        try
        {
            await caller.EnsureAuthorizedAsync(token);
            entry.Progress = new("Importing world", 0);
            if (server)
            {
                bool profile = input.ProfileAction switch
                { "new" => true, "keep" => false, _ => throw new QuasarOperationException(400, "Choose how to apply world settings.") };
                if (profile && !await Allowed(caller, QuasarPolicyNames.CanEditConfigs))
                    throw new QuasarOperationException(403, "Configuration permission is required to import settings.");
                var updated = await caller.Services.GetRequiredService<ServerWorldImportService>().ImportAsync(GetServer(caller, target), entry.Stage.Files!, input.Name, profile, token);
                discard = true;
                return new(null, updated.ConfigProfileId, updated.UniqueName, ["Imported and selected " + updated.WorldSaveName + "."]);
            }

            if (input.ProfileAction is not ("keep" or "existing" or "new") || input.ProfileOnly && (input.ProfileAction != "new" || entry.Stage.TemplateId is null))
                throw new QuasarOperationException(400, "Select a valid template profile action.");
            if (input.ProfileAction == "new" && string.IsNullOrWhiteSpace(input.ProfileName) || !input.ProfileOnly && string.IsNullOrWhiteSpace(input.Name))
                throw new QuasarOperationException(400, "A name is required.");
            var profiles = caller.Services.GetRequiredService<QuasarConfigProfileCatalog>();
            QuasarConfigProfile? profileToSave = null;
            var messages = new List<string>();
            if (input.ProfileAction != "keep")
            {
                var imported = ReadProfile(entry);
                if (input.ProfileAction == "new")
                {
                    profileToSave = imported.Profile;
                    profileToSave.ConfigProfileId = "";
                    profileToSave.Name = input.ProfileName!.Trim();
                    profileToSave.Description = input.ProfileDescription?.Trim() ?? "";
                    QuasarDefaultPlugins.Apply(profileToSave, enable: true);
                }
                else
                {
                    profileToSave = profiles.GetProfile(input.ProfileId ?? "") ?? throw new QuasarOperationException(404, "The selected profile no longer exists.");
                    var existing = profileToSave.Mods.Select(m => m.WorkshopId).ToHashSet();
                    var additions = imported.Profile.Mods.Where(m => m.WorkshopId > 0 && existing.Add(m.WorkshopId)).ToArray();
                    profileToSave.Mods.AddRange(additions);
                    messages.Add($"Imported {additions.Length} mod(s) into {profileToSave.Name}.");
                }
                if (profileToSave.Mods.Count > 0)
                {
                    var resolved = await caller.Services.GetRequiredService<QuasarWorkshopModResolver>().ResolveDependenciesAsync(profileToSave.Mods, cancellationToken: token);
                    profileToSave.Mods = resolved.Mods.ToList();
                    profileToSave.RootSettings.AutodetectDependencies = resolved.Warnings.Count > 0;
                    messages.AddRange(resolved.Warnings);
                    if (resolved.AddedDependencyCount > 0) messages.Add($"Added {resolved.AddedDependencyCount} dependency mod(s).");
                }
            }
            await caller.EnsureAuthorizedAsync(token);
            var templateId = entry.Stage.TemplateId;
            if (!input.ProfileOnly)
            {
                var template = await caller.Services.GetRequiredService<QuasarWorldTemplateCatalog>().ImportAsync(input.Name.Trim(),
                    input.Description?.Trim() ?? "", entry.Stage.Path, token, new TemplateProgress(entry));
                templateId = template.WorldTemplateId;
                messages.Add($"Created world template '{template.Name}'.");
                // Publishing succeeded. Never copy the template a second time after a profile failure.
                discard = true;
            }
            if (profileToSave is not null)
            {
                if (input.ProfileAction == "new") profileToSave.SourceWorldTemplateId = templateId!;
                try
                {
                    await caller.EnsureAuthorizedAsync(token);
                    await profiles.UpsertAsync(profileToSave, token);
                }
                catch (Exception error) when (discard && error is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    messages.Add("The world template was created, but its profile could not be saved: " + error.Message);
                    return new(templateId, null, null, messages.ToArray());
                }
            }
            discard = true;
            return new(templateId, profileToSave?.ConfigProfileId, null, messages.ToArray());
        }
        catch (Exception error) when (error is InvalidOperationException or InvalidDataException or IOException or ArgumentException)
        { throw new QuasarOperationException(409, error.Message); }
        finally { staging.Release(entry, discard || entry.Stage.Files?.Published == true); }
    }

    private static async Task<bool> Allowed(QuasarOperationContext caller, string policy) =>
        (await caller.Services.GetRequiredService<IAuthorizationService>().AuthorizeAsync(caller.User, null, policy)).Succeeded;
}
