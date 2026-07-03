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

    /// <summary>"Grid" | "Character" | "Float" | "Voxel" | "Other".</summary>
    public string TypeTag { get; set; } = string.Empty;

    /// <summary>e.g. "LargeStatic" | "LargeShip" | "SmallShip" | "Player" | "Bot" | "Asteroid".</summary>
    public string SubType { get; set; } = string.Empty;

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
