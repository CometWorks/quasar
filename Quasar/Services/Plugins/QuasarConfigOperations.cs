using System.Globalization;
using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Quasar.Models;
using Quasar.Plugin.Abstractions.Operations;
using Quasar.Services.Auth;
using static Quasar.Services.Plugins.QuasarCoreOperations;

namespace Quasar.Services.Plugins;

/// <summary>Server definitions and the shared config profiles: DS config and world settings, plugins and mods.</summary>
public static class QuasarConfigOperations
{
    public sealed record DefinitionSetRequest(
        string UniqueName = "",
        string Field = "",
        JsonElement Value = default
    );

    public sealed record DefinitionSaveRequest(DedicatedServerDefinition? Definition = null);

    public sealed record ProfileRequest(string? ProfileId = null, string? UniqueName = null);

    public sealed record SettingsGetRequest(
        string? ProfileId = null,
        string? UniqueName = null,
        string? Option = null
    );

    public sealed record SettingSetRequest(
        string? ProfileId = null,
        string? UniqueName = null,
        string Option = "",
        JsonElement Value = default
    );

    public sealed record ProfileSaveRequest(QuasarConfigProfile? Profile = null);

    public sealed record PluginSetRequest(
        string? ProfileId = null,
        string? UniqueName = null,
        string PluginId = "",
        bool Selected = true,
        string? Version = null
    );

    public sealed record ModRequest(
        string? ProfileId = null,
        string? UniqueName = null,
        long WorkshopId = 0,
        int? Index = null
    );

    public sealed record FieldChangeResult(
        string Target,
        string Field,
        JsonElement Previous,
        JsonElement Value,
        IReadOnlyList<string> Servers,
        string AppliesAt = "next start"
    );

    public sealed record ProfileSummary(string Id, string Name, IReadOnlyList<string> Servers);

    public sealed record SettingInfo(
        string Option,
        string Label,
        string Category,
        string Kind,
        object? Value,
        bool? IsSet,
        double? Min,
        double? Max,
        IReadOnlyList<object>? Choices,
        string Help
    );

    public sealed record ProfileSettings(
        string ProfileId,
        IReadOnlyList<string> Servers,
        IReadOnlyList<SettingInfo> Settings
    );

    public sealed record ProfilePlugins(
        string ProfileId,
        IReadOnlyList<QuasarPluginSelection> Plugins
    );

    public sealed record ProfileMods(string ProfileId, IReadOnlyList<QuasarModSelection> Mods);

    public sealed record SaveResult(
        string Target,
        string Message,
        IReadOnlyList<string> Servers,
        string AppliesAt = "next start"
    );

    // Fields an operation may set one at a time. Paths, executables, launch arguments, the runtime, the name,
    // the profile, the world and the goal (start/stop own it; AutoStart follows the goal) stay out.
    private static readonly HashSet<string> SettableDefinitionFields = new(
        [
            nameof(DedicatedServerDefinition.DisplayName),
            nameof(DedicatedServerDefinition.InGameServerName),
            nameof(DedicatedServerDefinition.InGameWorldName),
            nameof(DedicatedServerDefinition.ServerPort),
            nameof(DedicatedServerDefinition.ServerIP),
            nameof(DedicatedServerDefinition.AgentProfilerMode),
            nameof(DedicatedServerDefinition.DsLogFilesToKeep),
            nameof(DedicatedServerDefinition.EnableHealthMonitoring),
            nameof(DedicatedServerDefinition.AutoRestartOnUnhealthy),
            nameof(DedicatedServerDefinition.AgentStartupGraceSeconds),
            nameof(DedicatedServerDefinition.AgentStartupHardLimitSeconds),
            nameof(DedicatedServerDefinition.AgentAttachRetryAttempts),
            nameof(DedicatedServerDefinition.AgentAttachRetryDelaySeconds),
            nameof(DedicatedServerDefinition.AgentHeartbeatTimeoutSeconds),
            nameof(DedicatedServerDefinition.SimulationProgressWindowSeconds),
            nameof(DedicatedServerDefinition.MinimumSimulationProgressScore),
            nameof(DedicatedServerDefinition.WarnAfterUptimeHours),
            nameof(DedicatedServerDefinition.RecycleAfterUptimeHours),
            nameof(DedicatedServerDefinition.RestartOnCrash),
            nameof(DedicatedServerDefinition.RestartDelaySeconds),
            nameof(DedicatedServerDefinition.MaxRestartAttempts),
            nameof(DedicatedServerDefinition.DailyRestartTimeLocal),
            nameof(DedicatedServerDefinition.MaximumUptime),
            nameof(DedicatedServerDefinition.AvoidSimultaneousScheduledRestarts),
            nameof(DedicatedServerDefinition.StartupProcessPriority),
            nameof(DedicatedServerDefinition.ReadyProcessPriority),
            nameof(DedicatedServerDefinition.CpuAffinity),
        ],
        StringComparer.OrdinalIgnoreCase
    );

    private static readonly JsonSerializerOptions DefinitionJson = new(QuasarCoreOperations.Json)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly QuasarOperationMetadata Read = new()
    {
        Effect = QuasarOperationEffect.Read,
    };

    public static void AddQuasarConfigOperations(this IServiceCollection services)
    {
        const string plugin = QuasarCoreOperations.PluginId;
        services.AddQuasarOperation<ServerRequest, Dictionary<string, object?>>(
            plugin,
            "servers.definition.get",
            [QuasarPolicyNames.CanView],
            (caller, input, _) =>
            {
                var server = QuasarCoreOperations.RequireServer(caller.Services, input.UniqueName);
                var result = new Dictionary<string, object?>
                {
                    ["uniqueName"] = server.UniqueName,
                    ["goalState"] = server.GoalState.ToString(),
                    ["configProfileId"] = server.ConfigProfileId,
                    ["worldTemplateId"] = server.WorldTemplateId,
                    ["worldSaveName"] = server.WorldSaveName,
                };
                foreach (var field in SettableDefinitionFields.Order(StringComparer.Ordinal))
                    result[JsonNamingPolicy.CamelCase.ConvertName(field)] = DefinitionValue(
                        server,
                        DefinitionProperty(field)
                    );
                return Task.FromResult(result);
            },
            Read with
            {
                Description =
                    "Returns a server definition's settings without filesystem paths, executables or launch arguments. The fields other than uniqueName, goalState, configProfileId, worldTemplateId and worldSaveName can be set with servers.definition.set.",
            }
        );
        services.AddQuasarOperation<DefinitionSetRequest, FieldChangeResult>(
            plugin,
            "servers.definition.set",
            [QuasarPolicyNames.CanEditServers],
            SetDefinitionFieldAsync,
            new()
            {
                Reversal = QuasarReversal.Undo,
                Undo = new DefinitionFieldUndo(),
                Description =
                    "Sets one field of a server definition, such as the display name, port, restart schedule, health checks, process priorities or CPU affinity. It applies at the server's next start.",
            }
        );
        services.AddQuasarOperation<DefinitionSaveRequest, SaveResult>(
            plugin,
            "servers.definition.save",
            [QuasarPolicyNames.CanEditServers],
            SaveDefinitionAsync,
            new()
            {
                Reversal = QuasarReversal.Undo,
                Undo = new DefinitionUndo(),
                AgentAccess = QuasarAgentAccess.Denied,
                Description =
                    "Saves a whole server definition from the server editor, without renaming it.",
            }
        );
        services.AddQuasarOperation<EmptyRequest, IReadOnlyList<ProfileSummary>>(
            plugin,
            "config.profiles.list",
            [QuasarPolicyNames.CanView],
            (caller, _, _) =>
                Task.FromResult<IReadOnlyList<ProfileSummary>>(
                    caller
                        .Services.GetRequiredService<QuasarConfigProfileCatalog>()
                        .GetProfiles()
                        .Select(profile => new ProfileSummary(
                            profile.ConfigProfileId,
                            profile.Name,
                            UsedBy(caller.Services, profile.ConfigProfileId)
                        ))
                        .ToList()
                ),
            Read with
            {
                Description =
                    "Lists the config profiles with the servers and clusters that use each. A profile holds the DS config, world settings, plugins and mods that its servers share.",
            }
        );
        services.AddQuasarOperation<SettingsGetRequest, ProfileSettings>(
            plugin,
            "config.settings.get",
            [QuasarPolicyNames.CanView],
            (caller, input, _) =>
            {
                var profile = ResolveProfile(caller.Services, input.ProfileId, input.UniqueName);
                var options = string.IsNullOrWhiteSpace(input.Option)
                    ? QuasarConfigMetadata.Options.Where(option => !option.Hidden)
                    : [FindOption(input.Option)];
                return Task.FromResult(
                    new ProfileSettings(
                        profile.ConfigProfileId,
                        UsedBy(caller.Services, profile.ConfigProfileId),
                        options.Select(option => Describe(option, profile)).ToList()
                    )
                );
            },
            Read with
            {
                Description =
                    "Returns the DS config and world settings of a config profile, given by profileId or by a server's uniqueName: one option, or all of them with label, kind, value, limits and choices. Passwords are never returned.",
            }
        );
        services.AddQuasarOperation<SettingSetRequest, FieldChangeResult>(
            plugin,
            "config.settings.set",
            [QuasarPolicyNames.CanEditConfigs],
            SetSettingAsync,
            new()
            {
                Reversal = QuasarReversal.Undo,
                Undo = new ProfilePartUndo(
                    (profile, key) => SettingValue(FindOption(key), profile),
                    (profile, key, value) =>
                    {
                        var option = FindOption(key);
                        var property = QuasarConfigMetadata.GetProperty(option);
                        property.SetValue(
                            Target(option, profile),
                            value.Deserialize(property.PropertyType, QuasarCoreOperations.Json)
                        );
                    }
                ),
                Description =
                    "Sets one DS config or world setting option on a config profile, given by profileId or by a server's uniqueName. Every server using the profile gets it at its next start; the result lists them.",
            }
        );
        services.AddQuasarOperation<ProfileSaveRequest, SaveResult>(
            plugin,
            "config.profile.save",
            [QuasarPolicyNames.CanEditConfigs],
            SaveProfileAsync,
            new()
            {
                Reversal = QuasarReversal.Undo,
                Undo = new ProfileUndo(),
                AgentAccess = QuasarAgentAccess.Denied,
                Description = "Saves a whole config profile from the Configs page.",
            }
        );
        services.AddQuasarOperation<ProfileRequest, ProfilePlugins>(
            plugin,
            "config.plugins.list",
            [QuasarPolicyNames.CanView],
            (caller, input, _) =>
            {
                var profile = ResolveProfile(caller.Services, input.ProfileId, input.UniqueName);
                return Task.FromResult(
                    new ProfilePlugins(profile.ConfigProfileId, profile.Plugins)
                );
            },
            Read with
            {
                Description =
                    "Lists the Magnetar plugins selected in a config profile, given by profileId or by a server's uniqueName.",
            }
        );
        services.AddQuasarOperation<ProfileRequest, ProfileMods>(
            plugin,
            "config.mods.list",
            [QuasarPolicyNames.CanView],
            (caller, input, _) =>
            {
                var profile = ResolveProfile(caller.Services, input.ProfileId, input.UniqueName);
                return Task.FromResult(new ProfileMods(profile.ConfigProfileId, profile.Mods));
            },
            Read with
            {
                Description =
                    "Lists the Workshop mods of a config profile in load order, given by profileId or by a server's uniqueName.",
            }
        );
        services.AddQuasarOperation<PluginSetRequest, FieldChangeResult>(
            plugin,
            "config.plugins.set",
            [QuasarPolicyNames.CanEditConfigs],
            SetPluginAsync,
            new()
            {
                Reversal = QuasarReversal.Undo,
                Undo = new ProfilePartUndo(
                    (profile, key) => FindPlugin(profile, key),
                    (profile, key, value) =>
                    {
                        profile.Plugins.RemoveAll(plugin => SamePlugin(plugin, key));
                        if (
                            value.Deserialize<QuasarPluginSelection>(QuasarCoreOperations.Json) is
                            { } selection
                        )
                            profile.Plugins.Add(selection);
                        SortPlugins(profile);
                    }
                ),
                Description =
                    "Selects or removes one Magnetar plugin in a config profile, optionally pinning a version. The plugin must be listed by a plugin hub or dev folder. Applies at the next start of the servers using the profile.",
            }
        );
        var modsUndo = new ProfilePartUndo(
            (profile, _) => new ModState(profile.Mods, profile.RootSettings.AutodetectDependencies),
            (profile, _, value) =>
            {
                var state = value.Deserialize<ModState>(QuasarCoreOperations.Json)!;
                profile.Mods = state.Mods.ToList();
                profile.RootSettings.AutodetectDependencies = state.AutodetectDependencies;
            }
        );
        services.AddQuasarOperation<ModRequest, FieldChangeResult>(
            plugin,
            "config.mods.add",
            [QuasarPolicyNames.CanEditConfigs],
            AddModAsync,
            new()
            {
                Reversal = QuasarReversal.Undo,
                Undo = modsUndo,
                Description =
                    "Adds a Workshop mod to a config profile, at index in the load order or at the end, with the mods it depends on. Applies at the next start of the servers using the profile.",
            }
        );
        services.AddQuasarOperation<ModRequest, FieldChangeResult>(
            plugin,
            "config.mods.remove",
            [QuasarPolicyNames.CanEditConfigs],
            RemoveModAsync,
            new()
            {
                Reversal = QuasarReversal.Undo,
                Undo = modsUndo,
                Description =
                    "Removes a Workshop mod from a config profile. Applies at the next start of the servers using the profile.",
            }
        );
    }

    private static async Task<FieldChangeResult> SetDefinitionFieldAsync(
        QuasarOperationContext caller,
        DefinitionSetRequest input,
        CancellationToken token
    )
    {
        var catalog = caller.Services.GetRequiredService<DedicatedServerCatalog>();
        var server = QuasarCoreOperations.RequireServer(caller.Services, input.UniqueName);
        if (!SettableDefinitionFields.Contains(input.Field ?? string.Empty))
            throw new QuasarOperationException(
                400,
                $"'{input.Field}' cannot be set by an operation. Settable fields: {string.Join(", ", SettableDefinitionFields.Order(StringComparer.Ordinal))}."
            );
        var property = DefinitionProperty(input.Field!);
        object? value;
        try
        {
            value = input.Value.Deserialize(property.PropertyType, DefinitionJson);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException)
        {
            throw new QuasarOperationException(
                400,
                $"{property.Name} needs a {TypeName(property.PropertyType)} value."
            );
        }
        ValidateDefinitionValue(property.Name, value);
        var previous = ToElement(DefinitionValue(server, property));
        property.SetValue(server, value);
        await catalog.UpsertAsync(server, token);
        var stored = ToElement(DefinitionValue(catalog.GetServer(server.UniqueName)!, property));
        caller.Undo!.Server = server.UniqueName;
        caller.Undo.SetData(new FieldChange(server.UniqueName, property.Name, previous, stored));
        return new(server.UniqueName, property.Name, previous, stored, [server.UniqueName]);
    }

    private static void ValidateDefinitionValue(string field, object? value)
    {
        string? error = (field, value) switch
        {
            (_, null) => "a value",
            (_, int number) when number < 0 => "a value of 0 or more",
            (_, float number) when number < 0 || float.IsNaN(number) => "a value of 0 or more",
            (nameof(DedicatedServerDefinition.ServerPort), int port) when port is < 1 or > 65535 =>
                "a port from 1 to 65535",
            (nameof(DedicatedServerDefinition.ServerIP), string address)
                when !IPAddress.TryParse(address.Trim(), out _) => "an IP address",
            (nameof(DedicatedServerDefinition.AgentProfilerMode), string mode)
                when mode.Trim() is not ("Off" or "SafeContinuous" or "DeepContinuous") =>
                "Off, SafeContinuous or DeepContinuous",
            (nameof(DedicatedServerDefinition.DailyRestartTimeLocal), string times)
                when times
                    .Split(
                        [' ', ',', ';', '\n', '\r', '\t'],
                        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
                    )
                    .Any(time => DedicatedServerSupervisor.TryParseDailyTime(time) is null) =>
                "HH:mm times separated by commas, or nothing",
            (nameof(DedicatedServerDefinition.MaximumUptime), string uptime)
                when uptime.Trim().Length > 0
                    && !DedicatedServerSupervisor.TryParseDurationHoursMinutes(uptime, out _) =>
                "a duration as hours:minutes, or nothing",
            (nameof(DedicatedServerDefinition.CpuAffinity), string affinity)
                when affinity.Trim().Length > 0
                    && !CpuAffinitySpec.TryParse(
                        affinity,
                        Environment.ProcessorCount,
                        out _,
                        out _
                    ) => "a CPU set such as 0-7 with at least 2 cores, or nothing",
            _ => null,
        };
        if (error is not null)
            throw new QuasarOperationException(400, $"{field} needs {error}.");
    }

    private static async Task<SaveResult> SaveDefinitionAsync(
        QuasarOperationContext caller,
        DefinitionSaveRequest input,
        CancellationToken token
    )
    {
        var definition =
            input.Definition
            ?? throw new QuasarOperationException(400, "A definition is required.");
        var catalog = caller.Services.GetRequiredService<DedicatedServerCatalog>();
        var current = QuasarCoreOperations.RequireServer(caller.Services, definition.UniqueName);
        definition.OriginalUniqueName = current.UniqueName;
        caller.Undo!.Server = current.UniqueName;
        await catalog.UpsertAsync(definition, token);
        await AddBlobAsync(caller.Undo, "previous.json", current, token);
        await AddBlobAsync(
            caller.Undo,
            "after.json",
            catalog.GetServer(current.UniqueName)!,
            token
        );
        caller.Undo.SetData(new { uniqueName = current.UniqueName });
        return new(current.ConfigProfileId, "Server saved.", [current.UniqueName]);
    }

    private static async Task<FieldChangeResult> SetSettingAsync(
        QuasarOperationContext caller,
        SettingSetRequest input,
        CancellationToken token
    )
    {
        var option = FindOption(input.Option);
        if (option.Kind == QuasarConfigOptionKind.Password)
            throw new QuasarOperationException(
                400,
                $"{option.Label} is a password and can only be set on the Configs page."
            );
        var profile = ResolveProfile(caller.Services, input.ProfileId, input.UniqueName);
        var property = QuasarConfigMetadata.GetProperty(option);
        var value = ParseSetting(option, property.PropertyType, input.Value);
        return await ChangeProfileAsync(
            caller,
            profile,
            input.UniqueName,
            option.PropertyName,
            current => SettingValue(option, current),
            current => property.SetValue(Target(option, current), value),
            token
        );
    }

    private static async Task<FieldChangeResult> SetPluginAsync(
        QuasarOperationContext caller,
        PluginSetRequest input,
        CancellationToken token
    )
    {
        var pluginId = input.PluginId?.Trim() ?? string.Empty;
        var profile = ResolveProfile(caller.Services, input.ProfileId, input.UniqueName);
        var existing = FindPlugin(profile, pluginId);
        QuasarPluginCatalogEntry? entry = null;
        if (input.Selected)
        {
            if (!QuasarPluginCatalogService.IsManualSelectionAllowed(pluginId))
                throw new QuasarOperationException(
                    409,
                    "This plugin is managed automatically by Magnetar and cannot be selected manually."
                );
            entry =
                caller
                    .Services.GetRequiredService<QuasarPluginCatalogService>()
                    .GetEntries()
                    .FirstOrDefault(candidate =>
                        candidate.PluginId.Equals(pluginId, StringComparison.OrdinalIgnoreCase)
                    )
                ?? throw new QuasarOperationException(
                    404,
                    $"No plugin hub or dev folder lists '{pluginId}'."
                );
            if (entry.ConflictingHubs.Count > 1)
                throw new QuasarOperationException(
                    409,
                    $"More than one plugin hub lists this plugin ({string.Join(", ", entry.ConflictingHubs)})."
                );
            if (entry.Hidden)
                throw new QuasarOperationException(
                    409,
                    entry.IsLocalDevFolder
                        ? "Enable the dev folder source before selecting it in a config profile."
                        : $"This plugin is hidden in {entry.HubName} and cannot be selected manually."
                );
        }
        else if (existing is null)
            throw new QuasarOperationException(
                404,
                $"'{pluginId}' is not selected in this profile."
            );
        return await ChangeProfileAsync(
            caller,
            profile,
            input.UniqueName,
            entry?.PluginId ?? existing!.PluginId,
            current => FindPlugin(current, pluginId),
            current =>
            {
                current.Plugins.RemoveAll(plugin => SamePlugin(plugin, pluginId));
                if (entry is null)
                    return;
                current.Plugins.Add(
                    new QuasarPluginSelection
                    {
                        PluginId = entry.PluginId,
                        DisplayName = string.IsNullOrWhiteSpace(entry.FriendlyName)
                            ? entry.PluginId
                            : entry.FriendlyName,
                        SelectedVersion =
                            input.Version?.Trim() ?? existing?.SelectedVersion ?? string.Empty,
                    }
                );
                SortPlugins(current);
            },
            token
        );
    }

    private static async Task<FieldChangeResult> AddModAsync(
        QuasarOperationContext caller,
        ModRequest input,
        CancellationToken token
    )
    {
        if (input.WorkshopId <= 0)
            throw new QuasarOperationException(400, "A Workshop ID is required.");
        var profile = ResolveProfile(caller.Services, input.ProfileId, input.UniqueName);
        if (profile.Mods.Any(mod => mod.WorkshopId == input.WorkshopId))
            throw new QuasarOperationException(409, "The mod is already in this profile.");
        var resolver = caller.Services.GetService<QuasarWorkshopModResolver>();
        var name = resolver is null
            ? null
            : (await resolver.GetDisplayNamesAsync([input.WorkshopId], token)).GetValueOrDefault(
                input.WorkshopId
            );
        var mods = profile.Mods.ToList();
        mods.Insert(
            Math.Clamp(input.Index ?? mods.Count, 0, mods.Count),
            new QuasarModSelection
            {
                WorkshopId = input.WorkshopId,
                DisplayName = string.IsNullOrWhiteSpace(name) ? $"Mod {input.WorkshopId}" : name,
            }
        );
        // The Configs page resolves dependencies the same way when it saves.
        var resolved = resolver is null
            ? null
            : await resolver.ResolveDependenciesAsync(mods, cancellationToken: token);
        return await ChangeProfileAsync(
            caller,
            profile,
            input.UniqueName,
            "mods",
            current => new ModState(current.Mods, current.RootSettings.AutodetectDependencies),
            current =>
            {
                current.Mods = resolved?.Mods.ToList() ?? mods;
                if (resolved is not null)
                    current.RootSettings.AutodetectDependencies = resolved.Warnings.Count > 0;
            },
            token
        );
    }

    private static async Task<FieldChangeResult> RemoveModAsync(
        QuasarOperationContext caller,
        ModRequest input,
        CancellationToken token
    )
    {
        var profile = ResolveProfile(caller.Services, input.ProfileId, input.UniqueName);
        if (profile.Mods.All(mod => mod.WorkshopId != input.WorkshopId))
            throw new QuasarOperationException(404, "The mod is not in this profile.");
        return await ChangeProfileAsync(
            caller,
            profile,
            input.UniqueName,
            "mods",
            current => new ModState(current.Mods, current.RootSettings.AutodetectDependencies),
            current => current.Mods.RemoveAll(mod => mod.WorkshopId == input.WorkshopId),
            token
        );
    }

    // One field-sized change of a profile, recorded for undo as the part's value before and after.
    private static async Task<FieldChangeResult> ChangeProfileAsync(
        QuasarOperationContext caller,
        QuasarConfigProfile profile,
        string? uniqueName,
        string key,
        Func<QuasarConfigProfile, object?> read,
        Action<QuasarConfigProfile> change,
        CancellationToken token
    )
    {
        var catalog = caller.Services.GetRequiredService<QuasarConfigProfileCatalog>();
        var previous = ToElement(read(profile));
        change(profile);
        await catalog.UpsertAsync(profile, token);
        var stored = ToElement(read(catalog.GetProfile(profile.ConfigProfileId)!));
        var servers = UsedBy(caller.Services, profile.ConfigProfileId);
        caller.Undo!.Server = string.IsNullOrWhiteSpace(uniqueName)
            ? servers.Count == 1
                ? servers[0]
                : null
            : uniqueName.Trim();
        caller.Undo.SetData(new FieldChange(profile.ConfigProfileId, key, previous, stored));
        return new(profile.ConfigProfileId, key, previous, stored, servers);
    }

    private static async Task<SaveResult> SaveProfileAsync(
        QuasarOperationContext caller,
        ProfileSaveRequest input,
        CancellationToken token
    )
    {
        var profile =
            input.Profile ?? throw new QuasarOperationException(400, "A profile is required.");
        if (string.IsNullOrWhiteSpace(profile.Name))
            throw new QuasarOperationException(400, "Profile name required.");
        var catalog = caller.Services.GetRequiredService<QuasarConfigProfileCatalog>();
        var current =
            catalog.GetProfile(profile.ConfigProfileId)
            ?? throw new QuasarOperationException(404, "Config profile not found.");
        await catalog.UpsertAsync(profile, token);
        // Blobs, not inline data: a profile holds the server password.
        await AddBlobAsync(caller.Undo!, "previous.json", current, token);
        await AddBlobAsync(
            caller.Undo!,
            "after.json",
            catalog.GetProfile(current.ConfigProfileId)!,
            token
        );
        caller.Undo!.SetData(new { profileId = current.ConfigProfileId });
        var servers = UsedBy(caller.Services, current.ConfigProfileId);
        caller.Undo.Server = servers.Count == 1 ? servers[0] : null;
        return new(current.ConfigProfileId, "Config profile saved.", servers);
    }

    internal static QuasarConfigProfile ResolveProfile(
        IServiceProvider services,
        string? profileId,
        string? uniqueName
    )
    {
        var id = profileId?.Trim();
        if (string.IsNullOrEmpty(id))
        {
            var name = uniqueName?.Trim();
            if (string.IsNullOrEmpty(name))
                throw new QuasarOperationException(
                    400,
                    "Give a profileId or a server's uniqueName."
                );
            id =
                services
                    .GetRequiredService<DedicatedServerCatalog>()
                    .GetServer(name)
                    ?.ConfigProfileId
                ?? services.GetService<ClusterCatalog>()?.GetCluster(name)?.ConfigProfileId
                ?? throw new QuasarOperationException(404, $"Server '{name}' not found.");
            if (id.Length == 0)
                throw new QuasarOperationException(409, $"Server '{name}' has no config profile.");
        }
        return services.GetRequiredService<QuasarConfigProfileCatalog>().GetProfile(id)
            ?? throw new QuasarOperationException(404, $"Config profile '{id}' not found.");
    }

    private static IReadOnlyList<string> UsedBy(IServiceProvider services, string profileId) =>
        services
            .GetRequiredService<DedicatedServerCatalog>()
            .GetServers()
            .Where(server =>
                server.ConfigProfileId.Equals(profileId, StringComparison.OrdinalIgnoreCase)
            )
            .Select(server => server.UniqueName)
            .Concat(
                (services.GetService<ClusterCatalog>()?.GetClusters() ?? [])
                    .Where(cluster =>
                        cluster.ConfigProfileId.Equals(
                            profileId,
                            StringComparison.OrdinalIgnoreCase
                        )
                    )
                    .Select(cluster => cluster.UniqueName)
            )
            .ToList();

    internal static QuasarConfigOptionDefinition FindOption(string? name) =>
        QuasarConfigMetadata.Options.FirstOrDefault(option =>
            !option.Hidden
            && option.PropertyName.Equals(name?.Trim(), StringComparison.OrdinalIgnoreCase)
        ) ?? throw new QuasarOperationException(404, $"Unknown config option '{name}'.");

    private static object Target(
        QuasarConfigOptionDefinition option,
        QuasarConfigProfile profile
    ) =>
        option.Scope == QuasarConfigOptionScope.Root
            ? profile.RootSettings
            : profile.SessionSettings;

    private static object? SettingValue(
        QuasarConfigOptionDefinition option,
        QuasarConfigProfile profile
    ) => QuasarConfigMetadata.GetProperty(option).GetValue(Target(option, profile));

    private static SettingInfo Describe(
        QuasarConfigOptionDefinition option,
        QuasarConfigProfile profile
    )
    {
        var value = SettingValue(option, profile);
        var password = option.Kind == QuasarConfigOptionKind.Password;
        IReadOnlyList<object>? choices = option.Kind switch
        {
            QuasarConfigOptionKind.SelectInteger =>
            [
                .. option.SelectOptions.Select(choice => new { choice.Value, choice.Label }),
            ],
            QuasarConfigOptionKind.SelectText =>
            [
                .. option.SelectTextOptions.Select(choice => new { choice.Value, choice.Label }),
            ],
            _ => null,
        };
        return new(
            option.PropertyName,
            option.Label,
            option.CategoryKey,
            option.Kind.ToString(),
            password ? null : value,
            password ? !string.IsNullOrEmpty(value as string) : null,
            option.Min,
            option.Max,
            choices,
            option.HelperText
        );
    }

    internal static object ParseSetting(
        QuasarConfigOptionDefinition option,
        Type type,
        JsonElement value
    )
    {
        var text = value.ValueKind == JsonValueKind.String ? value.GetString()!.Trim() : null;
        object? parsed = option.Kind switch
        {
            QuasarConfigOptionKind.Boolean
                when value.ValueKind is JsonValueKind.True or JsonValueKind.False =>
                value.GetBoolean(),
            QuasarConfigOptionKind.Integer
                when value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) =>
                number,
            QuasarConfigOptionKind.Decimal when value.ValueKind == JsonValueKind.Number =>
                value.GetDouble(),
            QuasarConfigOptionKind.Text or QuasarConfigOptionKind.LongText when text is not null =>
                value.GetString(),
            QuasarConfigOptionKind.SelectInteger
                when value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var choice) =>
                option.SelectOptions.FirstOrDefault(item => item.Value == choice)?.Value,
            QuasarConfigOptionKind.SelectInteger when text is not null => option
                .SelectOptions.FirstOrDefault(item =>
                    item.Label.Equals(text, StringComparison.OrdinalIgnoreCase)
                    || item.XmlName.Equals(text, StringComparison.OrdinalIgnoreCase)
                )
                ?.Value,
            QuasarConfigOptionKind.SelectText when text is not null => option
                .SelectTextOptions.FirstOrDefault(item =>
                    item.Value.Equals(text, StringComparison.OrdinalIgnoreCase)
                    || item.Label.Equals(text, StringComparison.OrdinalIgnoreCase)
                )
                ?.Value,
            QuasarConfigOptionKind.KeyValueText when value.ValueKind == JsonValueKind.Object =>
                value,
            _ => null,
        };
        if (parsed is null)
            throw new QuasarOperationException(
                400,
                $"{option.PropertyName} needs {Expected(option)}."
            );
        if (parsed is long or double)
        {
            var number = Convert.ToDouble(parsed, CultureInfo.InvariantCulture);
            if (number < option.Min || number > option.Max)
                throw new QuasarOperationException(
                    400,
                    $"{option.PropertyName} must be between {option.Min?.ToString(CultureInfo.InvariantCulture) ?? "any"} and {option.Max?.ToString(CultureInfo.InvariantCulture) ?? "any"}."
                );
        }
        // Through JSON, so the value takes the property's type: enums, floats, dictionaries.
        try
        {
            return JsonSerializer
                .SerializeToElement(parsed, QuasarCoreOperations.Json)
                .Deserialize(type, QuasarCoreOperations.Json)!;
        }
        catch (JsonException)
        {
            throw new QuasarOperationException(
                400,
                $"{option.PropertyName} needs {Expected(option)}."
            );
        }
    }

    private static string Expected(QuasarConfigOptionDefinition option) =>
        option.Kind switch
        {
            QuasarConfigOptionKind.Boolean => "true or false",
            QuasarConfigOptionKind.Integer => "a whole number",
            QuasarConfigOptionKind.Decimal => "a number",
            QuasarConfigOptionKind.SelectInteger or QuasarConfigOptionKind.SelectText => "one of "
                + string.Join(
                    ", ",
                    option.Kind == QuasarConfigOptionKind.SelectInteger
                        ? option.SelectOptions.Select(choice => $"{choice.Value} ({choice.Label})")
                        : option.SelectTextOptions.Select(choice => choice.Value)
                ),
            QuasarConfigOptionKind.KeyValueText => "an object of names to whole numbers",
            _ => "a text",
        };

    private static PropertyInfo DefinitionProperty(string field) =>
        typeof(DedicatedServerDefinition).GetProperty(
            field,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase
        )!;

    private static object? DefinitionValue(
        DedicatedServerDefinition server,
        PropertyInfo property
    ) => property.GetValue(server) is Enum value ? value.ToString() : property.GetValue(server);

    private static string TypeName(Type type) =>
        type == typeof(bool) ? "true or false"
        : type == typeof(int) ? "whole number"
        : type == typeof(float) ? "number"
        : type.IsEnum ? string.Join(", ", Enum.GetNames(type)) + " (one of)"
        : "text";

    private static QuasarPluginSelection? FindPlugin(
        QuasarConfigProfile profile,
        string pluginId
    ) => profile.Plugins.FirstOrDefault(plugin => SamePlugin(plugin, pluginId));

    private static bool SamePlugin(QuasarPluginSelection plugin, string pluginId) =>
        plugin.PluginId.Equals(pluginId.Trim(), StringComparison.OrdinalIgnoreCase);

    private static void SortPlugins(QuasarConfigProfile profile) =>
        profile.Plugins = profile
            .Plugins.OrderBy(plugin => plugin.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static async Task AddBlobAsync(
        QuasarUndoRecorder undo,
        string name,
        object value,
        CancellationToken token
    )
    {
        using var content = new MemoryStream(
            JsonSerializer.SerializeToUtf8Bytes(value, QuasarCoreOperations.Json)
        );
        await undo.AddBlobAsync(name, content, token);
    }

    private static T ReadBlob<T>(QuasarUndoContext context, string name) =>
        JsonSerializer.Deserialize<T>(
            File.ReadAllBytes(Path.Combine(context.BlobDirectory, name)),
            QuasarCoreOperations.Json
        )!;

    private static JsonElement ToElement(object? value) =>
        JsonSerializer.SerializeToElement(value, QuasarCoreOperations.Json);

    private sealed record FieldChange(
        string Target,
        string Key,
        JsonElement Before,
        JsonElement After
    );

    private sealed record ModState(
        IReadOnlyList<QuasarModSelection> Mods,
        bool AutodetectDependencies
    );

    private sealed class DefinitionFieldUndo : IQuasarUndoHandler
    {
        public Task<string?> CheckAsync(QuasarUndoContext context, CancellationToken token)
        {
            var change = context.Data.Deserialize<FieldChange>(QuasarCoreOperations.Json)!;
            var server = context
                .Services.GetRequiredService<DedicatedServerCatalog>()
                .GetServer(change.Target);
            if (server is null)
                return Task.FromResult<string?>($"Server '{change.Target}' no longer exists.");
            var current = DefinitionValue(server, DefinitionProperty(change.Key));
            return Task.FromResult(
                QuasarCoreOperations.JsonEquals(change.After, current)
                    ? null
                    : $"{change.Key} is {JsonSerializer.Serialize(current, QuasarCoreOperations.Json)} now, not {change.After.GetRawText()}."
            );
        }

        public async Task ReverseAsync(QuasarUndoContext context, CancellationToken token)
        {
            var change = context.Data.Deserialize<FieldChange>(QuasarCoreOperations.Json)!;
            var catalog = context.Services.GetRequiredService<DedicatedServerCatalog>();
            var server =
                catalog.GetServer(change.Target)
                ?? throw new InvalidOperationException(
                    $"Server '{change.Target}' no longer exists."
                );
            var property = DefinitionProperty(change.Key);
            property.SetValue(
                server,
                change.Before.Deserialize(property.PropertyType, DefinitionJson)
            );
            await catalog.UpsertAsync(server, token);
        }
    }

    private sealed class ProfilePartUndo(
        Func<QuasarConfigProfile, string, object?> read,
        Action<QuasarConfigProfile, string, JsonElement> write
    ) : IQuasarUndoHandler
    {
        public Task<string?> CheckAsync(QuasarUndoContext context, CancellationToken token)
        {
            var change = context.Data.Deserialize<FieldChange>(QuasarCoreOperations.Json)!;
            var profile = context
                .Services.GetRequiredService<QuasarConfigProfileCatalog>()
                .GetProfile(change.Target);
            return Task.FromResult(
                profile is null ? $"Config profile '{change.Target}' no longer exists."
                : QuasarCoreOperations.JsonEquals(change.After, read(profile, change.Key)) ? null
                : $"{change.Key} of profile '{change.Target}' was changed again since."
            );
        }

        public async Task ReverseAsync(QuasarUndoContext context, CancellationToken token)
        {
            var change = context.Data.Deserialize<FieldChange>(QuasarCoreOperations.Json)!;
            var catalog = context.Services.GetRequiredService<QuasarConfigProfileCatalog>();
            var profile =
                catalog.GetProfile(change.Target)
                ?? throw new InvalidOperationException(
                    $"Config profile '{change.Target}' no longer exists."
                );
            write(profile, change.Key, change.Before);
            await catalog.UpsertAsync(profile, token);
        }
    }

    // Whole-object saves compare and restore the stored JSON, apart from the save time and the goal.
    private sealed class DefinitionUndo : IQuasarUndoHandler
    {
        public Task<string?> CheckAsync(QuasarUndoContext context, CancellationToken token)
        {
            var after = ReadBlob<DedicatedServerDefinition>(context, "after.json");
            var current = context
                .Services.GetRequiredService<DedicatedServerCatalog>()
                .GetServer(after.UniqueName);
            return Task.FromResult(
                current is null ? $"Server '{after.UniqueName}' no longer exists."
                : Comparable(current).Equals(Comparable(after)) ? null
                : $"Server '{after.UniqueName}' was changed again since."
            );
        }

        public async Task ReverseAsync(QuasarUndoContext context, CancellationToken token)
        {
            var previous = ReadBlob<DedicatedServerDefinition>(context, "previous.json");
            var catalog = context.Services.GetRequiredService<DedicatedServerCatalog>();
            var current =
                catalog.GetServer(previous.UniqueName)
                ?? throw new InvalidOperationException(
                    $"Server '{previous.UniqueName}' no longer exists."
                );
            previous.OriginalUniqueName = previous.UniqueName;
            previous.GoalState = current.GoalState;
            previous.AutoStart = current.AutoStart;
            await catalog.UpsertAsync(previous, token);
        }

        private static string Comparable(DedicatedServerDefinition definition)
        {
            var copy = definition.Clone();
            copy.UpdatedAtUtc = default;
            copy.GoalState = DedicatedServerGoalState.Off;
            copy.AutoStart = false;
            return JsonSerializer.Serialize(copy, QuasarCoreOperations.Json);
        }
    }

    private sealed class ProfileUndo : IQuasarUndoHandler
    {
        public Task<string?> CheckAsync(QuasarUndoContext context, CancellationToken token)
        {
            var after = ReadBlob<QuasarConfigProfile>(context, "after.json");
            var current = context
                .Services.GetRequiredService<QuasarConfigProfileCatalog>()
                .GetProfile(after.ConfigProfileId);
            return Task.FromResult(
                current is null ? $"Config profile '{after.ConfigProfileId}' no longer exists."
                : Comparable(current).Equals(Comparable(after)) ? null
                : $"Config profile '{after.Name}' was changed again since."
            );
        }

        public async Task ReverseAsync(QuasarUndoContext context, CancellationToken token) =>
            await context
                .Services.GetRequiredService<QuasarConfigProfileCatalog>()
                .UpsertAsync(ReadBlob<QuasarConfigProfile>(context, "previous.json"), token);

        private static string Comparable(QuasarConfigProfile profile)
        {
            var node = JsonSerializer
                .SerializeToNode(profile, QuasarCoreOperations.Json)!
                .AsObject();
            node.Remove("updatedAtUtc");
            return node.ToJsonString();
        }
    }
}
