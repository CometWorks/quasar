namespace Magnetar.Protocol.Model;

public sealed class DeathEventSnapshot
{
    public string VictimName { get; set; } = string.Empty;

    public string? KillerName { get; set; }

    public string? WeaponName { get; set; }

    public string DeathType { get; set; } = "Unknown";

    public long TimestampTicksUtc { get; set; }
}
