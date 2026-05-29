namespace Quasar.Models;

public sealed class KnownPlayerRecord
{
    public string PlayerKey { get; set; } = string.Empty;

    public string UniqueName { get; set; } = string.Empty;

    public string ServerId { get; set; } = string.Empty;

    public string ServerName { get; set; } = string.Empty;

    public string WorldName { get; set; } = string.Empty;

    public string NodeId { get; set; } = string.Empty;

    public string NodeName { get; set; } = string.Empty;

    public long SteamId { get; set; }

    public long IdentityId { get; set; }

    public int SerialId { get; set; }

    public string DisplayName { get; set; } = string.Empty;

    public string PlatformDisplayName { get; set; } = string.Empty;

    public string PlatformIcon { get; set; } = string.Empty;

    public string GameAcronym { get; set; } = string.Empty;

    public string ServiceName { get; set; } = string.Empty;

    public string FactionTag { get; set; } = string.Empty;

    public string PromoteLevel { get; set; } = string.Empty;

    public bool IsAdmin { get; set; }

    public bool IsBanned { get; set; }

    public int LastObservedPingMs { get; set; }

    public DateTimeOffset FirstSeenUtc { get; set; }

    public DateTimeOffset LastSeenUtc { get; set; }

    public DateTimeOffset? LastOnlineUtc { get; set; }
}
