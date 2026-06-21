using System;

namespace Magnetar.Protocol.Model;

public class ServerMetrics
{
    public int PlayersOnline { get; set; }

    public int MaxPlayers { get; set; }

    public ulong SimulationFrameCounter { get; set; }

    public float SimSpeed { get; set; }

    public float SimCpuLoadPercent { get; set; }

    public float ServerCpuLoadPercent { get; set; }

    public bool IsSaveInProgress { get; set; }

    public DateTimeOffset? LastWorldSaveUtc { get; set; }

    public long? UnsavedGameTimeSeconds { get; set; }

    public int UsedPcu { get; set; }

    public int TotalPcu { get; set; }

    public long? MemoryWorkingSetMb { get; set; }

    public int? ActiveGridCount { get; set; }

    public int? ActiveEntityCount { get; set; }

    public int? TotalBlockCount { get; set; }

    public int? FloatingObjectCount { get; set; }

    public int UptimeSeconds { get; set; }

    public int ModsLoaded { get; set; }

    public int PluginsLoaded { get; set; }
}
