using Magnetar.Protocol.Model;
using Quasar.Services.Auth;

namespace Quasar.Services.ServerList;

// Private management envelope. Only Listings are public; never publish this envelope verbatim.
public sealed record ServerListPublication(int SchemaVersion, string InstallationId, long Epoch, long Sequence,
    long DiagnosticsGeneration, string? ConsentDecisionUtc, string Challenge,
    IReadOnlyList<ServerListAdmin> Admins, IReadOnlyList<ServerListListing> Listings);
public sealed record ServerListWithdrawal(int SchemaVersion, string InstallationId, long Epoch, long Sequence);
public sealed record ServerListChallenge(string Nonce, DateTimeOffset ExpiresAtUtc, long DiagnosticsGeneration);
public sealed record ServerListAdmin(string Provider, string Subject);
public sealed record ServerListListing(string ListingId, string Name, string PublicHost, int PublicPort,
    DateTimeOffset? ObservedAtUtc, bool? Running, int? PlayersOnline, int? MaxPlayers, float? SimSpeed,
    string Platform = "Magnetar", ServerListCluster? Cluster = null);
public sealed record ServerListCluster(string Kind, int? Online, int? Total, ServerListNode[]? Nodes = null,
    string? Phase = null, string? Health = null, int? Partitions = null, int? Handovers = null);
public sealed record ServerListNode(string Name, string Role, string State, bool? Online, int? QuarantinedPartitions);

public static class ServerListProtocol
{
    public const int Version = 1;
    public static readonly TimeSpan Freshness = TimeSpan.FromSeconds(60);
    public static bool IsId(string? value) => value is { Length: 32 }
        && Guid.TryParseExact(value, "N", out _) && value == value.ToLowerInvariant();

    public static bool HasConsent(DataHandlingConsentSettings settings) => settings.ListingGranted
        && settings.PolicyVersion == DataHandlingConsentSettings.CurrentPolicyVersion && settings.DiagnosticsGranted && settings.Generation > 0
        && settings.GrantedSinceUtc != default;

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
            NonNegative(metrics.MaxPlayers), snapshot.IsRunning && float.IsFinite(metrics.SimSpeed)
                && metrics.SimSpeed >= 0 ? Math.Min(metrics.SimSpeed, 1f) : null);
    }

    internal static ServerListListing FromCluster(ServerListEntry entry,
        CometWorks.ClusterGateway.AdminContract.V1.ClusterStatus status, DateTimeOffset now)
    {
        if (!IsFresh(status.ObservedAt, now)) return UnknownCluster(entry);
        // Publish ordinal labels rather than private host names, node identifiers or endpoints.
        var nodes = status.Nodes.Select((node, index) => new ServerListNode($"Node {index + 1}",
            node.Role.ToString(), node.State.ToString(), node.LeaseFresh && node.LeaseExpires > now
                && node.State != CometWorks.ClusterGateway.AdminContract.V1.NodeState.Dead,
            NonNegative(node.QuarantinedPartitions))).ToArray();
        return new(entry.ListingId, entry.Name, entry.PublicHost, entry.PublicPort, status.ObservedAt,
            status.AcceptingPlayers, NonNegative(status.Counts.ConnectedClients), null, null, "Magnetar",
            new("CometWorks", nodes.Count(n => n.Online == true), nodes.Length, nodes,
                status.Phase.ToString(), status.Health.ToString(), NonNegative(status.Counts.Partitions),
                NonNegative(status.Counts.Handovers)));
    }

    internal static bool IsFresh(DateTimeOffset observed, DateTimeOffset now) => observed <= now
        && now - observed <= Freshness;
    internal static int? NonNegative(int value) => value >= 0 ? value : null;
    internal static ServerListListing UnknownCluster(ServerListEntry entry) => Unknown(entry) with
        { Cluster = new("CometWorks", null, null) };
    internal static ServerListListing Unknown(ServerListEntry entry) => new(entry.ListingId, entry.Name,
        entry.PublicHost, entry.PublicPort, null, null, null, null, null);
}
