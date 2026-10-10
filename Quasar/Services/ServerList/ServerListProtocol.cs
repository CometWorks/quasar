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
    DateTimeOffset? ObservedAtUtc, bool? Running, int? PlayersOnline, int? MaxPlayers, float? SimSpeed);

public static class ServerListProtocol
{
    public const int Version = 1;
    public static readonly TimeSpan Freshness = TimeSpan.FromSeconds(60);
    public static bool IsId(string? value) => value is { Length: 32 }
        && Guid.TryParseExact(value, "N", out _) && value == value.ToLowerInvariant();

    public static bool HasConsent(DataHandlingConsentSettings settings) => settings.ConsentGranted == true
        && settings.PolicyVersion == 2 && settings.DiagnosticsGranted && settings.Generation > 0
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
                && metrics.SimSpeed >= 0 ? metrics.SimSpeed : null);
    }

    internal static bool IsFresh(DateTimeOffset observed, DateTimeOffset now) => observed <= now
        && now - observed <= Freshness;
    internal static int? NonNegative(int value) => value >= 0 ? value : null;
    internal static ServerListListing Unknown(ServerListEntry entry) => new(entry.ListingId, entry.Name,
        entry.PublicHost, entry.PublicPort, null, null, null, null, null);
}
