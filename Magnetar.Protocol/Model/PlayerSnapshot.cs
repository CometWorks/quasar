namespace Magnetar.Protocol.Model;

public class PlayerSnapshot
{
    public long SteamId { get; set; }

    public string DisplayName { get; set; } = string.Empty;

    public string FactionTag { get; set; } = string.Empty;

    public bool IsAdmin { get; set; }

    public int PingMs { get; set; }
}
