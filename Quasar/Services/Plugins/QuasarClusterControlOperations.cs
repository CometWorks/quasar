using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using Quasar.Models;
using Quasar.Plugin.Abstractions.Operations;
using Quasar.Services.Auth;
using Quasar.Services.PluginSdk;
using static Quasar.Services.Plugins.QuasarClusterOperations;
using Admin = CometWorks.ClusterGateway.AdminContract.V1;

namespace Quasar.Services.Plugins;

/// <summary>Cluster lifecycle, player moderation and log reads, through the same services and checks as the cluster pages.</summary>
public static class QuasarClusterControlOperations
{
    /// <param name="OperationId">The cluster operation to follow with cluster.operation; null when none was recorded.</param>
    /// <param name="State">Running, Succeeded or Failed.</param>
    public sealed record ClusterActionResult(
        string UniqueName,
        string? OperationId,
        string State,
        string Message
    );

    public sealed record ClusterOperationRequest(string UniqueName = "", string OperationId = "");

    public sealed record ClusterOperationStatus(
        string OperationId,
        string Kind,
        string State,
        string Actor,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt,
        string? Error,
        JsonElement? Result
    );

    /// <param name="SteamId">The player's SteamID64, as a string.</param>
    public sealed record ClusterPlayerRequest(string UniqueName = "", string SteamId = "");

    /// <param name="Level">None, Scripter, Moderator, SpaceMaster or Admin.</param>
    public sealed record ClusterPromotionRequest(
        string UniqueName = "",
        string SteamId = "",
        string Level = ""
    );

    /// <param name="Node">One node, by the name cluster.status reports; all nodes when empty.</param>
    /// <param name="Lines">1 to 2000.</param>
    /// <param name="From"><c>tail</c> or <c>head</c>.</param>
    /// <param name="Filter">Keeps entries containing this text, ignoring case.</param>
    /// <param name="MaxBytes">1 to 65536.</param>
    public sealed record ClusterLogRequest(
        string UniqueName = "",
        string? Node = null,
        int Lines = 200,
        string From = "tail",
        string? Filter = null,
        int MaxBytes = 16384
    );

    /// <param name="Lines">1 to 2000.</param>
    /// <param name="From"><c>tail</c> or <c>head</c>.</param>
    /// <param name="Filter">Keeps events containing this text, ignoring case.</param>
    /// <param name="MaxBytes">1 to 65536.</param>
    public sealed record ClusterEventsRequest(
        string UniqueName = "",
        int Lines = 200,
        string From = "tail",
        string? Filter = null,
        int MaxBytes = 16384
    );

    private const string FollowHint = "Follow it with cluster.operation.";
    private const int EventPageSize = 500;
    private const int EventPages = 20;
    private static readonly TimeSpan LifecycleDeadline = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan LongDeadline = TimeSpan.FromSeconds(300);
    private static readonly QuasarOperationMetadata Read = new()
    {
        Effect = QuasarOperationEffect.Read,
    };
    private static readonly IQuasarUndoHandler GoalUndo = new GoalStateUndo();

    public static void AddQuasarClusterControlOperations(this IServiceCollection services)
    {
        const string plugin = QuasarCoreOperations.PluginId;
        string[] manage = [QuasarPolicyNames.ClusterManage];
        services.AddQuasarOperation<ClusterRequest, ClusterActionResult>(
            plugin,
            "cluster.start",
            manage,
            StartAsync,
            new()
            {
                Reversal = QuasarReversal.Undo,
                Undo = GoalUndo,
                Deadline = LifecycleDeadline,
                Description =
                    "Starts a stopped cluster by setting its goal On, once its services have stopped cleanly. Returns when the goal is recorded; the Gateway and nodes come up after that, which cluster.status shows.",
            }
        );
        services.AddQuasarOperation<ClusterRequest, ClusterActionResult>(
            plugin,
            "cluster.stop",
            manage,
            StopAsync,
            new()
            {
                Reversal = QuasarReversal.Undo,
                Undo = GoalUndo,
                Deadline = LifecycleDeadline,
                Description =
                    "Shuts a cluster down: players disconnect while it saves and drains, and it stays off. Returns when the stop is requested; cluster.status shows the progress.",
            }
        );
        services.AddQuasarOperation<ClusterRequest, ClusterActionResult>(
            plugin,
            "cluster.restart",
            manage,
            RestartAsync,
            new()
            {
                Reversal = QuasarReversal.NothingToReverse,
                Deadline = LifecycleDeadline,
                Description =
                    "Restarts a running cluster: a normal stop with a save and drain, then a start once it has stopped cleanly, within ten minutes. Returns when the stop is requested; cluster.status shows the progress.",
            }
        );
        services.AddQuasarOperation<ClusterRequest, ClusterActionResult>(
            plugin,
            "cluster.save",
            manage,
            SaveAsync,
            new()
            {
                Reversal = QuasarReversal.NothingToReverse,
                Description =
                    "Asks a running cluster's Gateway to save the world on every node. Returns the cluster operation; cluster.operation shows when the save is done.",
            }
        );
        services.AddQuasarOperation<ClusterRequest, ClusterActionResult>(
            plugin,
            "cluster.gateway.restart",
            manage,
            GatewayRestartAsync,
            new()
            {
                Reversal = QuasarReversal.NothingToReverse,
                Description =
                    "Restarts a running cluster's Gateway process: clients reconnect while the nodes keep simulating. Returns the cluster operation to follow with cluster.operation.",
            }
        );
        services.AddQuasarOperation<ClusterRequest, ClusterActionResult>(
            plugin,
            "cluster.services.recover",
            manage,
            (caller, input, token) =>
                RecoverAsync(
                    caller,
                    input,
                    "cluster.gateway.recover",
                    (deployments, cluster, key, actor) =>
                        deployments.RecoverGatewayAsync(cluster, key, actor, default),
                    token
                ),
            new()
            {
                Deadline = LongDeadline,
                Description =
                    "Recovers a stopped cluster's missing services: restarts its recorded Gateway, which resumes any pending shutdown. It keeps the world state and never marks a dirty shutdown clean.",
            }
        );
        services.AddQuasarOperation<ClusterRequest, ClusterActionResult>(
            plugin,
            "cluster.recover",
            manage,
            (caller, input, token) =>
                RecoverAsync(
                    caller,
                    input,
                    "cluster.recover",
                    (deployments, cluster, key, actor) =>
                        deployments.RecoverClusterAsync(
                            cluster,
                            Guid.NewGuid(),
                            key,
                            actor,
                            default
                        ),
                    token
                ),
            new()
            {
                Deadline = LongDeadline,
                Description =
                    "Recovers a stopped cluster after an unclean shutdown, with every node stopped: starts a new recovery generation from the retained saves and plugin data. Unsaved game changes may be lost.",
            }
        );
        services.AddQuasarOperation<ClusterOperationRequest, ClusterOperationStatus>(
            plugin,
            "cluster.operation",
            [QuasarPolicyNames.ClusterQuery],
            (caller, input, _) => Task.FromResult(Operation(caller, input)),
            Read with
            {
                Description =
                    "Returns one cluster operation by id, as the cluster page tracks it: kind, Running, Succeeded or Failed, the error and the Gateway's result.",
            }
        );

        var banUndo = new PlayerUndo(
            async (services, cluster, steamId, token) =>
                JsonSerializer.SerializeToElement(
                    await IsBannedAsync(services, cluster, steamId, token)
                ),
            before => (before.GetBoolean() ? "ban" : "unban", null)
        );
        var levelUndo = new PlayerUndo(
            async (services, cluster, steamId, token) =>
                JsonSerializer.SerializeToElement(
                    (await PromotionAsync(services, cluster, steamId, token)).Level.ToString()
                ),
            before =>
                (
                    "set-player-promotion",
                    Enum.Parse<Admin.PlayerPromotionLevel>(before.GetString()!)
                )
        );
        AddPlayerAction(
            services,
            "cluster.players.kick",
            "kick",
            null,
            "Kicks an online player from a cluster. The player can rejoin after the kick cooldown."
        );
        AddPlayerAction(
            services,
            "cluster.players.ban",
            "ban",
            banUndo,
            "Bans a player from a cluster at its Gateway, which also keeps them out of every node."
        );
        AddPlayerAction(
            services,
            "cluster.players.unban",
            "unban",
            banUndo,
            "Lifts a player's ban on a cluster."
        );
        AddPlayerAction(
            services,
            "cluster.players.kick-cooldown.clear",
            "clear-kick-cooldown",
            null,
            "Clears a kicked player's rejoin cooldown on a cluster."
        );
        services.AddQuasarOperation<ClusterPromotionRequest, ClusterActionResult>(
            plugin,
            "cluster.players.promotion.set",
            manage,
            (caller, input, token) =>
            {
                if (
                    !Enum.TryParse<Admin.PlayerPromotionLevel>(
                        input.Level?.Trim(),
                        ignoreCase: true,
                        out var level
                    )
                    || !Enum.IsDefined(level)
                    || int.TryParse(input.Level, out _)
                )
                    throw new QuasarOperationException(
                        400,
                        $"Level must be one of {string.Join(", ", Enum.GetNames<Admin.PlayerPromotionLevel>())}."
                    );
                return PlayerActionAsync(
                    caller,
                    input.UniqueName,
                    input.SteamId,
                    "set-player-promotion",
                    level,
                    token
                );
            },
            new()
            {
                Reversal = QuasarReversal.Undo,
                Undo = levelUndo,
                Description =
                    "Sets a player's role on a cluster, saved by its World Authority: None, Scripter, Moderator, SpaceMaster or Admin.",
            }
        );

        services.AddQuasarOperation<ClusterLogRequest, QuasarServerOperations.LogTailResponse>(
            plugin,
            "cluster.logs.tail",
            [QuasarPolicyNames.ClusterQuery],
            NodeLogsAsync,
            Read with
            {
                Description =
                    "Reads the recent plugin log entries of a cluster's nodes, or of one node, as the cluster console shows them: the last (tail) or first (head) entries, optionally only those containing a filter text. Defaults: 200 entries, at most 16 KiB.",
            }
        );
        services.AddQuasarOperation<ClusterEventsRequest, QuasarServerOperations.LogTailResponse>(
            plugin,
            "cluster.events.tail",
            [QuasarPolicyNames.ClusterQuery],
            EventsAsync,
            Read with
            {
                Description =
                    "Reads a cluster's Cluster Registry events, the Gateway's event and audit log of nodes, players and admin actions, as the cluster console shows them: the last (tail) or first (head) events, optionally only those containing a filter text. Defaults: 200 events, at most 16 KiB.",
            }
        );
    }

    private static void AddPlayerAction(
        IServiceCollection services,
        string operationId,
        string action,
        IQuasarUndoHandler? undo,
        string description
    ) =>
        services.AddQuasarOperation<ClusterPlayerRequest, ClusterActionResult>(
            QuasarCoreOperations.PluginId,
            operationId,
            [QuasarPolicyNames.ClusterManage],
            (caller, input, token) =>
                PlayerActionAsync(caller, input.UniqueName, input.SteamId, action, null, token),
            new()
            {
                Reversal = undo is null ? QuasarReversal.NothingToReverse : QuasarReversal.Undo,
                Undo = undo,
                Description = description,
            }
        );

    private static ClusterDefinition RequireCluster(
        QuasarOperationContext caller,
        string uniqueName
    )
    {
        var name = uniqueName?.Trim() ?? string.Empty;
        var cluster = caller.Services.GetRequiredService<ClusterCatalog>().GetCluster(name);
        if (cluster is null || !caller.User.CanQueryCluster(cluster.UniqueName))
            throw new QuasarOperationException(404, $"Cluster '{name}' not found.");
        return cluster;
    }

    private static ClusterDefinition Track(QuasarOperationContext caller, string uniqueName)
    {
        var cluster = RequireCluster(caller, uniqueName);
        caller.Undo!.Server = cluster.UniqueName;
        return cluster;
    }

    private static string Actor(ClaimsPrincipal user) =>
        user.Identity?.Name ?? user.FindFirstValue(ClaimTypes.NameIdentifier) ?? "operation";

    private static bool IsManaged(ClusterDefinition cluster) =>
        cluster.Gateway is not null && !string.IsNullOrWhiteSpace(cluster.HostCommandUrl);

    private static async Task<ClusterActionResult> StartAsync(
        QuasarOperationContext caller,
        ClusterRequest input,
        CancellationToken token
    )
    {
        var cluster = Track(caller, input.UniqueName);
        if (cluster.GoalState == DedicatedServerGoalState.On)
            throw new QuasarOperationException(
                409,
                $"Cluster '{cluster.UniqueName}' is already on."
            );
        var result = await SetGoalAsync(caller, cluster, DedicatedServerGoalState.On, token);
        caller.Undo!.SetData(new GoalChange(cluster.UniqueName, DedicatedServerGoalState.On));
        return result with { Message = "Start requested; follow it with cluster.status." };
    }

    // What the Stop button does: a managed cluster's goal goes Off, an observed one gets a Gateway shutdown.
    private static async Task<ClusterActionResult> StopAsync(
        QuasarOperationContext caller,
        ClusterRequest input,
        CancellationToken token
    )
    {
        var cluster = Track(caller, input.UniqueName);
        if (cluster.GoalState == DedicatedServerGoalState.Off)
            throw new QuasarOperationException(
                409,
                $"Cluster '{cluster.UniqueName}' is already off."
            );
        if (!IsManaged(cluster))
        {
            await RequireGatewayOperationAsync(
                caller.Services,
                cluster,
                "POST /admin/v1/shutdown",
                "The cluster Gateway does not allow a shutdown.",
                token
            );
            return await SubmitAsync(
                caller,
                cluster,
                ClusterAdminCommand.Create("shutdown", new Admin.ShutdownRequest()),
                token
            );
        }
        var result = await SetGoalAsync(caller, cluster, DedicatedServerGoalState.Off, token);
        caller.Undo!.SetData(new GoalChange(cluster.UniqueName, DedicatedServerGoalState.Off));
        return result with { Message = "Stop requested; follow it with cluster.status." };
    }

    // The Discord restart: goal Off, then goal On once the stop is clean. The start half runs on after the call.
    private static async Task<ClusterActionResult> RestartAsync(
        QuasarOperationContext caller,
        ClusterRequest input,
        CancellationToken token
    )
    {
        var cluster = Track(caller, input.UniqueName);
        if (cluster.GoalState == DedicatedServerGoalState.Off)
            throw new QuasarOperationException(
                409,
                $"Cluster '{cluster.UniqueName}' is off; use cluster.start."
            );
        var stop = await SetGoalAsync(caller, cluster, DedicatedServerGoalState.Off, token);
        var commands = caller.Services.GetRequiredService<ClusterCommandService>();
        var logger = caller
            .Services.GetRequiredService<ILoggerFactory>()
            .CreateLogger(typeof(QuasarClusterControlOperations));
        var actor = Actor(caller.User);
        _ = Task.Run(async () =>
        {
            try
            {
                var start = await commands.StartAfterStopAsync(
                    cluster.UniqueName,
                    Guid.NewGuid().ToString("N"),
                    actor
                );
                if (start.Error is { } error)
                    logger.LogWarning(
                        "Cluster {Cluster} restart could not start it again: {Error}",
                        cluster.UniqueName,
                        error.Message
                    );
            }
            catch (Exception error)
            {
                logger.LogWarning(
                    error,
                    "Cluster {Cluster} restart could not start it again.",
                    cluster.UniqueName
                );
            }
        });
        return stop with
        {
            State = nameof(ClusterOperationState.Running),
            Message =
                "Stop requested; Quasar starts the cluster again once it has stopped cleanly, giving up after ten minutes. Follow it with cluster.status.",
        };
    }

    private static async Task<ClusterActionResult> SaveAsync(
        QuasarOperationContext caller,
        ClusterRequest input,
        CancellationToken token
    )
    {
        var cluster = Track(caller, input.UniqueName);
        if (cluster.GoalState != DedicatedServerGoalState.On)
            throw new QuasarOperationException(409, $"Cluster '{cluster.UniqueName}' is not on.");
        await RequireGatewayOperationAsync(
            caller.Services,
            cluster,
            "POST /admin/v1/save-all",
            "The cluster Gateway does not allow a world save.",
            token
        );
        return await SubmitAsync(caller, cluster, new("save-all"), token);
    }

    private static async Task<ClusterActionResult> GatewayRestartAsync(
        QuasarOperationContext caller,
        ClusterRequest input,
        CancellationToken token
    )
    {
        var cluster = Track(caller, input.UniqueName);
        if (cluster.GoalState != DedicatedServerGoalState.On || cluster.Gateway is null)
            throw new QuasarOperationException(
                409,
                "Only a running cluster with a managed Gateway can restart its Gateway."
            );
        await RequireGatewayOperationAsync(
            caller.Services,
            cluster,
            "POST /admin/v1/gateway/restart",
            "The cluster Gateway does not allow a restart.",
            token
        );
        return await SubmitAsync(caller, cluster, new("gateway-restart"), token);
    }

    private static Task<ClusterActionResult> RecoverAsync(
        QuasarOperationContext caller,
        ClusterRequest input,
        string kind,
        Func<ClusterDeploymentService, string, string, string, Task<ClusterOperation>> recover,
        CancellationToken token
    )
    {
        var cluster = Track(caller, input.UniqueName);
        if (cluster.GoalState != DedicatedServerGoalState.Off || cluster.ActiveDeployment is null)
            throw new QuasarOperationException(
                409,
                "Recovery needs a deployed cluster whose goal is Off."
            );
        var deployments = caller.Services.GetRequiredService<ClusterDeploymentService>();
        return RunAsync(
            caller,
            cluster,
            kind,
            (key, actor) => recover(deployments, cluster.UniqueName, key, actor),
            token
        );
    }

    private static Task<ClusterActionResult> SetGoalAsync(
        QuasarOperationContext caller,
        ClusterDefinition cluster,
        DedicatedServerGoalState goal,
        CancellationToken token
    )
    {
        var commands = caller.Services.GetRequiredService<ClusterCommandService>();
        return RunAsync(
            caller,
            cluster,
            "cluster.goal.set",
            (key, actor) => commands.SetGoalAsync(cluster.UniqueName, new(goal), key, actor),
            token
        );
    }

    private static Task<ClusterActionResult> SubmitAsync(
        QuasarOperationContext caller,
        ClusterDefinition cluster,
        ClusterAdminCommand command,
        CancellationToken token
    )
    {
        var commands = caller.Services.GetRequiredService<ClusterCommandService>();
        return RunAsync(
            caller,
            cluster,
            "cluster." + command.Action,
            (key, actor) => commands.SubmitAsync(cluster.UniqueName, command, key, actor),
            token
        );
    }

    // The work runs to its end like a click on the page; the deadline only ends the wait for it.
    private static async Task<ClusterActionResult> RunAsync(
        QuasarOperationContext caller,
        ClusterDefinition cluster,
        string kind,
        Func<string, string, Task<ClusterOperation>> run,
        CancellationToken token
    )
    {
        var key = Guid.NewGuid().ToString("N");
        Task<ClusterOperation>? task = null;
        ClusterOperation operation;
        try
        {
            task = run(key, Actor(caller.User));
            operation = await task.WaitAsync(token);
        }
        catch (OperationCanceledException)
            when (token.IsCancellationRequested && task is { IsCompleted: false })
        {
            var running = caller
                .Services.GetRequiredService<ClusterOperationStore>()
                .Find(cluster.UniqueName, kind, key);
            return new(
                cluster.UniqueName,
                running?.OperationId,
                nameof(ClusterOperationState.Running),
                "Still running at the deadline. " + FollowHint
            );
        }
        catch (ClusterOperationConflictException error)
        {
            throw new QuasarOperationException(
                error.StatusCode is >= 400 and < 500 ? error.StatusCode : 409,
                error.Message
            );
        }
        catch (ClusterOperationStoreUnavailableException error)
        {
            throw new QuasarOperationException(503, error.Message);
        }
        catch (ClusterGatewayException error)
        {
            throw GatewayUnavailable(error);
        }
        catch (Exception error)
            when (error is InvalidOperationException or InvalidDataException or KeyNotFoundException
            )
        {
            throw new QuasarOperationException(409, error.Message);
        }
        if (operation.State == ClusterOperationState.Failed)
            throw new QuasarOperationException(
                409,
                $"{operation.Error?.Message ?? "The cluster operation failed."} (operation {operation.OperationId})"
            );
        return new(
            cluster.UniqueName,
            operation.OperationId,
            operation.State.ToString(),
            operation.State == ClusterOperationState.Running ? "Submitted. " + FollowHint : "Done."
        );
    }

    private static QuasarOperationException GatewayUnavailable(ClusterGatewayException error) =>
        new(
            (int)error.StatusCode is >= 400 and < 500 ? 409 : 503,
            $"The cluster Gateway did not answer: {error.Message}"
        );

    // The page's CanRun: a Gateway credential with management scope that offers the operation.
    private static async Task RequireGatewayOperationAsync(
        IServiceProvider services,
        ClusterDefinition cluster,
        string operation,
        string unavailable,
        CancellationToken token
    )
    {
        Admin.AdminCapabilities capabilities;
        try
        {
            capabilities = (
                await services
                    .GetRequiredService<ClusterGatewayClient>()
                    .GetCapabilitiesAsync(cluster, token)
            ).Data;
        }
        catch (ClusterGatewayException error)
        {
            throw GatewayUnavailable(error);
        }
        if (
            capabilities.Scope != Admin.AdminScope.Manage
            || !capabilities.Operations.Contains(operation, StringComparer.Ordinal)
        )
            throw new QuasarOperationException(409, unavailable);
    }

    private static ClusterOperationStatus Operation(
        QuasarOperationContext caller,
        ClusterOperationRequest input
    )
    {
        var cluster = RequireCluster(caller, input.UniqueName);
        var operation = caller
            .Services.GetRequiredService<ClusterOperationStore>()
            .Get(input.OperationId?.Trim() ?? string.Empty);
        if (
            operation is null
            || !operation.Cluster.Equals(cluster.UniqueName, StringComparison.OrdinalIgnoreCase)
        )
            throw new QuasarOperationException(
                404,
                $"Operation '{input.OperationId}' not found on cluster '{cluster.UniqueName}'."
            );
        return new(
            operation.OperationId,
            operation.Kind,
            operation.State.ToString(),
            operation.Actor,
            operation.CreatedAt,
            operation.UpdatedAt,
            operation.Error is { } error ? $"{error.Code}: {error.Message}" : null,
            operation.Result
        );
    }

    // The checks of the Players page's cluster player menu, then the same command.
    private static async Task<ClusterActionResult> PlayerActionAsync(
        QuasarOperationContext caller,
        string uniqueName,
        string steamIdText,
        string action,
        Admin.PlayerPromotionLevel? level,
        CancellationToken token
    )
    {
        if (
            !ulong.TryParse(
                steamIdText?.Trim(),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var steamId
            )
            || steamId == 0
        )
            throw new QuasarOperationException(400, "SteamId must be a SteamID64.");
        var cluster = Track(caller, uniqueName);
        var snapshot = caller
            .Services.GetRequiredService<ClusterPlayerService>()
            .GetSnapshot(cluster.UniqueName);
        if (
            snapshot?.CanRun(ClusterPlayerSnapshot.GatewayOperation(action)) != true
            || action == "set-player-promotion" && snapshot.Moderation?.Promotions is null
        )
            throw new QuasarOperationException(
                409,
                ClusterPlayerSnapshot.ModerationUnavailableReason(snapshot)
                    ?? "The cluster Gateway does not support this player action."
            );
        if (action == "kick" && !snapshot.Clients.Any(client => client.ClientId == steamId))
            throw new QuasarOperationException(409, "This player is not connected to the cluster.");
        if (
            action == "clear-kick-cooldown"
            && snapshot.Moderation?.KickCooldowns.Any(kick =>
                kick.UserId == steamId && kick.ExpiresAt > DateTimeOffset.UtcNow
            ) != true
        )
            throw new QuasarOperationException(
                409,
                "This player has no kick cooldown on the cluster."
            );

        object? before = null;
        object? after = null;
        Admin.SetPlayerPromotionRequest? promotion = null;
        if (action is "ban" or "unban")
        {
            before = await IsBannedAsync(caller.Services, cluster, steamId, token);
            after = action == "ban";
        }
        else if (level is { } wanted)
        {
            var current = snapshot.Moderation!.Promotions!.FirstOrDefault(entry =>
                entry.UserId == steamId
            );
            before = (current?.Level ?? Admin.PlayerPromotionLevel.None).ToString();
            after = wanted.ToString();
            promotion = new(wanted, current?.Revision ?? 0);
        }
        var result = await SubmitAsync(
            caller,
            cluster,
            ClusterAdminCommand.Create(
                action,
                promotion,
                target: steamId.ToString(CultureInfo.InvariantCulture)
            ),
            token
        );
        // Nothing to undo when the action left things as they were.
        if (after is not null && !Equals(before, after))
            caller.Undo!.SetData(
                new PlayerChange(
                    cluster.UniqueName,
                    steamId,
                    JsonSerializer.SerializeToElement(before),
                    JsonSerializer.SerializeToElement(after)
                )
            );
        return result;
    }

    private static async Task<bool> IsBannedAsync(
        IServiceProvider services,
        ClusterDefinition cluster,
        ulong steamId,
        CancellationToken token
    )
    {
        try
        {
            return (
                await services
                    .GetRequiredService<ClusterGatewayClient>()
                    .GetBansAsync(cluster, token)
            ).Data.Banned.Contains(steamId);
        }
        catch (ClusterGatewayException error)
        {
            throw GatewayUnavailable(error);
        }
    }

    private static async Task<Admin.PlayerPromotion> PromotionAsync(
        IServiceProvider services,
        ClusterDefinition cluster,
        ulong steamId,
        CancellationToken token
    )
    {
        Admin.PlayerModerationStatus moderation;
        try
        {
            moderation = (
                await services
                    .GetRequiredService<ClusterGatewayClient>()
                    .GetPlayerModerationAsync(cluster, token)
            ).Data;
        }
        catch (ClusterGatewayException error)
        {
            throw GatewayUnavailable(error);
        }
        if (moderation.Promotions is null)
            throw new QuasarOperationException(
                409,
                moderation.PromotionError ?? "The cluster's player roles are unavailable."
            );
        return moderation.Promotions.FirstOrDefault(entry => entry.UserId == steamId)
            ?? new(steamId, Admin.PlayerPromotionLevel.None);
    }

    private static async Task<QuasarServerOperations.LogTailResponse> NodeLogsAsync(
        QuasarOperationContext caller,
        ClusterLogRequest input,
        CancellationToken token
    )
    {
        var cluster = RequireCluster(caller, input.UniqueName);
        ClusterFleet fleet;
        try
        {
            fleet = (
                await caller
                    .Services.GetRequiredService<ClusterFleetService>()
                    .GetAsync(cluster, token)
            ).Data;
        }
        catch (ClusterGatewayException error)
        {
            throw GatewayUnavailable(error);
        }
        var node = input.Node?.Trim();
        var nodes = fleet
            .Nodes.Where(entry =>
                string.IsNullOrEmpty(node)
                || entry.Registry.Node.Equals(node, StringComparison.OrdinalIgnoreCase)
            )
            .ToList();
        if (!string.IsNullOrEmpty(node) && nodes.Count == 0)
            throw new QuasarOperationException(
                404,
                $"Node '{node}' not found in cluster '{cluster.UniqueName}'; cluster.status lists its nodes."
            );
        var logs = caller.Services.GetRequiredService<PluginLogStream>();
        var lines = nodes
            .Where(entry => entry.Telemetry is not null)
            .SelectMany(entry =>
                logs.GetEntries(entry.Telemetry!.TelemetryKey)
                    .Select(log => (Node: entry.Registry.Node, Log: log))
            )
            .OrderBy(item => item.Log.TimestampUtc)
            .Select(item =>
                $"{item.Log.TimestampUtc:u} · {item.Node} · {item.Log.Plugin} · {item.Log.Level} · {item.Log.Message}"
                + (string.IsNullOrWhiteSpace(item.Log.Exception) ? "" : "\n" + item.Log.Exception)
            )
            .ToList();
        return QuasarServerOperations.SliceLines(
            string.IsNullOrEmpty(node)
                ? "plugin logs of all nodes"
                : $"plugin logs of {nodes[0].Registry.Node}",
            lines,
            new(cluster.UniqueName, null, input.Lines, input.From, input.Filter, input.MaxBytes)
        );
    }

    private static async Task<QuasarServerOperations.LogTailResponse> EventsAsync(
        QuasarOperationContext caller,
        ClusterEventsRequest input,
        CancellationToken token
    )
    {
        var cluster = RequireCluster(caller, input.UniqueName);
        var gateway = caller.Services.GetRequiredService<ClusterGatewayClient>();
        var events = new List<Admin.AdminEvent>();
        long cursor = 0;
        try
        {
            for (var page = 0; page < EventPages; page++)
            {
                var data = (
                    await gateway.GetEventsAsync(cluster, cursor, EventPageSize, token)
                ).Data;
                events.AddRange(data.Events);
                if (data.Events.Length < EventPageSize || data.NextCursor <= cursor)
                    break;
                cursor = data.NextCursor;
            }
        }
        catch (ClusterGatewayException error)
        {
            throw GatewayUnavailable(error);
        }
        var lines = events
            .DistinctBy(entry => entry.Seq)
            .OrderBy(entry => entry.Seq)
            .Select(entry =>
                string.Join(
                    " · ",
                    new[] { entry.At.ToString("u"), entry.Type, entry.Actor, entry.Target }.Where(
                        value => !string.IsNullOrWhiteSpace(value)
                    )
                )
                + (entry.Data is { Count: > 0 } data ? " · " + JsonSerializer.Serialize(data) : "")
            )
            .ToList();
        return QuasarServerOperations.SliceLines(
            "Cluster Registry events",
            lines,
            new(cluster.UniqueName, null, input.Lines, input.From, input.Filter, input.MaxBytes)
        );
    }

    private sealed record GoalChange(string UniqueName, DedicatedServerGoalState After);

    // Undo of a start sets the goal Off again, undo of a stop sets it On, while the goal is still what the action left.
    private sealed class GoalStateUndo : IQuasarUndoHandler
    {
        public Task<string?> CheckAsync(QuasarUndoContext context, CancellationToken token)
        {
            var change = context.Data.Deserialize<GoalChange>(QuasarCoreOperations.Json)!;
            var cluster = context
                .Services.GetRequiredService<ClusterCatalog>()
                .GetCluster(change.UniqueName);
            return Task.FromResult(
                cluster is null ? $"Cluster '{change.UniqueName}' no longer exists."
                : cluster.GoalState == change.After ? null
                : $"The cluster's goal is {cluster.GoalState} now, not {change.After}."
            );
        }

        public async Task ReverseAsync(QuasarUndoContext context, CancellationToken token)
        {
            var change = context.Data.Deserialize<GoalChange>(QuasarCoreOperations.Json)!;
            var operation = await context
                .Services.GetRequiredService<ClusterCommandService>()
                .SetGoalAsync(
                    change.UniqueName,
                    new(
                        change.After == DedicatedServerGoalState.On
                            ? DedicatedServerGoalState.Off
                            : DedicatedServerGoalState.On
                    ),
                    Guid.NewGuid().ToString("N"),
                    Actor(context.User),
                    token
                );
            if (operation.State == ClusterOperationState.Failed)
                throw new InvalidOperationException(
                    operation.Error?.Message ?? "The goal change failed."
                );
        }
    }

    private sealed record PlayerChange(
        string UniqueName,
        ulong SteamId,
        JsonElement Before,
        JsonElement After
    );

    // Compares the Gateway's current state with what the action left and sends the command that restores the previous one.
    private sealed class PlayerUndo(
        Func<
            IServiceProvider,
            ClusterDefinition,
            ulong,
            CancellationToken,
            Task<JsonElement>
        > current,
        Func<JsonElement, (string Action, Admin.PlayerPromotionLevel? Level)> restore
    ) : IQuasarUndoHandler
    {
        public async Task<string?> CheckAsync(QuasarUndoContext context, CancellationToken token)
        {
            var change = context.Data.Deserialize<PlayerChange>(QuasarCoreOperations.Json)!;
            var cluster = context
                .Services.GetRequiredService<ClusterCatalog>()
                .GetCluster(change.UniqueName);
            if (cluster is null)
                return $"Cluster '{change.UniqueName}' no longer exists.";
            var now = await current(context.Services, cluster, change.SteamId, token);
            return now.GetRawText() == change.After.GetRawText()
                ? null
                : $"Player {change.SteamId} on '{change.UniqueName}' is {now.GetRawText()} now, not {change.After.GetRawText()}.";
        }

        public async Task ReverseAsync(QuasarUndoContext context, CancellationToken token)
        {
            var change = context.Data.Deserialize<PlayerChange>(QuasarCoreOperations.Json)!;
            var cluster =
                context.Services.GetRequiredService<ClusterCatalog>().GetCluster(change.UniqueName)
                ?? throw new InvalidOperationException(
                    $"Cluster '{change.UniqueName}' no longer exists."
                );
            var (action, level) = restore(change.Before);
            Admin.SetPlayerPromotionRequest? promotion = level is { } wanted
                ? new(
                    wanted,
                    (
                        await PromotionAsync(context.Services, cluster, change.SteamId, token)
                    ).Revision
                )
                : null;
            var operation = await context
                .Services.GetRequiredService<ClusterCommandService>()
                .SubmitAsync(
                    cluster.UniqueName,
                    ClusterAdminCommand.Create(
                        action,
                        promotion,
                        target: change.SteamId.ToString(CultureInfo.InvariantCulture)
                    ),
                    Guid.NewGuid().ToString("N"),
                    Actor(context.User),
                    token
                );
            if (operation.State == ClusterOperationState.Failed)
                throw new InvalidOperationException(
                    operation.Error?.Message ?? "The player action failed."
                );
        }
    }
}
