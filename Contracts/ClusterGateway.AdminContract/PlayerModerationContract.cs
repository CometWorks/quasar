namespace CometWorks.ClusterGateway.AdminContract.V1;

/// <summary>Assignable game roles. Owner is deliberately not assignable.</summary>
public enum PlayerPromotionLevel { None, Scripter, Moderator, SpaceMaster, Admin }

public sealed record SetPlayerPromotionRequest(
    [property: System.Text.Json.Serialization.JsonRequired] PlayerPromotionLevel Level,
    [property: System.Text.Json.Serialization.JsonRequired] long ExpectedRevision);
public sealed record PlayerPromotion(ulong UserId, PlayerPromotionLevel Level, long Revision = 0);
public sealed record PlayerKickCooldown(ulong UserId, DateTimeOffset ExpiresAt);

/// <summary>Null Promotions means the current WA could not be queried; an empty map means
/// everyone has None. Cooldowns come from Gateway admission, including disconnected players.
/// Promotions persist in WA saves/journal; kick deadlines are transient across Gateway restart.</summary>
public sealed record PlayerModerationStatus(PlayerPromotion[]? Promotions, PlayerKickCooldown[] KickCooldowns,
    string? PromotionError = null);
