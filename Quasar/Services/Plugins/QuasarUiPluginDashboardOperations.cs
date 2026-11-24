using System.Text.Json;
using Quasar.Client.UiPlugins;
using Quasar.Plugin.Abstractions.Operations;
using QuasarAgentKind = Quasar.Plugin.Abstractions.Security.QuasarAgentKind;
using Quasar.Services.Auth;
using Quasar.Services.Updates;
using static Quasar.Services.Plugins.QuasarCoreOperations;

namespace Quasar.Services.Plugins;

internal static class QuasarUiPluginDashboardOperations
{
    internal sealed record Toggle(bool Enabled);
    internal sealed record Selection(string Id, string Commit);
    internal sealed record PluginState(string Id, bool Enabled);
    internal sealed record RuleInput(string PluginId, string OperationId, QuasarOperationRule? Rule);
    internal sealed record RoleInput(string Name, string[] Roles);
    internal sealed record RestartInput(Guid? UpdateId);
    internal sealed record ReviewedCompanionUpdates(string[] Keys);
    private static readonly string[] Manage = [QuasarPolicyNames.CanManageSecurity];
    private static readonly QuasarOperationMetadata Change = new() { RequireDefaultPolicies = true, AgentAccess = QuasarAgentAccess.Denied, Effect = QuasarOperationEffect.Change };
    private static readonly QuasarOperationMetadata Read = Change with { Effect = QuasarOperationEffect.Read };

    public static void AddQuasarUiPluginDashboardOperations(this IServiceCollection services)
    {
        services.AddSingleton<UiPluginInstallJobs>();
        services.AddQuasarRead<EmptyRequest>(PluginId, "ui-plugins.dashboard", Manage, (caller, _, token) =>
            Task.FromResult(JsonRead(Snapshot(caller, token))), Read with { Description = "Returns installed UI plugins, available catalog entries and diagnostics." });
        services.AddQuasarOperation<ReviewedCompanionUpdates, bool>(PluginId, "ui-plugins.companion-updates.review", Manage,
            async (caller, input, token) =>
            {
                try
                {
                    return await caller.Services.GetRequiredService<PushNotificationService>()
                        .ReviewCompanionUpdatesAsync(caller.User, input.Keys ?? [], token);
                }
                catch (InvalidDataException error) { throw new QuasarOperationException(400, error.Message); }
            }, Change with { Description = "Acknowledges the shown companion restart reminders for the current admin." });
        services.AddQuasarRead<EmptyRequest>(PluginId, "rbac.sdk-settings", Manage, async (caller, _, _) =>
        {
            var operations = await caller.Services.GetRequiredService<QuasarOperationService>().GetSettingsAsync(caller.User);
            var config = caller.Services.GetRequiredService<RbacConfigCatalog>().GetConfig();
            return JsonRead(new SdkPermissions(operations.Select(op => new EndpointAccess(op.PluginId, op.OperationId, op.DefaultPolicies,
                op.EffectiveRule is {} rule ? new(rule.Enabled, rule.RequiredPolicies ?? []) : null, op.Overridden, op.Error)).ToList(),
                config.PolicyOverrides, config.AgentRoles));
        }, Read with { Description = "Lists SDK operations, their effective policies and role assignments." });
        services.AddQuasarOperation<RuleInput, bool>(PluginId, "rbac.sdk-rule.save", Manage, async (caller, input, token) =>
        {
            await caller.Services.GetRequiredService<QuasarOperationService>().SaveRuleAsync(caller.User, input.PluginId, input.OperationId, input.Rule, token);
            return true;
        }, Change with { Description = "Updates the access rule for a plugin SDK operation." });
        services.AddQuasarOperation<RoleInput, bool>(PluginId, "rbac.policy-roles.save", Manage, async (caller, input, token) =>
        {
            await caller.Services.GetRequiredService<QuasarOperationService>().SavePolicyRolesAsync(caller.User, input.Name, input.Roles ?? [], token);
            return true;
        }, Change with { Description = "Updates the roles that satisfy the selected policy." });
        services.AddQuasarOperation<RoleInput, bool>(PluginId, "rbac.agent-roles.save", Manage, async (caller, input, token) =>
        {
            if (!Enum.TryParse<QuasarAgentKind>(input.Name, true, out var kind) || !Enum.IsDefined(kind)) throw new QuasarOperationException(400, "Unknown agent role.");
            await caller.Services.GetRequiredService<QuasarOperationService>().SaveAgentRolesAsync(caller.User, kind, input.Roles ?? [], token);
            return true;
        }, Change with { Description = "Sets roles assigned to the selected agent kind." });
        services.AddQuasarOperation<Toggle, bool>(PluginId, "ui-plugins.safe-mode", Manage, async (_, input, token) =>
        {
            var path = QuasarUiPluginCatalog.SafeModeMarkerPath;
            if (input.Enabled)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllTextAsync(path, $"Quasar UI plugin safe boot marker created {DateTimeOffset.UtcNow:u}.", token);
            }
            else File.Delete(path);
            return true;
        }, Change with { Description = "Changes the safe boot marker for the next worker start." });
        services.AddQuasarOperation<Toggle, bool>(PluginId, "ui-plugins.automatic-updates", Manage, async (caller, input, _) =>
        {
            await caller.Services.GetRequiredService<QuasarUpdateService>().SetAutoUpdateUiPluginsAsync(input.Enabled);
            return true;
        }, Change with { Description = "Updates the automatic UI plugin update setting." });
        services.AddQuasarOperation<EmptyRequest, bool>(PluginId, "ui-plugins.refresh", Manage, async (caller, _, token) =>
        {
            await Hub(caller).RefreshAsync(token);
            return true;
        }, Change with { Description = "Refreshes the UI plugin hub catalog.", Deadline = TimeSpan.FromMinutes(5) });
        services.AddQuasarOperation<EmptyRequest, QuasarDotNetSdkStatus>(PluginId, "ui-plugins.sdk.check", Manage,
            (caller, _, _) => Task.FromResult(Hub(caller).RefreshDotNetSdkStatus()), Read with { Description = "Checks the SDK available to build UI plugins." });
        services.AddQuasarOperation<EmptyRequest, UiPluginInstallJob>(PluginId, "ui-plugins.sdk.install", Manage, (caller, _, _) =>
        {
            var hub = Hub(caller);
            return Task.FromResult(Jobs(caller).Start(".NET SDK", hub.InstallDotNetSdkAsync));
        }, Change with { Description = "Starts installing the verified private SDK for UI plugin builds." });
        services.AddQuasarOperation<Selection, UiPluginInstallJob>(PluginId, "ui-plugins.install", Manage, (caller, input, _) =>
        {
            var hub = Hub(caller);
            var entry = Selected(hub, input);
            return Task.FromResult(Jobs(caller).Start(entry.CatalogId, async token =>
            {
                await hub.InstallOrUpdateAsync(entry, token);
                return new(true, $"{entry.CatalogId} installed.", "");
            }));
        }, Change with { Description = "Starts installing the reviewed plugin catalog revision." });
        services.AddQuasarOperation<Guid, UiPluginInstallJob>(PluginId, "ui-plugins.install.status", Manage,
            (caller, id, _) => Task.FromResult(Jobs(caller).Get(id)), Read with { Description = "Returns the outcome of a worker-managed plugin or private SDK installation." });
        services.AddQuasarOperation<Selection, bool>(PluginId, "ui-plugins.remove", Manage, async (caller, input, token) =>
        {
            var hub = Hub(caller);
            await hub.RemoveAsync(Selected(hub, input), token);
            return true;
        }, Change with { Description = "Removes the selected installed UI plugin." });
        services.AddQuasarOperation<PluginState, bool>(PluginId, "ui-plugins.enable", Manage, async (caller, input, token) =>
        {
            var catalog = caller.Services.GetRequiredService<QuasarUiPluginCatalog>();
            var hub = Hub(caller);
            if (!catalog.LoadedPlugins.Any(p => p.Id == input.Id) && !hub.GetEntries().Select(hub.GetInstallState).Any(p => p.Installed && p.RuntimePluginId == input.Id))
                throw new QuasarOperationException(404, "Installed plugin not found.");
            await caller.Services.GetRequiredService<QuasarUiPluginStateStore>().SetEnabledAsync(input.Id, input.Enabled, token);
            return true;
        }, Change with { Description = "Changes whether the selected UI plugin loads at worker startup." });
        services.AddQuasarOperation<RestartInput, bool>(PluginId, "ui-plugins.restart", Manage, async (caller, input, _) =>
        {
            var hub = Hub(caller);
            if (!hub.RestartRequired || hub.DotNetSdkInstallInProgress || Jobs(caller).Latest is { Result: null }) throw new QuasarOperationException(409, "Plugin restart state changed. Refresh before continuing.");
            await caller.Services.GetRequiredService<QuasarShutdownService>().RestartWorkerAsync(confirmedUpdateId: input.UpdateId);
            return true;
        }, Change with { Description = "Restarts the worker to apply reviewed plugin changes." });
    }

    private static UiPluginInstallJobs Jobs(QuasarOperationContext caller) => caller.Services.GetRequiredService<UiPluginInstallJobs>();

    private static QuasarUiPluginHubCatalogService Hub(QuasarOperationContext caller) => caller.Services.GetRequiredService<QuasarUiPluginHubCatalogService>();

    private static QuasarUiPluginHubEntry Selected(QuasarUiPluginHubCatalogService hub, Selection selection) =>
        hub.GetEntries().FirstOrDefault(e => e.CatalogId == selection.Id && e.Commit == selection.Commit)
        ?? throw new QuasarOperationException(409, "The catalog entry changed. Refresh before continuing.");

    private static QuasarRead JsonRead<T>(T value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        return new(_ => Task.FromResult<Stream>(new MemoryStream(bytes, false)), "application/json", Length: bytes.LongLength);
    }

    private static UiPluginDashboard Snapshot(QuasarOperationContext caller, CancellationToken token)
    {
        var services = caller.Services;
        var catalog = services.GetRequiredService<QuasarUiPluginCatalog>();
        var hub = services.GetRequiredService<QuasarUiPluginHubCatalogService>();
        var restart = services.GetRequiredService<WebServiceOptions>().RestartCapability;
        var update = services.GetRequiredService<QuasarShutdownService>().PendingRestartUpdate;
        var entries = hub.GetEntries().ToList();
        var installed = entries.ToDictionary(entry => entry.CatalogId, hub.GetInstallState);
        var companionNotices = services.GetRequiredService<PushNotificationService>()
            .UiPluginNotices(entries, entry => installed[entry.CatalogId], caller.User)
            .Where(notice => notice.Key.StartsWith(UpdateNotices.CompanionRestartKeyPrefix, StringComparison.Ordinal)).ToArray();
        return new(catalog.SafeMode, QuasarUiPluginCatalog.SafeModeMarkerPath, File.Exists(QuasarUiPluginCatalog.SafeModeMarkerPath),
            catalog.SafeModeReasons, catalog.LoadErrors, catalog.LoadedPlugins.Select(p => Describe(p, token)).ToList(),
            catalog.NavItems.Select(n => new NavDetails(n.Text, n.Href, n.Zone, n.Order, n.Policy)).ToList(),
            catalog.Extensions.Select(e => new ExtensionDetails(e.TargetKey, e.Mode.ToString(), e.ComponentType.FullName ?? e.ComponentType.Name, e.Priority, e.PluginId, e.Policy)).ToList(),
            catalog.RazorAssemblies.Select(a => new AssemblyDetails(a.GetName().Name, a.Location)).ToList(), entries,
            installed, hub.LastRefreshUtc, hub.LastError, services.GetRequiredService<QuasarUpdateOptions>().AutoUpdateUiPlugins,
            hub.RestartRequired, restart.CanRestart, restart.Detail + " " + restart.Repair, update?.Id, update?.Version, hub.GetDotNetSdkStatus(),
            hub.GetDotNetSdkInstallAvailability(), hub.DotNetSdkInstallInProgress, OperatingSystem.IsWindows(), services.GetRequiredService<UiPluginInstallJobs>().Latest,
            companionNotices);
    }

    private static PluginDetails Describe(QuasarLoadedUiPlugin plugin, CancellationToken token)
    {
        var ctx = plugin.Context;
        var output = Path.GetDirectoryName(plugin.EntryAssemblyPath) ?? "";
        var paths = new Dictionary<string, FileDetails>();
        foreach (var file in new[] { plugin.EntryAssemblyPath, plugin.LoadedAssemblyPath })
        {
            try { var info = new FileInfo(file); paths[file] = info.Exists ? new(null, info.Length, info.LastWriteTimeUtc) : new("missing", 0, null); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { paths[file] = new("unreadable", 0, null); }
        }
        foreach (var directory in new[] { output, plugin.ShadowCopyDirectory, ctx.StaticAssetsDirectory, plugin.SourceStaticAssetsDirectory, ctx.PluginDirectory })
            if (!string.IsNullOrEmpty(directory) && !paths.ContainsKey(directory)) paths[directory] = InspectDirectory(directory, token);
        bool Changed(string? source, DateTime? snapshot) => source is not null && paths.TryGetValue(source, out var info)
            && info.Modified is { } modified && snapshot is { } loaded && modified > loaded.AddSeconds(1);
        var manifest = ctx.Manifest;
        return new(plugin.Id, plugin.DisplayName, manifest.Version, manifest.EntryAssembly, manifest.EntryType, manifest.Stylesheets, plugin.EntryAssemblyPath, plugin.LoadedAssemblyPath,
            plugin.ShadowCopyDirectory, plugin.ShadowCopyHash, ctx.StaticAssetsDirectory, plugin.SourceStaticAssetsDirectory, ctx.PluginDirectory, output,
            $"{QuasarUiPluginCatalog.StaticAssetRequestPathPrefix}/{plugin.Id}/", Changed(output, InspectDirectory(plugin.ShadowCopyDirectory, token, ctx.StaticAssetsDirectory).Modified),
            Changed(plugin.SourceStaticAssetsDirectory, ctx.StaticAssetsDirectory is { } dir ? paths.GetValueOrDefault(dir)?.Modified : null), paths);
    }

    private static FileDetails InspectDirectory(string path, CancellationToken token, string? exclude = null)
    {
        try
        {
            if (!Directory.Exists(path)) return new("missing", 0, null);
            long count = 0;
            DateTime? latest = null;
            foreach (var file in new DirectoryInfo(path).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = false, AttributesToSkip = FileAttributes.ReparsePoint }))
            {
                token.ThrowIfCancellationRequested();
                if (exclude is not null && file.FullName.StartsWith(exclude + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
                count++;
                if (latest is null || file.LastWriteTimeUtc > latest) latest = file.LastWriteTimeUtc;
            }
            return new(null, count, latest);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return new("unreadable", 0, null); }
    }
}

internal sealed class UiPluginInstallJobs(IHostApplicationLifetime lifetime, ILogger<UiPluginInstallJobs> logger)
{
    private readonly object _gate = new();
    private readonly List<UiPluginInstallJob> _jobs = [];

    internal UiPluginInstallJob? Latest { get { lock (_gate) return _jobs.LastOrDefault(); } }

    internal UiPluginInstallJob Get(Guid id)
    {
        lock (_gate) return _jobs.Find(j => j.Id == id) ?? throw new QuasarOperationException(404, "Installation status expired. Refresh the installed plugin list.");
    }

    internal UiPluginInstallJob Start(string target, Func<CancellationToken, Task<QuasarDotNetSdkInstallResult>> install)
    {
        lock (_gate)
        {
            if (_jobs.LastOrDefault() is { Result: null })
                throw new QuasarOperationException(409, "An installation is already in progress.");
            var job = new UiPluginInstallJob(Guid.NewGuid(), target, null);
            if (_jobs.Count == 16) _jobs.RemoveAt(0);
            _jobs.Add(job);
            _ = Task.Run(async () =>
            {
                QuasarDotNetSdkInstallResult result;
                try { result = await install(lifetime.ApplicationStopping); }
                catch (Exception error)
                {
                    logger.LogWarning(error, "Installation of {Target} failed", target);
                    result = new(false, error.Message, "");
                }
                if (result.Output.Length > 8000) result = result with { Output = result.Output[^8000..] };
                lock (_gate) _jobs[^1] = job with { Result = result };
            });
            return job;
        }
    }
}
