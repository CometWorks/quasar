using System.Text.Json;
using Magnetar.Protocol.Model;
using Quasar.Models;
using Quasar.Services.Auth;
using Admin = CometWorks.ClusterGateway.AdminContract.V1;

namespace Quasar.Services.ServerList;

// Private management envelope. Only Listings are public; never publish this envelope verbatim.
public sealed record ServerListPublication(int SchemaVersion, string InstallationId, long Epoch, long Sequence,
    DateTimeOffset IssuedAtUtc, IReadOnlyList<ServerListAdmin> Admins, IReadOnlyList<ServerListListing> Listings);
public sealed record SignedServerListPublication(string PublicKey, string Payload, string Signature);
public sealed record ServerListAdmin(string Provider, string Subject);
public sealed record ServerListListing(string ListingId, string Name, string PublicHost, int PublicPort,
    DateTimeOffset? ObservedAtUtc, bool? Running, int? PlayersOnline, int? MaxPlayers, float? SimSpeed,
    string Platform = "Magnetar", ServerListCluster? Cluster = null);
public sealed record ServerListCluster(string Kind, int? Online, int? Total, ServerListNode[]? Nodes = null,
    string? Phase = null, string? Health = null, int? Partitions = null, int? Handovers = null);
public sealed record ServerListNode(string Name, string Role, string State, bool? Online, int? QuarantinedPartitions);

public static class ServerListProtocol
{
    public const int Version = 2;
    public static readonly TimeSpan Freshness = TimeSpan.FromSeconds(60);
    public static bool IsId(string? value) => value is { Length: 32 }
        && Guid.TryParseExact(value, "N", out _) && value == value.ToLowerInvariant();

    public static ServerListAdmin[] GetAdmins(RbacConfig config) => config.SubjectRoleMappings
        .Where(m => m.Provider.Equals(QuasarAuthSchemes.Steam, StringComparison.OrdinalIgnoreCase)
            && m.Roles.Contains(QuasarRoles.Admin, StringComparer.OrdinalIgnoreCase)
            && m.Subject.Length == 17 && ulong.TryParse(m.Subject, out _))
        .Select(m => new ServerListAdmin(QuasarAuthSchemes.Steam, m.Subject)).Distinct().ToArray();

    public static ServerListListing FromAgent(ServerListEntry entry, IReadOnlyList<AgentRuntimeState> agents,
        DateTimeOffset now)
    {
        var matches = agents.Where(a => !a.IsCluster && a.UniqueNameKey.Equals(entry.UniqueName,
            StringComparison.OrdinalIgnoreCase)).ToArray();
        var agent = matches.Length == 1 ? matches[0] : null;
        var snapshot = agent?.Snapshot;
        if (agent is null || !agent.IsConnected || snapshot is null
            || !IsFresh(agent.LastSnapshotReceivedUtc, now) || !IsFresh(snapshot.CapturedAtUtc, now))
            return Unknown(entry);
        var metrics = snapshot.Metrics;
        return new(entry.ListingId, entry.Name, entry.PublicHost, entry.PublicPort, snapshot.CapturedAtUtc,
            snapshot.IsRunning, snapshot.IsRunning ? NonNegative(metrics.PlayersOnline) : null,
            NonNegative(metrics.MaxPlayers), snapshot.IsRunning ? SimulationSpeed(metrics.SimSpeed) : null);
    }

    internal static ServerListListing FromCluster(ServerListEntry entry,
        Admin.ClusterStatus status, ClusterDefinition cluster, IReadOnlyList<AgentRuntimeState> agents,
        DateTimeOffset now)
    {
        if (!IsFresh(status.ObservedAt, now)) return UnknownCluster(entry);
        // Admission belongs to the deployed Gateway, not a node's session or a staged profile.
        int? maxPlayers = null;
        if (cluster.ActiveDeployment is { } active
            && (status.DeploymentRevision is null || status.DeploymentRevision == active.Revision))
        {
            using var specification = ClusterOverview.Specification(cluster);
            if (specification?.RootElement is { ValueKind: JsonValueKind.Object } root
                && root.TryGetProperty("memberLimit", out var limit)
                && limit.ValueKind == JsonValueKind.Number
                && limit.TryGetInt32(out var value) && value >= 2)
                maxPlayers = value;
        }

        var observedAt = status.ObservedAt;
        var speeds = new List<float>();
        foreach (var node in status.Nodes.Where(n => n.Role == Admin.NodeRole.Regular
            && n.State == Admin.NodeState.Active && !n.Spare && n.LeaseFresh && n.LeaseExpires > now))
        {
            var agent = ClusterFleetService.MatchAgent(status.ClusterId, node, agents);
            if (agent is not { IsConnected: true, Snapshot: { IsRunning: true, TelemetryPaused: false } snapshot }
                || !IsFresh(agent.LastSnapshotReceivedUtc, now) || !IsFresh(snapshot.CapturedAtUtc, now)
                || SimulationSpeed(snapshot.Metrics.SimSpeed) is not { } speed)
                continue;
            speeds.Add(speed);
            // Never give an older Agent observation a new lifetime with each Registry poll.
            if (snapshot.CapturedAtUtc < observedAt) observedAt = snapshot.CapturedAtUtc;
        }
        // Publish ordinal labels rather than private host names, node identifiers or endpoints.
        var nodes = status.Nodes.Select((node, index) => new ServerListNode($"Node {index + 1}",
            node.Role.ToString(), node.State.ToString(), node.LeaseFresh && node.LeaseExpires > now
                && node.State != Admin.NodeState.Dead,
            NonNegative(node.QuarantinedPartitions))).ToArray();
        return new(entry.ListingId, entry.Name, entry.PublicHost, entry.PublicPort, observedAt,
            status.AcceptingPlayers, NonNegative(status.Counts.ConnectedClients), maxPlayers,
            speeds.Count > 0 ? speeds.Average() : null, "Magnetar",
            new("CometWorks", nodes.Count(n => n.Online == true), nodes.Length, nodes,
                status.Phase.ToString(), status.Health.ToString(), NonNegative(status.Counts.Partitions),
                NonNegative(status.Counts.Handovers)));
    }

    internal static bool IsFresh(DateTimeOffset observed, DateTimeOffset now) => observed <= now
        && now - observed <= Freshness;
    internal static int? NonNegative(int value) => value >= 0 ? value : null;
    private static float? SimulationSpeed(float value) => float.IsFinite(value) && value >= 0
        ? Math.Min(value, 1f) : null;
    internal static ServerListListing UnknownCluster(ServerListEntry entry) => Unknown(entry) with
        { Cluster = new("CometWorks", null, null) };
    internal static ServerListListing Unknown(ServerListEntry entry) => new(entry.ListingId, entry.Name,
        entry.PublicHost, entry.PublicPort, null, null, null, null, null);
}
