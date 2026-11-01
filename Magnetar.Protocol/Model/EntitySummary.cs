namespace Magnetar.Protocol.Model;

/// <summary>
/// A single live world entity as seen by the agent. Position and bounding box
/// values are flattened to plain doubles so the netstandard protocol assembly
/// stays free of any VRage math dependency.
/// </summary>
public class EntitySummary
{
    public long EntityId { get; set; }

    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Transport identifiers: "Grid" | "Character" | "Float" | "Voxel" | "Other".</summary>
    public string TypeTag { get; set; } = string.Empty;

    /// <summary>Raw kind or item definition subtype, e.g. "LargeStatic", "Player", "Asteroid", "SteelPlate".</summary>
    public string SubType { get; set; } = string.Empty;

    /// <summary>Quantity of a floating item stack; null for other entities or when unavailable.</summary>
    public decimal? ItemAmount { get; set; }

    /// <summary>Cluster placement: Local, Frozen, Unowned, Absent or Unknown. Null on older agents and standalone servers.</summary>
    public string? ClusterResidence { get; set; }

    public int? BlockCount { get; set; }

    public int? Pcu { get; set; }

    public ulong? OwnerSteamId { get; set; }

    public string OwnerName { get; set; } = string.Empty;

    public double PositionX { get; set; }

    public double PositionY { get; set; }

    public double PositionZ { get; set; }

    public double AabbMinX { get; set; }

    public double AabbMinY { get; set; }

    public double AabbMinZ { get; set; }

    public double AabbMaxX { get; set; }

    public double AabbMaxY { get; set; }

    public double AabbMaxZ { get; set; }

    /// <summary>Largest world-AABB dimension in metres, a convenience for sizing/sorting.</summary>
    public double SizeMeters { get; set; }
}
