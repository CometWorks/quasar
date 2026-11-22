namespace Quasar.Services.Players;

public sealed class PlayerAccessSettings
{
    public long Revision { get; set; }
    public string PublicUrl { get; set; } = "";
    // Retained for settings compatibility. Browser linking now uses the shared authentication service.
    public string DiscordClientId { get; set; } = "";
    public string DiscordClientSecret { get; set; } = "";
    public string? VerifiedUrl { get; set; }
    public DateTimeOffset? VerifiedUntil { get; set; }
    public List<PlayerAccessPolicy> Policies { get; set; } = [];
}

public sealed class PlayerAccessPolicy
{
    public string Target { get; set; } = "";
    public ulong GuildId { get; set; }
    public bool Enabled { get; set; }
    public bool Whitelist { get; set; }
    public string Instructions { get; set; } = "";
    public List<ulong> RequiredRoles { get; set; } = [];
    public List<ulong> PriorityRoles { get; set; } = [];
    public List<PlayerPromotionMapping> Promotions { get; set; } = [];
    public List<string> AllowedSteamIds { get; set; } = [];
}

public sealed record PlayerPromotionMapping(ulong RoleId, int Level);
public sealed record AccessTarget(string Key, string Name, string Enforcement);
public sealed record AccessRole(string Id, string Name);
public sealed record AccessDashboard(string Settings, string GuildId, bool Configured, bool GuildConnected, string GuildName, AccessRole[] Roles,
    AccessTarget[] Targets, bool Ready, string? PublicUrl);
public sealed record AccessRow(string Steam, string Discord, string Reason, int Promotion, bool Priority, DateTimeOffset ExpiresAt);
public sealed record LinkedPlayer(string SteamId, string DiscordUserId, bool Authenticated, DateTimeOffset LinkedAtUtc, string Name);
