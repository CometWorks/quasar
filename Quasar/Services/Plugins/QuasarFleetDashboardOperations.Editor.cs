using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Quasar.Client.Fleet;
using Quasar.Models;
using Quasar.Plugin.Abstractions.Operations;
using Quasar.Services.Auth;
using static Quasar.Services.Plugins.QuasarCoreOperations;

namespace Quasar.Services.Plugins;

internal static partial class QuasarFleetDashboardOperations
{
    private static readonly string[] EditServer = [QuasarPolicyNames.CanEditServers];
    private static readonly string[] EditServerProfile = [QuasarPolicyNames.CanEditServers, QuasarPolicyNames.CanEditConfigs];

    private static void RegisterEditor(IServiceCollection services)
    {
        services.AddQuasarRead<EditorSelection>(PluginId, "fleet.editor", EditServer, async (caller, selection, token) =>
        {
            var catalog = caller.Services.GetRequiredService<DedicatedServerCatalog>();
            var servers = catalog.GetServers();
            var current = string.IsNullOrEmpty(selection.UniqueName) ? null : RequireServer(caller.Services, selection.UniqueName);
            bool active = current is not null && Active(caller.Services, current.UniqueName);
            var ports = servers.Select(s => s.ServerPort).ToHashSet();
            int port = Enumerable.Range(27016, 65535 - 27016 + 1).FirstOrDefault(p => !ports.Contains(p));
            bool health = !caller.Services.GetRequiredService<WebServiceOptions>().DisableServerHealthMonitoring;
            var draft = current?.Clone() ?? new DedicatedServerDefinition
            {
                DisplayName = IdentifierSlug.SuggestName("server"),
                ServerPort = port,
                EnableHealthMonitoring = health,
                AutoRestartOnUnhealthy = health,
                AgentStartupHardLimitSeconds = DedicatedServerDefinition.DefaultAgentStartupHardLimitSeconds,
                ConfigProfileId = selection.InitialProfile,
                WorldTemplateId = selection.InitialTemplate,
                MinimumSimulationProgressScore = .05f
            };
            if (selection.IsClone && current is not null)
            {
                draft.DisplayName = (string.IsNullOrWhiteSpace(current.DisplayName) ? current.UniqueName : current.DisplayName) + " Copy";
                draft.UniqueName = catalog.GenerateUniqueServerName(current.UniqueName + "-copy");
                draft.ServerPort = port; draft.AutoStart = false;
                draft.DedicatedServerAppDataPath = draft.MagnetarAppDataPath = draft.WorldPath = draft.WorldSaveName = draft.ConfigFilePath = "";
            }
            if (string.IsNullOrEmpty(draft.UniqueName)) draft.UniqueName = catalog.GenerateUniqueServerName(draft.DisplayName);
            if (!OperatingSystem.IsWindows()) draft.ManagedRuntime = ManagedServerRuntime.DotNet10;
            // Older server.json files could point WorldPath directly at the selected save.
            if (string.IsNullOrWhiteSpace(draft.WorldSaveName) && !string.IsNullOrWhiteSpace(draft.WorldPath))
            {
                var path = DedicatedServerPathResolver.ResolvePath(draft.WorldPath, DedicatedServerPathResolver.Resolve(draft).SavesPath);
                if (File.Exists(Path.Combine(path, "Sandbox.sbc")) && Directory.GetParent(path) is { } parent)
                { draft.WorldSaveName = Path.GetFileName(Path.TrimEndingDirectorySeparator(path)); draft.WorldPath = DedicatedServerPathResolver.ToStoredPath(parent.FullName, null); }
            }
            var auth = caller.Services.GetRequiredService<IAuthorizationService>();
            var setup = new ServerEditorSetup(draft, current?.UpdatedAtUtc, active, OperatingSystem.IsWindows(), Environment.ProcessorCount,
                servers.Select(s => new ExistingServer(s.UniqueName, s.ServerPort)).ToArray(),
                caller.Services.GetRequiredService<QuasarConfigProfileCatalog>().GetProfiles().Select(p => new FleetProfile(p.ConfigProfileId, p.Name, p.SourceWorldTemplateId)).ToArray(),
                caller.Services.GetRequiredService<QuasarWorldTemplateCatalog>().GetTemplates().Select(t => new WorldTemplate(t.WorldTemplateId, t.Name, t.Description, t.UpdatedAtUtc, true, 0)).ToArray(),
                (await auth.AuthorizeAsync(caller.User, null, QuasarPolicyNames.CanEditConfigs)).Succeeded);
            return EditorJson(setup);
        }, Read with { Description = "Returns settings and choices for editing a standalone server." });
        services.AddQuasarRead<ServerPathInput>(PluginId, "fleet.editor.paths", EditServer, (caller, input, _) =>
        {
            var d = Definition(input);
            var paths = DedicatedServerPathResolver.Resolve(d);
            var defaults = DedicatedServerPathResolver.Resolve(new() { UniqueName = d.UniqueName });
            var lookup = paths;
            if (!string.IsNullOrWhiteSpace(input.OriginalName) && input.OriginalName != d.UniqueName &&
                !Directory.Exists(paths.ServerRoot) && caller.Services.GetRequiredService<DedicatedServerCatalog>().GetServer(input.OriginalName) is { } original)
            {
                var old = d.Clone();
                old.UniqueName = original.UniqueName;
                lookup = DedicatedServerPathResolver.Resolve(old);
            }
            string[] overrides = [d.DedicatedServerAppDataPath, d.MagnetarAppDataPath, d.WorkingDirectory, d.ExecutablePath, d.ConfigFilePath, d.WorldPath];
            var saves = (Directory.Exists(lookup.SavesPath) ? Directory.EnumerateDirectories(lookup.SavesPath) : []).Select(p => Path.GetFileName(p))
                .Where(n => !n.StartsWith('.') && File.Exists(Path.Combine(lookup.SavesPath, n, "Sandbox.sbc"))).Order(StringComparer.OrdinalIgnoreCase)
                .Select(n => new EditorSave(n, n, true)).ToList();
            if (d.WorldSaveName.Length > 0 && !saves.Any(s => s.Name.Equals(d.WorldSaveName, StringComparison.OrdinalIgnoreCase)))
                saves.Add(new(d.WorldSaveName, d.WorldSaveName + " (unavailable)", false));
            var mods = MissingMods(caller.Services, d).Select(m => m.DisplayName).ToArray();
            return Task.FromResult(EditorJson(new ServerEditorPaths(paths.SavesPath, paths.WorldSavePath, defaults.SavesPath, defaults.ConfigFilePath,
                defaults.DedicatedServerAppDataPath, defaults.MagnetarAppDataPath, overrides.Any(p => DedicatedServerPathResolver.IsAbsoluteOutsideQuasarRoot(p)), saves.ToArray(), mods)));
        }, Read with { Description = "Previews the selected server's resolved directories." });
        services.AddQuasarOperation<EditorQuickProfile, string>(PluginId, "fleet.editor.profile.new", EditServerProfile, async (caller, input, token) =>
        {
            if (string.IsNullOrWhiteSpace(input.Name)) throw new QuasarOperationException(400, "A profile name is required.");
            var profile = new QuasarConfigProfile { Name = input.Name.Trim(), Description = input.Description?.Trim() ?? "" };
            QuasarDefaultPlugins.Apply(profile, true);
            await caller.Services.GetRequiredService<QuasarConfigProfileCatalog>().UpsertAsync(profile, token);
            return profile.ConfigProfileId;
        }, Change with { Description = "Creates a configuration profile for the server editor." });
        services.AddQuasarOperation<ServerPathInput, string>(PluginId, "fleet.editor.profile.merge", EditServerProfile, async (caller, input, token) =>
        {
            var d = Definition(input);
            var profiles = caller.Services.GetRequiredService<QuasarConfigProfileCatalog>();
            var profile = profiles.GetProfile(d.ConfigProfileId) ?? throw new QuasarOperationException(404, "Profile not found.");
            var missing = MissingMods(caller.Services, d);
            if (missing.Length == 0) return "Selected profile already has all world mods.";
            foreach (var mod in missing) profile.Mods.Add(mod);
            var resolved = await caller.Services.GetRequiredService<QuasarWorkshopModResolver>().ResolveDependenciesAsync(profile.Mods, cancellationToken: token);
            profile.Mods = resolved.Mods.ToList();
            profile.RootSettings.AutodetectDependencies = resolved.Warnings.Count > 0;
            await caller.EnsureAuthorizedAsync(token);
            await profiles.UpsertAsync(profile, token);
            return $"Merged {missing.Length} mod(s) into {profile.Name}. " + string.Join(" ", resolved.Warnings);
        }, Change with { Description = "Merges selected configuration settings into a profile." });
        services.AddQuasarOperation<SaveFromTemplate, string>(PluginId, "fleet.editor.world.new", EditServer, async (caller, input, token) =>
        {
            var d = Definition(input.Path);
            var saves = DedicatedServerPathResolver.Resolve(d).SavesPath;
            var templates = caller.Services.GetRequiredService<QuasarWorldTemplateCatalog>();
            var template = templates.GetTemplate(input.Template) ?? throw new QuasarOperationException(404, "Template not found.");
            var name = IdentifierSlug.CreateUnique(template.Name, "World", n => Directory.Exists(Path.Combine(saves, n)));
            using var staged = await WorldSaveFiles.FromFolderAsync(templates.GetWorldDirectory(template.WorldTemplateId), saves, cancellationToken: token);
            await caller.EnsureAuthorizedAsync(token);
            staged.Publish(Path.Combine(saves, name));
            return name;
        }, Change with { Description = "Creates a server world from the selected template.", Deadline = QuasarOperationMetadata.MaximumDeadline });
        RegisterSave("fleet.editor.update", true);
        RegisterSave("fleet.editor.save", false);
        void RegisterSave(string id, bool undo)
        {
            var metadata = Change with
            {
                Deadline = QuasarOperationMetadata.MaximumDeadline,
                Description = undo ? "Saves a reviewed standalone server definition and records its prior values for undo."
                    : "Creates, renames or clones a standalone server, optionally preparing a separate copy of its world."
            };
            if (undo) metadata = metadata with { Reversal = QuasarReversal.Undo, Undo = new QuasarConfigOperations.DefinitionUndo() };
            services.AddQuasarUpload<EmptyRequest, string>(PluginId, id, EditServer, 4 * 1024 * 1024,
                (caller, _, stream, token) => Task.FromResult(new QuasarUpload<string>(async t =>
                {
                    var input = await JsonSerializer.DeserializeAsync<ServerEditorCommit>(stream, cancellationToken: t)
                        ?? throw new QuasarOperationException(400, "A server definition is required.");
                    if (input.Source is null || input.Definition is null)
                        throw new QuasarOperationException(400, "A server definition and source are required.");
                    if (undo != (!input.Source.IsClone && input.Source.UniqueName.Length > 0 && input.Source.UniqueName == input.Definition.UniqueName))
                        throw new QuasarOperationException(400, "The server editor action does not match the selection.");
                    await caller.EnsureAuthorizedAsync(t);
                    try { return await SaveEditor(caller, input, t); }
                    catch (InvalidOperationException e) { throw new QuasarOperationException(409, e.Message); }
                    catch (InvalidDataException e) { throw new QuasarOperationException(400, e.Message); }
                })), metadata);
        }
    }

    private static QuasarRead EditorJson<T>(T value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        return new(_ => Task.FromResult<Stream>(new MemoryStream(bytes, false)), "application/json", Length: bytes.Length);
    }
    private static bool Active(IServiceProvider services, string name) =>
        services.GetRequiredService<DedicatedServerSupervisor>().GetSnapshots().FirstOrDefault(s => s.UniqueName.Equals(name, StringComparison.OrdinalIgnoreCase))?.State
            is DedicatedServerProcessState.Starting or DedicatedServerProcessState.Running or DedicatedServerProcessState.Restarting or DedicatedServerProcessState.Stopping;

    private static DedicatedServerDefinition Definition(ServerPathInput input)
    {
        if (string.IsNullOrWhiteSpace(input.UniqueName) || !System.Text.RegularExpressions.Regex.IsMatch(input.UniqueName, "\\A[a-zA-Z0-9_-]+\\z"))
            throw new QuasarOperationException(400, "Enter a valid server identifier.");
        if (!string.IsNullOrEmpty(input.WorldSaveName)) WorldSaveFiles.ValidateSaveName(input.WorldSaveName);
        return new()
        {
            UniqueName = input.UniqueName,
            DedicatedServerAppDataPath = input.DedicatedServerAppDataPath,
            MagnetarAppDataPath = input.MagnetarAppDataPath,
            WorldPath = input.WorldPath,
            WorldSaveName = input.WorldSaveName,
            WorkingDirectory = input.WorkingDirectory,
            ConfigFilePath = input.ConfigFilePath,
            ExecutablePath = input.ExecutablePath,
            ConfigProfileId = input.ConfigProfileId,
            DisableImplicitMagnetarModLoad = input.DisableImplicitMagnetarModLoad
        };
    }
    private static QuasarModSelection[] MissingMods(IServiceProvider provider, DedicatedServerDefinition d)
    {
        var profile = provider.GetRequiredService<QuasarConfigProfileCatalog>().GetProfile(d.ConfigProfileId);
        if (profile is null || string.IsNullOrEmpty(d.WorldSaveName)) return [];
        var path = Path.Combine(d.GetWorldSavePath(), WorldSandboxConfigEditor.SandboxConfigFileName);
        if (!File.Exists(path)) return [];
        var installed = profile.Mods.Select(m => m.WorkshopId).ToHashSet();
        try
        {
            return WorldSandboxConfigEditor.ReadMods(path).Where(m => m.WorkshopId > 0 && installed.Add(m.WorkshopId) &&
                (d.DisableImplicitMagnetarModLoad || profile.RootSettings.CrossPlatform || !m.DisplayName.Equals("MagnetarMod", StringComparison.OrdinalIgnoreCase))).ToArray();
        }
        catch (Exception e) when (e is IOException or System.Xml.XmlException or InvalidDataException) { return []; }
    }

    private static async Task<string> SaveEditor(QuasarOperationContext caller, ServerEditorCommit input, CancellationToken token)
    {
        var catalog = caller.Services.GetRequiredService<DedicatedServerCatalog>();
        var draft = JsonSerializer.SerializeToElement(input.Definition).Deserialize<DedicatedServerDefinition>()
            ?? throw new QuasarOperationException(400, "Invalid server definition.");
        _ = Definition(ServerPathInput.From(draft));
        WorldSaveFiles.ValidateSaveName(draft.WorldSaveName);
        string sourceName = input.Source.UniqueName ?? "";
        bool copying = input.Source.IsClone && input.CopyWorld;
        if (input.Source.IsClone && sourceName.Length == 0) throw new QuasarOperationException(400, "Choose a server to clone.");
        ValidateEditor(caller.Services, draft);
        DedicatedServerPathResolver.CanonicalizeForStorage(draft);
        string destination = draft.GetWorldSavePath();
        string? backup = null;
        StagedWorld? staged = null;
        bool published = false;
        IDisposable? sourceReservation = null;
        DedicatedServerDefinition? before = null;
        try
        {
            if (copying)
            {
                if (!caller.Services.GetRequiredService<Quasar.Services.Backup.ServerRestoreCoordinator>().TryBeginBackup(sourceName, out sourceReservation))
                    throw new QuasarOperationException(409, "Another backup, restore or import is active on the source server.");
                var original = RequireServer(caller.Services, sourceName);
                if (original.GoalState == DedicatedServerGoalState.On && !Active(caller.Services, sourceName))
                    throw new QuasarOperationException(409, "Wait for the source server to finish starting or stop it before cloning.");
                ServerManagementActions.EnsureCloneStorageIsIndependent(original, draft);
                var source = original.GetWorldSavePath();
                if (Active(caller.Services, sourceName))
                    source = ServerManagementActions.FindLatestWorldBackupDirectory(source)
                        ?? throw new QuasarOperationException(409, "No Space Engineers Backup snapshot exists. Stop the source server or wait for a backup.");
                staged = await WorldSaveFiles.FromFolderAsync(source, Path.GetDirectoryName(destination)!, cancellationToken: token);
            }
            await caller.EnsureAuthorizedAsync(token);
            await catalog.SaveEditorAsync(draft, sourceName, input.Revision, input.Source.IsClone, current =>
            {
                before = current;
                if (current is not null && !input.Source.IsClone && !string.Equals(sourceName, draft.UniqueName, StringComparison.OrdinalIgnoreCase)
                    && Active(caller.Services, sourceName)) throw new InvalidOperationException("Stop the server before renaming it.");
                if (input.Source.IsClone) ServerManagementActions.EnsureCloneStorageIsIndependent(current!, draft);
                if (staged is null)
                {
                    var existingWorld = destination;
                    if (current is not null && !input.Source.IsClone && current.UniqueName != draft.UniqueName)
                    {
                        var beforeRename = draft.Clone();
                        beforeRename.UniqueName = current.UniqueName;
                        existingWorld = beforeRename.GetWorldSavePath();
                    }
                    if (!File.Exists(Path.Combine(existingWorld, "Sandbox.sbc"))) throw new InvalidOperationException("Selected save is missing Sandbox.sbc.");
                    return;
                }
                WorldSaveFiles.EnsureNoLinks(destination);
                foreach (var other in catalog.GetServers())
                {
                    var world = other.GetWorldSavePath();
                    if (DedicatedServerPathResolver.IsPathWithinRoot(destination, world) || DedicatedServerPathResolver.IsPathWithinRoot(world, destination))
                        throw new InvalidOperationException("The clone's save folder must be separate from every configured server's selected world.");
                }
                if (Directory.Exists(destination))
                {
                    if (!DedicatedServerPathResolver.PathsEqual(input.ReplaceWorld, destination) || !File.Exists(Path.Combine(destination, "Sandbox.sbc")))
                        throw new InvalidOperationException("Confirm replacement of the selected existing save before copying the source world.");
                    backup = Path.Combine(Path.GetDirectoryName(destination)!, ".clone-previous-" + Guid.NewGuid().ToString("N"));
                    Directory.Move(destination, backup);
                }
                staged.Publish(destination);
                published = true;
            }, () =>
            {
                if (published) WorldSaveFiles.Cleanup(destination);
                if (backup is not null && Directory.Exists(backup) && !Directory.Exists(destination)) Directory.Move(backup, destination);
            }, token);
        }
        finally { staged?.Dispose(); sourceReservation?.Dispose(); }
        if (backup is not null) WorldSaveFiles.Cleanup(backup);
        if (before is not null && !input.Source.IsClone && before.UniqueName == draft.UniqueName)
            await QuasarConfigOperations.RecordDefinitionUndoAsync(caller, before, catalog.GetServer(draft.UniqueName)!, token);
        return input.Source.IsClone ? copying ? "Server cloned with a copy of the source world." : "Server cloned with its selected save." : "Server saved.";
    }

    internal static void ValidateEditor(IServiceProvider services, DedicatedServerDefinition draft)
    {
        if (draft.ServerPort is < 1 or > 65535) throw new QuasarOperationException(400, "Port must be between 1 and 65535.");
        if (!CpuAffinitySpec.TryParse(draft.CpuAffinity, Environment.ProcessorCount, out _, out var error)) throw new QuasarOperationException(400, error ?? "Invalid CPU affinity.");
        if (services.GetRequiredService<QuasarConfigProfileCatalog>().GetProfile(draft.ConfigProfileId) is null)
            throw new QuasarOperationException(400, "Choose an existing configuration profile.");
        if (!OperatingSystem.IsWindows()) draft.ManagedRuntime = ManagedServerRuntime.DotNet10;
    }
}
