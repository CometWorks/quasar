namespace Magnetar.Protocol.Model;

public class KickedPlayerSnapshot
{
    public long SteamId { get; set; }

    public string DisplayName { get; set; } = string.Empty;

    public int RemainingCooldownMs { get; set; }
}
