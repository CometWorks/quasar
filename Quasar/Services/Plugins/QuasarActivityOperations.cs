using System.Security.Claims;
using System.Text.Json;
using Quasar.Models;
using Quasar.Plugin.Abstractions.Operations;
using Quasar.Services.Audit;
using Quasar.Services.Auth;

namespace Quasar.Services.Plugins;

/// <summary>The dashboard's recent activity and the audit log, as reads.</summary>
public static class QuasarActivityOperations
{
    public sealed record ActivityRequest(string? UniqueName = null, int Limit = 20);

    /// <param name="UniqueName">The server or cluster the event belongs to.</param>
    /// <param name="Level">error, warning, success or info.</param>
    public sealed record ActivityItem(
        DateTimeOffset At,
        string UniqueName,
        string DisplayName,
        string Message,
        string Level
    )
    {
        internal ClusterActivityDetails? ClusterDetails { get; init; }
    }

    internal sealed record ClusterActivityDetails(
        string OperationId,
        string Kind,
        DateTimeOffset StartedAt,
        string? ErrorCode
    );

    /// <param name="PrincipalKind">Person, Assistant, PlayerAgent or Monitor.</param>
    /// <param name="Result">Ok, Refused or Failed.</param>
    public sealed record AuditRequest(
        DateTimeOffset? From = null,
        DateTimeOffset? To = null,
        string? PrincipalKind = null,
        string? Principal = null,
        string? Requester = null,
        string? Operation = null,
        string? Server = null,
        string? Result = null,
        int Skip = 0,
        int Take = 50
    );

    /// <param name="Arguments">The request JSON with secrets redacted, cut at 500 characters.</param>
    public sealed record AuditItem(
        string Id,
        DateTimeOffset Time,
        string Principal,
        string? Requester,
        string Operation,
        string? Server,
        string Mode,
        string Result,
        string? Error,
        string? Reason,
        string? Request,
        string? Arguments,
        string Reversal,
        string? Reverses
    );

    /// <param name="More">Whether more entries match after these; ask again with a larger skip.</param>
    public sealed record AuditPage(IReadOnlyList<AuditItem> Entries, bool More);

    private const int MaxActivity = 100;
    private const int MaxAudit = 200;
    private const int ArgumentsLength = 500;

    public static void AddQuasarActivityOperations(this IServiceCollection services)
    {
        services.AddQuasarOperation<ActivityRequest, IReadOnlyList<ActivityItem>>(
            QuasarCoreOperations.PluginId,
            "activity.recent",
            [QuasarPolicyNames.CanView],
            (caller, input, _) => Task.FromResult(Activity(caller, input)),
            new()
            {
                Effect = QuasarOperationEffect.Read,
                Description =
                    $"Returns the dashboard's recent activity, newest first: server starts, stops and crashes, restarts with their reason, cluster operations and their progress, and agent command results. Optionally for one server or cluster, at most limit (default 20, up to {MaxActivity}) events.",
            }
        );
        services.AddQuasarOperation<AuditRequest, AuditPage>(
            QuasarCoreOperations.PluginId,
            "audit.list",
            [QuasarPolicyNames.CanManageSecurity],
            AuditAsync,
            new()
            {
                Effect = QuasarOperationEffect.Read,
                Description =
                    $"Lists audit log entries of changes made through Quasar, newest first, filtered like the Audit page: time range, principal kind and name, requester, operation, server and result substrings. Pages with skip and take (default 50, up to {MaxAudit}).",
            }
        );
    }

    /// <summary>Everything the user may see, newest first; the dashboard shows the first 5.</summary>
    internal static IReadOnlyList<ActivityItem> Recent(
        IServiceProvider services,
        ClaimsPrincipal user,
        int limit
    )
    {
        var events = new List<ActivityItem>();
        var clusters = (services.GetService<ClusterCatalog>()?.GetClusters() ?? [])
            .Where(cluster => user.CanQueryCluster(cluster.UniqueName))
            .ToDictionary(cluster => cluster.UniqueName, StringComparer.OrdinalIgnoreCase);
        foreach (
            var runtime in services.GetRequiredService<DedicatedServerSupervisor>().GetSnapshots()
        )
        {
            var name = runtime.UniqueName;
            if (runtime.StartedAtUtc is { } started)
                events.Add(new(started, name, name, "Server started", "success"));
            if (runtime.StoppedAtUtc is { } stopped)
                events.Add(
                    runtime.State
                        is DedicatedServerProcessState.Crashed
                            or DedicatedServerProcessState.Faulted
                        ? new(stopped, name, name, runtime.LastMessage, "error")
                        : new(stopped, name, name, "Server stopped", "info")
                );
            if (runtime.LastRestart is { } restart)
                events.Add(
                    new(
                        restart.CompletedAtUtc ?? restart.RequestedAtUtc,
                        name,
                        name,
                        string.IsNullOrWhiteSpace(restart.Reason)
                            ? "Restart " + restart.Outcome.ToString().ToLowerInvariant()
                            : restart.Reason,
                        restart.Outcome == DedicatedServerRestartOutcome.Failed
                            ? "error"
                            : "warning"
                    )
                );
        }
        foreach (
            var operation in services
                .GetService<ClusterOperationStore>()
                ?.GetRecent(clusters.Keys, limit)
                ?? []
        )
        {
            var cluster = clusters[operation.Cluster];
            events.Add(
                new(
                    operation.UpdatedAt,
                    cluster.UniqueName,
                    string.IsNullOrWhiteSpace(cluster.DisplayName)
                        ? cluster.UniqueName
                        : cluster.DisplayName,
                    ClusterActivityMessage(operation),
                    operation.State switch
                    {
                        ClusterOperationState.Failed => "error",
                        ClusterOperationState.Running => "warning",
                        _ => "success",
                    }
                )
                {
                    ClusterDetails = new(
                        operation.OperationId,
                        operation.Kind,
                        operation.CreatedAt,
                        operation.Error?.Code
                    ),
                }
            );
        }
        foreach (
            var agent in services
                .GetRequiredService<AgentRegistry>()
                .GetAgents()
                .Where(agent => !agent.IsCluster || clusters.ContainsKey(agent.ClusterId))
        )
        foreach (
            var result in agent
                .CommandResults.Where(result => !string.IsNullOrWhiteSpace(result.Message))
                .Take(6)
        )
            events.Add(
                new(
                    result.CompletedAtUtc,
                    agent.IsCluster ? agent.ClusterId : agent.UniqueNameKey,
                    agent.ServerDisplayName,
                    result.Message,
                    result.Success ? "info" : "error"
                )
            );
        return events.OrderByDescending(entry => entry.At).Take(limit).ToArray();
    }

    private static IReadOnlyList<ActivityItem> Activity(
        QuasarOperationContext caller,
        ActivityRequest input
    )
    {
        var limit = Math.Clamp(input.Limit <= 0 ? 20 : input.Limit, 1, MaxActivity);
        var name = input.UniqueName?.Trim();
        if (string.IsNullOrEmpty(name))
            return Recent(caller.Services, caller.User, limit);
        var known =
            caller.Services.GetRequiredService<DedicatedServerCatalog>().GetServer(name) is not null
            || caller.Services.GetService<ClusterCatalog>()?.GetCluster(name) is { } cluster
                && caller.User.CanQueryCluster(cluster.UniqueName)
            || caller
                .Services.GetRequiredService<AgentRegistry>()
                .GetAgents()
                .Any(agent =>
                    !agent.IsCluster
                    && agent.UniqueNameKey.Equals(name, StringComparison.OrdinalIgnoreCase)
                );
        if (!known)
            throw new QuasarOperationException(404, $"Server or cluster '{name}' not found.");
        // Filtered after the cut a cluster's operations get, so a busy fleet can't hide one server's events.
        return Recent(caller.Services, caller.User, int.MaxValue)
            .Where(item => item.UniqueName.Equals(name, StringComparison.OrdinalIgnoreCase))
            .Take(limit)
            .ToArray();
    }

    private static string ClusterActivityMessage(ClusterOperation operation)
    {
        var action = operation.Kind switch
        {
            "cluster.goal.set"
                when operation.Result is { ValueKind: JsonValueKind.Object } result
                    && result.TryGetProperty("goal", out var goal) => goal.ToString() switch
            {
                "On" => "Cluster start requested",
                "Off" => "Cluster stop requested",
                _ => "Cluster state change",
            },
            "cluster.goal.set" => "Cluster state change",
            "cluster.lifecycle.shutdown" or "cluster.shutdown" => "Cluster shutdown",
            "cluster.gateway-restart" => "Cluster Gateway restart",
            "cluster.save-all" => "Cluster world save",
            "cluster.kick" => "Cluster player kick",
            "cluster.ban" => "Cluster player ban",
            "cluster.unban" => "Cluster player unban",
            "cluster.setup" => "Cluster setup",
            "cluster.backup.create" => "Cluster backup",
            "cluster.backup.prepare-restore" => "Cluster restore preparation",
            "cluster.backup.restore" => "Cluster restore",
            "cluster.deployment.prepare" => "Cluster deployment preparation",
            "cluster.deployment.activate" => "Cluster deployment activation",
            "cluster.update.begin" => "Cluster update",
            "cluster.update.abandon" => "Cluster update cancellation",
            "cluster.recover" or "cluster.gateway.recover" => "Cluster recovery",
            _ => "Cluster operation",
        };
        return action
            + operation.State switch
            {
                ClusterOperationState.Running => " · in progress",
                ClusterOperationState.Failed => " · failed",
                _ => " · completed",
            };
    }

    private static async Task<AuditPage> AuditAsync(
        QuasarOperationContext caller,
        AuditRequest input,
        CancellationToken token
    )
    {
        var take = Math.Clamp(input.Take <= 0 ? 50 : input.Take, 1, MaxAudit);
        var entries = await caller
            .Services.GetRequiredService<QuasarAuditLog>()
            .QueryAsync(
                new()
                {
                    From = input.From,
                    To = input.To,
                    PrincipalKind = ParseOrNull<QuasarAuditPrincipalKind>(
                        input.PrincipalKind,
                        "PrincipalKind"
                    ),
                    Principal = input.Principal,
                    Requester = input.Requester,
                    Operation = input.Operation,
                    Server = input.Server,
                    Result = ParseOrNull<QuasarAuditResult>(input.Result, "Result"),
                    Skip = Math.Max(0, input.Skip),
                    Take = take + 1,
                },
                token
            );
        return new(entries.Take(take).Select(Item).ToList(), entries.Count > take);
    }

    private static AuditItem Item(QuasarAuditEntry entry)
    {
        var arguments = entry.Arguments is { } json
            ? JsonSerializer.Serialize(QuasarAuditLog.Redact(json))
            : null;
        if (arguments?.Length > ArgumentsLength)
            arguments = arguments[..ArgumentsLength] + "…";
        var requester = string.Join(
            " ",
            new[]
            {
                entry.Requester.Subject,
                entry.Requester.PlayerName,
                entry.Requester.SteamId is null ? null : "Steam " + entry.Requester.SteamId,
                entry.Requester.DiscordUserId is null
                    ? null
                    : "Discord " + entry.Requester.DiscordUserId,
            }
                .Where(part => !string.IsNullOrWhiteSpace(part))
                .Distinct()
        );
        return new(
            entry.Id,
            entry.Time,
            $"{entry.Principal.Kind}: {entry.Principal.Name ?? entry.Principal.Subject}",
            requester.Length == 0 ? null : requester,
            entry.Operation,
            entry.Server,
            entry.Mode + (entry.ApprovedBy is null ? null : " by " + entry.ApprovedBy),
            entry.Result.ToString(),
            entry.Error,
            entry.Reason,
            entry.Request,
            arguments,
            entry.Reversal.ToString(),
            entry.Reverses
        );
    }

    private static T? ParseOrNull<T>(string? value, string field)
        where T : struct, Enum =>
        string.IsNullOrWhiteSpace(value) ? null
        : Enum.TryParse<T>(value.Trim(), ignoreCase: true, out var parsed) && Enum.IsDefined(parsed)
            ? parsed
        : throw new QuasarOperationException(
            400,
            $"{field} must be one of {string.Join(", ", Enum.GetNames<T>())}."
        );
}
