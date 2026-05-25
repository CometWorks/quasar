namespace Magnetar.Protocol.Model;

public class PlayerSnapshot
{
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

    public int PingMs { get; set; }
}
