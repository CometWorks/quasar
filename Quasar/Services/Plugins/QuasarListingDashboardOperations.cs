using Microsoft.AspNetCore.Authorization;
using Quasar.Client.Administration;
using Quasar.Plugin.Abstractions.Operations;
using Quasar.Services.Auth;
using Quasar.Services.ServerList;

namespace Quasar.Services.Plugins;

internal static class QuasarListingDashboardOperations
{
    internal static void AddQuasarListingDashboard(this IServiceCollection services)
    {
        const string plugin = QuasarCoreOperations.PluginId;
        var write = new QuasarOperationMetadata { AgentAccess = QuasarAgentAccess.Denied, RequireDefaultPolicies = true };
        services.AddQuasarOperation<QuasarCoreOperations.EmptyRequest, ListingDashboard>(plugin, "listings.dashboard", [QuasarPolicyNames.CanView],
            async (caller, _, _) =>
            {
                var catalog = caller.Services.GetRequiredService<ServerListSettingsCatalog>();
                var (options, revision) = catalog.GetSnapshot();
                if (!options.Enabled) foreach (var listing in options.Listings) listing.Enabled = false;
                var servers = caller.Services.GetRequiredService<DedicatedServerCatalog>().GetServers();
                var clusters = caller.Services.GetRequiredService<ClusterCatalog>().GetClusters();
                var drafts = ServerListSettingsCatalog.CreateDrafts(options, servers, clusters).Select(entry =>
                {
                    var name = entry.Kind == "server"
                        ? servers.FirstOrDefault(s => Matches(s.UniqueName, entry.UniqueName))?.DisplayName
                        : clusters.FirstOrDefault(s => Matches(s.UniqueName, entry.UniqueName))?.DisplayName;
                    return new ListingDraft(entry.Kind, entry.UniqueName, string.IsNullOrEmpty(name) ? entry.UniqueName : name, name is not null)
                    { Enabled = entry.Enabled, Name = entry.Name, PublicHost = entry.PublicHost, PublicPort = entry.PublicPort };
                }).ToArray();
                var rbac = caller.Services.GetRequiredService<RbacConfigCatalog>().GetConfig();
                return new(revision, ServerListProtocol.GetAdmins(rbac).Length > 0, await CanManage(caller), catalog.LoadError, drafts);
            }, write with { Effect = QuasarOperationEffect.Read });
        services.AddQuasarOperation<ListingSave, bool>(plugin, "listings.save", [QuasarPolicyNames.CanManageSecurity],
            async (caller, input, token) =>
            {
                if (!await CanManage(caller)) throw new QuasarOperationException(403, "You no longer have permission to manage server listings.");
                var catalog = caller.Services.GetRequiredService<ServerListSettingsCatalog>();
                var (saved, revision) = catalog.GetSnapshot();
                if (revision != input.Revision) throw new QuasarOperationException(409, "Listing settings changed in another session. Reload before saving.");
                if (input.Listings is null) throw new QuasarOperationException(400, "Invalid server listings.");
                var all = ServerListSettingsCatalog.CreateDrafts(saved, caller.Services.GetRequiredService<DedicatedServerCatalog>().GetServers(),
                    caller.Services.GetRequiredService<ClusterCatalog>().GetClusters());
                var entries = new List<ServerListEntry>();
                var seen = new HashSet<(string, string)>();
                foreach (var draft in input.Listings)
                {
                    if (draft is null || draft.UniqueName is null || draft.Name is null || draft.PublicHost is null || !seen.Add((draft.Kind, draft.UniqueName.ToLowerInvariant())))
                        throw new QuasarOperationException(400, "Listings must identify distinct sources.");
                    var existing = saved.Listings.Any(item => item.Kind == draft.Kind && Matches(item.UniqueName, draft.UniqueName));
                    if (!draft.Enabled && string.IsNullOrWhiteSpace(draft.PublicHost) && !existing) continue;
                    var entry = all.FirstOrDefault(item => item.Kind == draft.Kind && Matches(item.UniqueName, draft.UniqueName))
                        ?? throw new QuasarOperationException(409, "A listing source no longer exists. Reload your settings.");
                    entry.Enabled = draft.Enabled; entry.Name = draft.Name; entry.PublicHost = draft.PublicHost; entry.PublicPort = draft.PublicPort;
                    entries.Add(entry);
                }
                if (saved.Listings.Any(item => !seen.Contains((item.Kind, item.UniqueName.ToLowerInvariant()))))
                    throw new QuasarOperationException(400, "Saved listings must be retained.");
                try { await catalog.SaveAsync(new() { Enabled = entries.Any(s => s.Enabled), Listings = entries }, input.Revision, token); }
                catch (InvalidDataException error) { throw new QuasarOperationException(400, error.Message); }
                catch (InvalidOperationException error) { throw new QuasarOperationException(409, error.Message); }
                return true;
            }, write);
    }

    private static async Task<bool> CanManage(QuasarOperationContext caller) =>
        (await caller.Services.GetRequiredService<IAuthorizationService>().AuthorizeAsync(caller.User, QuasarPolicyNames.CanManageSecurity)).Succeeded;

    private static bool Matches(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
