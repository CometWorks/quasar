namespace Magnetar.Protocol.Model;

public class ServerMetrics
{
    public int PlayersOnline { get; set; }

    public int MaxPlayers { get; set; }

    public float SimSpeed { get; set; }

    public float SimCpuLoadPercent { get; set; }

    public float ServerCpuLoadPercent { get; set; }

    public int UsedPcu { get; set; }

    public int TotalPcu { get; set; }

    public int UptimeSeconds { get; set; }

    public int ModsLoaded { get; set; }

    public int PluginsLoaded { get; set; }
}
