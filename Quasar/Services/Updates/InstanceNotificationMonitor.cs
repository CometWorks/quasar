using System.Security.Claims;
using Quasar.Models;
using Quasar.Services.Auth;

namespace Quasar.Services.Updates;

/// <summary>Captures short lifecycle transitions and sustained metric incidents independently of push delivery.</summary>
internal sealed class InstanceNotificationMonitor
{
    private readonly object _sync = new();
    private readonly Dictionary<(string Provider, string Subject), AccountState> _accounts = [];

    internal void Reset(string provider, string subject)
    {
        lock (_sync) _accounts.Remove((provider, subject));
    }

    internal UpdateNotice[] GetNotices(string provider, string subject, ClaimsPrincipal user, DateTimeOffset now)
    {
        lock (_sync)
            return _accounts.TryGetValue((provider, subject), out var account)
                ? account.Notices.Where(n => now - n.At < TimeSpan.FromHours(24)
                    && (n.Cluster is null || user.CanQueryCluster(n.Cluster)))
                    .Reverse().Select(n => n.Notice).ToArray() : [];
    }

    internal bool Observe(string provider, string subject, NotificationPreferences options,
        IReadOnlyList<DedicatedServerRuntimeSnapshot> servers, IReadOnlyList<AgentRuntimeState> agents,
        IReadOnlyList<ClusterDefinition> clusters, DateTimeOffset now)
    {
        lock (_sync)
        {
            if (!_accounts.TryGetValue((provider, subject), out var account))
                _accounts[(provider, subject)] = account = new();
            var changed = account.Notices.RemoveAll(n => now - n.At >= TimeSpan.FromHours(24)) > 0;
            foreach (var server in servers)
            {
                var current = new Lifecycle(server.State, server.StoppedAtUtc, server.LastRestart?.RequestedAtUtc);
                if (account.Servers.TryGetValue(server.UniqueName, out var previous))
                {
                    var restart = server.LastRestart;
                    var newRestart = restart is not null && restart.RequestedAtUtc != previous.RestartAt;
                    var failed = server.State is DedicatedServerProcessState.Crashed or DedicatedServerProcessState.Faulted;
                    if (options.Crashes && (newRestart && restart!.Cause == DedicatedServerRestartCause.CrashRecovery
                        || failed && (current.State != previous.State || current.StoppedAt != previous.StoppedAt)))
                        Add("crash", server.UniqueName, "Instance crashed or failed", server.LastMessage, "/");
                    if (options.HealthRestarts && newRestart && restart!.Cause is
                        DedicatedServerRestartCause.HealthPolicy or DedicatedServerRestartCause.AgentAttachRecovery)
                        Add("health-restart", server.UniqueName, "Instance health restart", restart.Reason, "/");
                }
                account.Servers[server.UniqueName] = current;
            }
            foreach (var key in account.Servers.Keys.Except(servers.Select(s => s.UniqueName), StringComparer.OrdinalIgnoreCase).ToArray())
                account.Servers.Remove(key);

            var seenRules = new HashSet<string>(StringComparer.Ordinal);
            foreach (var agent in agents)
            {
                var snapshot = agent.Snapshot;
                if (!agent.IsConnected || snapshot is not { IsRunning: true }) continue;
                var fresh = now - agent.LastSnapshotReceivedUtc <= TimeSpan.FromSeconds(30)
                    && now - snapshot.CapturedAtUtc <= TimeSpan.FromSeconds(30)
                    && snapshot.CapturedAtUtc <= now.AddSeconds(5);
                string? cluster = null;
                string name = agent.UniqueNameKey;
                string url = "/";
                if (agent.IsCluster)
                {
                    // The authenticated cluster identity is authoritative; names from telemetry are not.
                    if (!agent.HasClusterIdentity || !clusters.Any(c => c.UniqueName == agent.ClusterId)
                        || agents.Count(a => a.IsConnected && a.ClusterId == agent.ClusterId
                            && a.ClusterSlot == agent.ClusterSlot && a.ClusterNodeId == agent.ClusterNodeId
                            && a.ClusterEpoch == agent.ClusterEpoch) != 1) continue;
                    cluster = agent.ClusterId;
                    name = $"{cluster}/{agent.ClusterNodeId}";
                    url = "/clusters/" + Uri.EscapeDataString(cluster);
                }
                else if (!servers.Any(s => s.UniqueName.Equals(name, StringComparison.OrdinalIgnoreCase)
                             && s.State == DedicatedServerProcessState.Running)) continue;

                var metrics = snapshot.Metrics;
                Check("sim-speed", options.LowSimSpeed, fresh && metrics is { SimSpeed: >= 0 } && float.IsFinite(metrics.SimSpeed)
                    ? metrics.SimSpeed < options.SimSpeedThreshold : null,
                    "Low simulation speed", $"Simulation speed {metrics?.SimSpeed:0.000}; threshold {options.SimSpeedThreshold:0.000}.");
                Check("cpu", options.HighCpu, fresh && metrics is { ServerCpuLoadPercent: >= 0 } && float.IsFinite(metrics.ServerCpuLoadPercent)
                    ? metrics.ServerCpuLoadPercent > options.CpuThreshold : null,
                    "High process CPU usage", $"Process CPU {metrics?.ServerCpuLoadPercent:0.0}%; threshold {options.CpuThreshold:0.0}% (100% = one logical CPU).");
                Check("memory", options.HighMemory, fresh && metrics is { MemoryWorkingSetMb: >= 0 }
                    ? metrics.MemoryWorkingSetMb > options.MemoryThresholdMb : null,
                    "High memory usage", $"Working set {metrics?.MemoryWorkingSetMb:N0} MB; threshold {options.MemoryThresholdMb:N0} MB.");
                Check("unsaved", options.UnsavedWorld, fresh && metrics is { UnsavedGameTimeSeconds: >= 0 }
                    ? metrics.UnsavedGameTimeSeconds > options.UnsavedThresholdMinutes * 60L : null,
                    "World has unsaved changes", $"Unsaved game time {metrics?.UnsavedGameTimeSeconds / 60:N0} minutes; threshold {options.UnsavedThresholdMinutes} minutes.");

                void Check(string rule, bool enabled, bool? breached, string title, string body)
                {
                    if (!enabled) return;
                    var key = $"{agent.TelemetryKey}:{agent.ConnectionId}:{rule}";
                    seenRules.Add(key);
                    if (!account.Rules.TryGetValue(key, out var state)) account.Rules[key] = state = new();
                    if (breached is null)
                    {
                        state.Since = null;
                        state.Notified = false;
                        return;
                    }
                    var at = snapshot.CapturedAtUtc;
                    if (at <= state.LastSample) return;
                    if (at - state.LastSample > TimeSpan.FromSeconds(30))
                    {
                        state.Since = null;
                        state.Notified = false;
                    }
                    state.LastSample = at;
                    if (!breached.Value)
                    {
                        if (state.Notified && options.Recoveries)
                            Add(rule + "-recovered", name, title + " recovered", body, url, cluster);
                        state.Since = null;
                        state.Notified = false;
                        return;
                    }
                    state.Since ??= at;
                    if (at - state.Since < TimeSpan.FromSeconds(options.SustainedSeconds)
                        || at - state.LastAlert < TimeSpan.FromSeconds(options.CooldownSeconds)) return;
                    state.LastAlert = at;
                    state.Notified = true;
                    Add(rule, name, title, body + $" Sustained for at least {options.SustainedSeconds} seconds.", url, cluster);
                }
            }
            foreach (var key in account.Rules.Keys.Except(seenRules).ToArray()) account.Rules.Remove(key);
            return changed;

            void Add(string kind, string name, string title, string body, string url, string? cluster = null)
            {
                var notice = new UpdateNotice($"instance:{kind}:{Guid.NewGuid():N}", $"{name}: {title}", body, url, now);
                account.Notices.Add(new(now, cluster, notice));
                if (account.Notices.Count > 100) account.Notices.RemoveAt(0);
                changed = true;
            }
        }
    }

    private sealed record Lifecycle(DedicatedServerProcessState State, DateTimeOffset? StoppedAt, DateTimeOffset? RestartAt);
    private sealed record Incident(DateTimeOffset At, string? Cluster, UpdateNotice Notice);
    private sealed class AccountState
    {
        public Dictionary<string, Lifecycle> Servers { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, RuleState> Rules { get; } = new(StringComparer.Ordinal);
        public List<Incident> Notices { get; } = [];
    }
    private sealed class RuleState
    {
        public DateTimeOffset LastSample { get; set; }
        public DateTimeOffset? Since { get; set; }
        public DateTimeOffset LastAlert { get; set; }
        public bool Notified { get; set; }
    }
}
