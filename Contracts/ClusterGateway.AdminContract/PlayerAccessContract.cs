namespace CometWorks.ClusterGateway.AdminContract.V1;

/// <summary>Expiring, installation-owned Discord decisions. A stale enabled whitelist denies new joins.</summary>
public sealed record PlayerAccessPolicy(int Version, string Target, long Revision, DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt, bool Enabled, bool Whitelist, PlayerAccessGrant[] Players);
public sealed record PlayerAccessGrant(ulong SteamId, bool Allowed, bool Confirmed, bool Priority,
    int Promotion, DateTimeOffset ExpiresAt);
public sealed record PlayerAccessStatus(bool Enabled, bool Whitelist, long Revision, DateTimeOffset ExpiresAt);
