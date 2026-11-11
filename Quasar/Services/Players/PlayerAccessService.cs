using System.Globalization;
using System.Text.Json;
using Magnetar.Protocol.Model;
using Magnetar.Protocol.Transport;
using Quasar.Models;

namespace Quasar.Services.Players;

public sealed record PlayerAccessTarget(string Key, string Name);
public sealed record PlayerAccessDecision(PlayerAccessGrant Grant, string Reason);

public sealed class PlayerAccessService(PlayerAccessCatalog catalog, QuasarPlayerLinks links,
    PlayerAuthBrowser browser, DiscordPlayerRoles discord, DedicatedServerCatalog servers, ClusterCatalog clusters)
{
    public IReadOnlyList<PlayerAccessTarget> Targets() => servers.GetServers()
        .Select(s => new PlayerAccessTarget("server:" + s.UniqueName, s.DisplayName))
        .Concat(clusters.GetClusters().Select(c => new PlayerAccessTarget("cluster:" + c.UniqueName, c.DisplayName)))
        .OrderBy(t => t.Name).ToArray();

    public async Task<PlayerAccessDecision> EvaluateAsync(PlayerAccessPolicy policy, string steamId, CancellationToken token)
    {
        bool explicitlyAllowed = policy.AllowedSteamIds.Contains(steamId, StringComparer.Ordinal);
        PlayerAccessDecision Fallback(PlayerAccessDecision denied) => !explicitlyAllowed ? denied
            : new(new() { SteamId = ulong.Parse(steamId, CultureInfo.InvariantCulture), Allowed = true,
                Confirmed = true, ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(2) }, "Explicit Steam allowlist");
        var link = links.FindBySteamId(steamId);
        if (link is null) return Fallback(Denied(steamId, "Authenticate Steam and Discord on this website."));
        if (!browser.Ready) return Fallback(Denied(steamId, "Public player authentication is unavailable.", false));
        var membership = await discord.GetAsync(policy.GuildId, ulong.Parse(link.DiscordUserId, CultureInfo.InvariantCulture), token);
        var decision = Resolve(policy, steamId, membership, catalog.IsAuthorized(policy.Target, link.AuthenticationId!));
        return decision.Grant.Allowed ? decision : Fallback(decision);
    }

    internal static PlayerAccessDecision Resolve(PlayerAccessPolicy policy, string steamId, DiscordPlayerMembership membership, bool authorized)
    {
        if (!membership.Known || membership.ExpiresAt <= DateTimeOffset.UtcNow)
            return Denied(steamId, "Discord membership could not be verified. Try again shortly.", false);
        if (!membership.Member) return Denied(steamId, "Join the server's Discord guild.");
        if (policy.RequiredRoles.Count != 0 && !policy.RequiredRoles.Intersect(membership.Roles).Any())
            return Denied(steamId, "Your Discord account does not have a required access role.");
        if (!authorized)
            return Denied(steamId, "Review the instructions and authorize this server.");
        return new(new()
        {
            SteamId = ulong.Parse(steamId, CultureInfo.InvariantCulture), Allowed = true, Confirmed = true,
            Priority = policy.PriorityRoles.Intersect(membership.Roles).Any(),
            Promotion = policy.Promotions.Where(p => membership.Roles.Contains(p.RoleId)).Select(p => p.Level).DefaultIfEmpty(0).Max(),
            ExpiresAt = membership.ExpiresAt,
        }, "Authorized");
    }

    internal async Task AuthorizeAsync(PlayerAccessPolicy policy, string steamId, CancellationToken token)
    {
        if (!browser.Ready) throw new InvalidOperationException("Public player authentication is unavailable.");
        var link = links.FindBySteamId(steamId) ?? throw new InvalidOperationException("Authenticate both Steam and Discord first.");
        var member = await discord.GetAsync(policy.GuildId, ulong.Parse(link.DiscordUserId, CultureInfo.InvariantCulture), token);
        if (!member.Known || !member.Member || policy.RequiredRoles.Count != 0 && !member.Roles.Intersect(policy.RequiredRoles).Any())
            throw new InvalidOperationException("Discord membership and required access roles must be verified first.");
        await catalog.AuthorizeAsync(policy.Target, link.AuthenticationId!, token);
    }

    internal async Task<PlayerAccessSnapshot> BuildAsync(string target, CancellationToken token)
    {
        var settings = catalog.GetSettings();
        var policy = settings.Policies.FirstOrDefault(p => p.Target == target);
        var now = DateTimeOffset.UtcNow;
        var snapshot = new PlayerAccessSnapshot
        {
            Target = target, Revision = settings.Revision, IssuedAt = now, ExpiresAt = now.AddMinutes(2),
            Enabled = policy?.Enabled == true, Whitelist = policy?.Enabled == true && policy.Whitelist,
        };
        if (policy?.Enabled != true) return snapshot;
        foreach (var steamId in links.GetLinks().Where(l => l.Authenticated).Select(l => l.SteamId)
            .Concat(policy.AllowedSteamIds).Distinct(StringComparer.Ordinal))
            snapshot.Players.Add((await EvaluateAsync(policy, steamId, token)).Grant);
        return snapshot;
    }

    internal static PlayerAccessDecision Denied(string steamId, string reason, bool confirmed = true) => new(new()
    {
        SteamId = ulong.Parse(steamId, CultureInfo.InvariantCulture), Confirmed = confirmed,
        ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(2),
    }, reason);
}

/// <summary>Pushes expiring decisions even when no administrator has a browser open.</summary>
public sealed class PlayerAccessWorker(PlayerAccessService access, PlayerAccessCatalog catalog, AgentRegistry agents,
    ClusterCatalog clusters, ClusterGatewayClient gateway, ClusterHostClient hosts, PlayerAuthBrowser browser, ILogger<PlayerAccessWorker> logger) : BackgroundService
{
    private readonly Dictionary<string, string> status = new(StringComparer.Ordinal);
    private readonly object sync = new();
    private DateTimeOffset nextVerification;
    private readonly Dictionary<string, Delivery> delivered = new(StringComparer.Ordinal);
    public string Status(string target) { lock (sync) return status.GetValueOrDefault(target, "Waiting for the enforcement connection."); }

    protected override async Task ExecuteAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        try
        {
            do
            {
                var settings = catalog.GetSettings();
                if (settings.Policies.Any(p => p.Enabled) && settings.PublicUrl.Length != 0
                    && settings.VerifiedUrl == settings.PublicUrl && settings.VerifiedUntil < DateTimeOffset.UtcNow.AddDays(1)
                    && nextVerification <= DateTimeOffset.UtcNow)
                {
                    nextVerification = DateTimeOffset.UtcNow.AddHours(1);
                    try { await browser.VerifyAsync(token); }
                    catch (Exception error) when (!token.IsCancellationRequested)
                    { logger.LogWarning("Player authentication website revalidation failed: {Reason}", error.Message); }
                }
                foreach (var target in access.Targets())
                {
                    try
                    {
                        // Missing state is not authorization to open a previously enabled runtime whitelist.
                        if (!catalog.GetSettings().Policies.Any(p => p.Target == target.Key)) continue;
                        var snapshot = await access.BuildAsync(target.Key, token);
                        if (snapshot.Enabled && agents.GetAgents().Any(a => a.IsConnected
                            && (a.IsCluster ? "cluster:" + a.ClusterId : "server:" + a.UniqueNameKey) == target.Key
                            && a.Snapshot?.Plugins.Any(IsLegacyAuthentication) == true))
                            throw new InvalidOperationException("Remove the separate GameServerAuth plugin before enabling built-in player access.");
                        var fingerprint = JsonSerializer.Serialize(new
                        {
                            snapshot.Revision, snapshot.Enabled, snapshot.Whitelist,
                            Players = snapshot.Players.Select(p => new { p.SteamId, p.Allowed, p.Confirmed, p.Priority, p.Promotion }),
                        });
                        if (target.Key.StartsWith("cluster:", StringComparison.Ordinal))
                        {
                            if (!NeedsDelivery(snapshot, fingerprint, "gateway")) continue;
                            var cluster = clusters.GetClusters().Single(c => "cluster:" + c.UniqueName == target.Key);
                            await ApplyClusterAsync(cluster, snapshot, token);
                            delivered[target.Key] = new(fingerprint, DateTimeOffset.UtcNow, "gateway");
                        }
                        else
                        {
                            var current = agents.GetAgents().SingleOrDefault(a => !a.IsCluster && a.IsConnected && "server:" + a.UniqueNameKey == target.Key);
                            if (current is null) { Set(target.Key, "Server is not connected."); continue; }
                            if (current.Hello?.PlayerAccessVersion != PlayerAccessSnapshot.ProtocolVersion)
                            { Set(target.Key, "Restart the server to load the Agent with player access support."); continue; }
                            if (!NeedsDelivery(snapshot, fingerprint, current.ConnectionId)) continue;
                            var result = await agents.SendCommandAndWaitAsync(new()
                            {
                                AgentId = current.AgentId, ServerId = current.ServerKey, UniqueName = current.UniqueNameKey,
                                CommandType = ServerCommandType.UpdatePlayerAccess, Payload = JsonSerializer.Serialize(snapshot),
                            }, TimeSpan.FromSeconds(5), token);
                            if (!result.Success) throw new InvalidOperationException(result.Message);
                            delivered[target.Key] = new(fingerprint, DateTimeOffset.UtcNow, current.ConnectionId);
                            Set(target.Key, AppliedStatus(snapshot, "Player access policy applied."));
                        }
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                    catch (Exception error)
                    {
                        var message = "Enforcement update failed: " + error.Message;
                        if (Status(target.Key) != message) logger.LogWarning("Player access update failed for {Target}: {Reason}", target.Key, error.Message);
                        Set(target.Key, message);
                    }
                }
            } while (await timer.WaitForNextTickAsync(token));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private async Task ApplyClusterAsync(ClusterDefinition cluster, PlayerAccessSnapshot snapshot, CancellationToken token)
    {
        if (!snapshot.Enabled && !catalog.GetSettings().Policies.Any(p => p.Target == snapshot.Target)) return;
        var capabilities = (await gateway.GetCapabilitiesAsync(cluster, token)).Data;
        if (!capabilities.Operations.Contains("PUT /admin/v1/players/access", StringComparer.Ordinal))
            throw new InvalidOperationException("Update the cluster package to one supporting player access policies.");
        await clusters.WithLifecycleAsync(cluster.UniqueName, async current =>
        {
            await hosts.SyncPlayerAccessIntentAsync(current, token);
            return true;
        }, token);
        await gateway.SetPlayerAccessAsync(cluster, snapshot, token);
        Set(snapshot.Target, AppliedStatus(snapshot, "Cluster player access policy applied."));
    }

    private void Set(string target, string value) { lock (sync) status[target] = value; }
    private bool NeedsDelivery(PlayerAccessSnapshot policy, string fingerprint, string peer) =>
        !delivered.TryGetValue(policy.Target, out var previous) || previous.Fingerprint != fingerprint || previous.Peer != peer
        || policy.Enabled && previous.At <= DateTimeOffset.UtcNow.AddSeconds(-30);
    private sealed record Delivery(string Fingerprint, DateTimeOffset At, string Peer);
    private string AppliedStatus(PlayerAccessSnapshot snapshot, string success) => !snapshot.Enabled ? "Player access disabled."
        : browser.Ready ? success : "Policy applied; public player authentication is unavailable. Existing access restrictions remain.";
    internal static bool IsLegacyAuthentication(PluginRuntimeInfo plugin) => plugin.IsLoaded
        && (string.Equals(plugin.PluginId, "EC39AAEF-E46A-4291-A511-91ECE969D1D7", StringComparison.OrdinalIgnoreCase)
            || plugin.DisplayName.Contains("GameServerAuth", StringComparison.OrdinalIgnoreCase));
}
