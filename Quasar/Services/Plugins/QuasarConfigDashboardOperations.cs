using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Quasar.Client.Configs;
using Quasar.Models;
using Quasar.Plugin.Abstractions.Operations;
using Quasar.Services.Auth;
using static Quasar.Services.Plugins.QuasarCoreOperations;

namespace Quasar.Services.Plugins;

internal static class QuasarConfigDashboardOperations
{
    private sealed record TextInput(string Value);
    private sealed record DirectoryInput(string Path, bool ShowHidden, bool Xml);
    private sealed record ModInput(IReadOnlyList<QuasarModSelection> Mods, bool SortLoadOrder = false);
    private sealed record IdsInput(IReadOnlyList<long> Ids);
    private sealed record DevInput(string FolderPath, string DataFile, bool DebugBuild = false);
    private sealed record AccountInput(string Username, string Password, bool Clear);
    private sealed record KeyInput(string WebApiKey);
    private sealed record ProfileUpload(bool Create);
    private static readonly string[] Edit = [QuasarPolicyNames.CanView, QuasarPolicyNames.CanEditConfigs];
    private static readonly QuasarOperationMetadata Change = new() { RequireDefaultPolicies = true, AgentAccess = QuasarAgentAccess.Denied };

    internal static void AddConfigDashboardOperations(this IServiceCollection services)
    {
        services.AddSingleton<ConfigDashboardVersion>();
        services.AddQuasarOperation<EmptyRequest,ConfigRevision>(PluginId, "configs.revision", [QuasarPolicyNames.CanView],
            async (caller, _, _) => new(caller.Services.GetRequiredService<ConfigDashboardVersion>().Token, await MayEdit(caller)),
            Change with { Effect = QuasarOperationEffect.Read, Description = "Returns the current configuration revision and editor access." });
        Read<EmptyRequest, ConfigSnapshot>("snapshot", "Returns profiles, assignments and catalogs for the configuration editor.", async (c, _, ct) =>
        {
            var revision = c.Services.GetRequiredService<ConfigDashboardVersion>().Token;
            var edit = await MayEdit(c);
            var profiles = c.Services.GetRequiredService<QuasarConfigProfileCatalog>().GetProfiles();
            if (!edit)
                foreach (var p in profiles)
                    p.RootSettings.ServerPassword = string.IsNullOrEmpty(p.RootSettings.ServerPassword) ? "" : "configured";
            var catalog = edit ? c.Services.GetRequiredService<QuasarPluginCatalogService>() : null;
            var credentials = edit ? c.Services.GetRequiredService<SteamWorkshopCredentialsCatalog>() : null;
            return new ConfigSnapshot
            {
                Version = revision, CanEdit = edit, Profiles = profiles, Servers = Assignments(c),
                DevFolders = edit ? c.Services.GetRequiredService<QuasarDevFolderCatalog>().GetDevFolders() : [],
                Entries = catalog?.GetEntries() ?? [], HubEntries = catalog?.GetHubEntries() ?? [], HubStatuses = catalog?.GetHubStatuses() ?? [],
                LastError = catalog?.LastError ?? "", HasWebApiKey = credentials?.HasWebApiKey == true, SteamUsername = credentials?.GetCredentials().SteamUsername ?? ""
            };
        }, [QuasarPolicyNames.CanView]);
        Write<QuasarConfigOperations.ProfileSaveRequest, string>("create", "Creates a new configuration profile.", async (c, input, ct) =>
        {
            var profile = input.Profile;
            if (profile is null || !string.IsNullOrWhiteSpace(profile.ConfigProfileId) || string.IsNullOrWhiteSpace(profile.Name))
                throw new QuasarOperationException(400, "A new profile needs a name and an empty ID.");
            await c.Services.GetRequiredService<QuasarConfigProfileCatalog>().UpsertAsync(profile, ct);
            return profile.ConfigProfileId;
        });
        services.AddQuasarUpload<ProfileUpload,string>(PluginId, "configs.profile.upload", Edit, 16 * 1024 * 1024,
            (caller, input, stream, _) => Task.FromResult(new QuasarUpload<string>(async token =>
            {
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: token);
                await caller.EnsureAuthorizedAsync(token);
                // Use the existing profile operation so its policy, audit entry and undo blobs apply on every transport.
                var result = await caller.Services.GetRequiredService<IQuasarOperationDispatcher>().DispatchAsync(new(caller.User, caller.Services),
                    PluginId, input.Create ? "configs.create" : "config.profile.save", document.RootElement, token);
                if (result.Error is not null) throw new QuasarOperationException(result.StatusCode, result.Error);
                return input.Create ? (string)result.Value! : ((QuasarConfigOperations.SaveResult)result.Value!).Target;
            })), Change with { Description = "Saves an uploaded configuration profile after checking its revision." });
        Write<TextInput, bool>("delete", "Deletes the reviewed configuration profile.", async (c, input, ct) =>
        {
            if (Assignments(c).Any(s => s.ConfigProfileId.Equals(input.Value, StringComparison.OrdinalIgnoreCase)))
                throw new QuasarOperationException(409, "Profile still assigned. Reassign or clear it first.");
            await c.Services.GetRequiredService<QuasarConfigProfileCatalog>().DeleteAsync(input.Value, ct);
            return true;
        });
        Write<EmptyRequest, bool>("catalog.refresh", "Refreshes the available plugin catalog.", async (c, _, ct) =>
        {
            await c.Services.GetRequiredService<QuasarPluginCatalogService>().RefreshAsync(ct);
            return true;
        });
        Write<QuasarPluginHub, QuasarPluginHub>("hubs.add", "Adds a plugin hub to the catalog.", async (c, hub, ct) =>
        {
            await c.Services.GetRequiredService<QuasarPluginHubCatalog>().AddAsync(hub, ct);
            return QuasarPluginHubCatalog.Normalize(hub);
        });
        Write<TextInput, bool>("hubs.remove", "Removes a plugin hub from the catalog.", async (c, input, ct) =>
        {
            await c.Services.GetRequiredService<QuasarPluginHubCatalog>().RemoveAsync(input.Value, ct);
            return true;
        });
        Write<TextInput, string>("dev.add", "Adds a development plugin source.", async (c, input, ct) =>
        {
            var (folderPath, dataFile) = PluginManifestReader.ResolveDevFolder(input.Value);
            var folders = c.Services.GetRequiredService<QuasarDevFolderCatalog>();
            if (string.IsNullOrWhiteSpace(folderPath) || string.IsNullOrWhiteSpace(dataFile))
                throw new QuasarOperationException(400, "Select a plugin manifest.");
            if (folders.GetDevFolder(folderPath, dataFile) is not null)
                throw new QuasarOperationException(409, "Dev folder already registered.");
            var manifest = Path.Combine(folderPath, dataFile);
            PluginManifestReader.ValidateManifest(manifest);
            var metadata = PluginManifestReader.ReadMetadata(manifest);
            var name = QuasarDevFolderSelection.GetSourceFolderName(folderPath);
            await folders.UpsertAsync(new() { FolderPath = folderPath, DataFile = dataFile, DebugBuild = true,
                Name = string.IsNullOrWhiteSpace(metadata.FriendlyName) ? name : metadata.FriendlyName,
                PluginId = string.IsNullOrWhiteSpace(metadata.Id) ? name : metadata.Id }, ct);
            return Path.GetDirectoryName(input.Value) ?? folderPath;
        });
        Write<DevInput, bool>("dev.debug", "Changes debugging for a development plugin.", async (c, input, ct) =>
        {
            var folders = c.Services.GetRequiredService<QuasarDevFolderCatalog>();
            var entry = folders.GetDevFolder(input.FolderPath, input.DataFile) ?? throw new QuasarOperationException(404, "Dev folder not found.");
            entry.DebugBuild = input.DebugBuild;
            await folders.UpsertAsync(entry, ct);
            return true;
        });
        Write<DevInput, bool>("dev.remove", "Removes a development plugin source.", async (c, input, ct) =>
        {
            await c.Services.GetRequiredService<QuasarDevFolderCatalog>().DeleteAsync(input.FolderPath, input.DataFile, ct);
            return true;
        });
        Write<KeyInput, bool>("workshop.key", "Updates the Steam Workshop API key.", async (c, input, ct) =>
        {
            if (string.IsNullOrWhiteSpace(input.WebApiKey)) throw new QuasarOperationException(400, "Enter the replacement Steam Web API key.");
            var credentials = c.Services.GetRequiredService<SteamWorkshopCredentialsCatalog>();
            var current = credentials.GetCredentials();
            current.WebApiKey = input.WebApiKey.Trim();
            await credentials.SaveAsync(current, ct);
            return true;
        });
        Write<AccountInput, bool>("workshop.account", "Updates the account used for Steam Workshop.", async (c, input, ct) =>
        {
            if (!input.Clear && string.IsNullOrWhiteSpace(input.Username)) throw new QuasarOperationException(400, "Steam account name required.");
            var credentials = c.Services.GetRequiredService<SteamWorkshopCredentialsCatalog>();
            var current = credentials.GetCredentials();
            current.SteamUsername = input.Clear ? "" : input.Username.Trim();
            if (input.Clear || !string.IsNullOrEmpty(input.Password)) current.SteamPassword = input.Clear ? "" : input.Password;
            await credentials.SaveAsync(current, ct);
            return true;
        });
        Read<TextInput, QuasarWorkshopSearchResultSet>("workshop.search", "Searches the Steam Workshop catalog.", (c, input, ct) =>
            string.IsNullOrWhiteSpace(input.Value) ? Workshop(c).GetPopularModsAsync(ct) : Workshop(c).SearchModsAsync(input.Value, ct));
        Read<TextInput, QuasarWorkshopResolutionResult>("workshop.resolve", "Resolves the selected Workshop item or collection.", (c, input, ct) => Workshop(c).ResolveAsync(input.Value, ct));
        Read<ModInput, QuasarModDependencyResolutionResult>("workshop.dependencies", "Resolves dependencies for selected Workshop mods.", (c, input, ct) => Workshop(c).ResolveDependenciesAsync(input.Mods, input.SortLoadOrder, ct));
        Read<ModInput, QuasarWorkshopAvailabilityResult>("workshop.availability", "Checks Steam Workshop availability.", (c, input, ct) => Workshop(c).CheckAvailabilityAsync(input.Mods, ct));
        Read<IdsInput, IReadOnlyDictionary<long,string>>("workshop.names", "Resolves display names for Workshop IDs.", (c, input, ct) => Workshop(c).GetDisplayNamesAsync(input.Ids, ct));
        Read<DirectoryInput, ConfigDirectory>("directory", "Lists directories on the worker for the configuration editor.", (c, input, _) =>
        {
            var browser = c.Services.GetRequiredService<FileBrowserService>();
            var path = FileBrowserService.ResolvePath(input.Path);
            return Task.FromResult(new ConfigDirectory(path, FileBrowserService.IsWorldFolder(path), browser.GetShortcuts(),
                FileBrowserService.GetBreadcrumbs(path), browser.ListDirectories(path, input.ShowHidden), input.Xml ? browser.ListXmlFiles(path, input.ShowHidden) : []));
        });
        Read<EmptyRequest, ConfigTemplate[]>("templates", "Lists world templates available to the editor.", (c, _, _) => Task.FromResult(c.Services.GetRequiredService<QuasarWorldTemplateCatalog>().GetTemplates()
            .Select(t => new ConfigTemplate(t.WorldTemplateId, t.Name)).ToArray()));
        Read<TextInput, IReadOnlyList<QuasarModSelection>>("templates.mods", "Returns the selected world template's mod list.", (c, input, _) =>
        {
            var catalog = c.Services.GetRequiredService<QuasarWorldTemplateCatalog>();
            var template = catalog.GetTemplate(input.Value) ?? throw new QuasarOperationException(404, "World template not found.");
            return Task.FromResult<IReadOnlyList<QuasarModSelection>>(WorldSandboxConfigEditor.ReadMods(Path.Combine(catalog.GetWorldDirectory(template.WorldTemplateId), WorldSandboxConfigEditor.SandboxConfigFileName)));
        });

        void Write<T, R>(string name, string description, Func<QuasarOperationContext,T,CancellationToken,Task<R>> handler) =>
            services.AddQuasarOperation<T,R>(PluginId, "configs." + name, Edit, (c, input, ct) => handler(c, input, ct), Change with { Description = description });
        void Read<T, R>(string name, string description, Func<QuasarOperationContext,T,CancellationToken,Task<R>> handler, string[]? policies = null) =>
            services.AddQuasarRead<T>(PluginId, "configs." + name, policies ?? Edit, async (c, input, ct) =>
            {
                var result = await handler(c, input, ct);
                var bytes = JsonSerializer.SerializeToUtf8Bytes(result, ConfigDashboard.Wire);
                return new(_ => Task.FromResult<Stream>(new MemoryStream(bytes, false)), "application/json", Length: bytes.Length);
            }, Change with { Description = description, Effect = QuasarOperationEffect.Read });
    }

    private static async Task<bool> MayEdit(QuasarOperationContext caller) =>
        (await caller.Services.GetRequiredService<IAuthorizationService>().AuthorizeAsync(caller.User, null, QuasarPolicyNames.CanEditConfigs)).Succeeded;

    private static QuasarWorkshopModResolver Workshop(QuasarOperationContext c) => c.Services.GetRequiredService<QuasarWorkshopModResolver>();
    private static ConfigAssignment[] Assignments(QuasarOperationContext c) =>
        c.Services.GetRequiredService<DedicatedServerCatalog>().GetServers().Select(s => new ConfigAssignment(s.UniqueName, s.ConfigProfileId))
        .Concat((c.Services.GetService<ClusterCatalog>()?.GetClusters() ?? []).Select(s => new ConfigAssignment(s.UniqueName, s.ConfigProfileId))).ToArray();
}
