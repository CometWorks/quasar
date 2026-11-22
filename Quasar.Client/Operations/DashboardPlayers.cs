namespace Quasar.Client.Operations;

public enum PlayerCommand { KickPlayer, BanPlayer, UnbanPlayer, SetPlayerPromoteLevel, ClearKickCooldown }
public sealed record PlayerFilterOption(string Value, string Label);
public sealed record BrowserPlayerRecord(string PlayerKey, string UniqueName, bool IsCluster, string ServerName,
    string SteamId, string DisplayName, string PlatformDisplayName, string ServiceName, string FactionTag, string PromoteLevel,
    bool IsAdmin, bool IsBanned, DateTimeOffset LastSeenUtc);
public sealed class KnownPlayerView
{
    public required BrowserPlayerRecord Record { get; init; }
    public required string ServerDisplayName { get; init; }
    public required string ServerDetail { get; init; }
    public required string[] ClusterActions { get; init; }
    public bool IsOnline { get; init; }
    public bool ConnectionAvailable { get; init; }
    public bool GatewaySupportsModeration { get; init; }
    public string? PromotionWarning { get; init; }
    public string? PromotionRevision { get; init; }
    public string? ModerationUnavailableReason { get; init; }
    public DateTimeOffset? KickCooldownExpiresUtc { get; init; }
    public string ServerFilterKey => (Record.IsCluster ? "cluster::" : "") + Record.UniqueName.Trim();
    public bool CanModerate => Record.IsCluster ? ClusterActions.Any(a => a is "kick" or "ban" or "unban") : ConnectionAvailable;
    public bool CanRunClusterAction(string action) => ClusterActions.Contains(action);
    public bool IsKickCooldownActive => KickCooldownExpiresUtc > DateTimeOffset.UtcNow;
    public int KickCooldownRemainingSeconds => KickCooldownExpiresUtc.HasValue ? Math.Max(0, (int)Math.Ceiling((KickCooldownExpiresUtc.Value - DateTimeOffset.UtcNow).TotalSeconds)) : 0;
    public override bool Equals(object? obj) => obj is KnownPlayerView other && StringComparer.OrdinalIgnoreCase.Equals(Record.PlayerKey, other.Record.PlayerKey);
    public override int GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(Record.PlayerKey);
}
public sealed record PlayerTable(KnownPlayerView[] Rows, PlayerFilterOption[] Filters, int RetentionDays, int MinDays, int MaxDays,
    bool CanControl, bool CanManageClusters);
public sealed record PlayerMutationResult(string Message, bool Failed = false);
