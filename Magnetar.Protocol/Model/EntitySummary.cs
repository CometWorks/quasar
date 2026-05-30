namespace Magnetar.Protocol.Model;

/// <summary>
/// A single live world entity as seen by the agent. Position, bounding box and the
/// full world matrix are flattened to plain doubles so the netstandard protocol
/// assembly stays free of any VRage math dependency. The world AABB and world-matrix
/// fields are captured up front so a future world-space renderer can consume this DTO
/// (oriented bounding boxes, not just axis-aligned ones) without a schema change.
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

    // Full world matrix (VRageMath.MatrixD), flattened row-major. Rows 1-3 carry the
    // orientation basis (Right / Up / Backward) and scale; row 4 (M41/M42/M43) is the
    // translation, equal to Position above. A future renderer needs this to draw
    // oriented bounding boxes; AABB alone only yields axis-aligned ones.
    public double WorldMatrixM11 { get; set; }

    public double WorldMatrixM12 { get; set; }

    public double WorldMatrixM13 { get; set; }

    public double WorldMatrixM14 { get; set; }

    public double WorldMatrixM21 { get; set; }

    public double WorldMatrixM22 { get; set; }

    public double WorldMatrixM23 { get; set; }

    public double WorldMatrixM24 { get; set; }

    public double WorldMatrixM31 { get; set; }

    public double WorldMatrixM32 { get; set; }

    public double WorldMatrixM33 { get; set; }

    public double WorldMatrixM34 { get; set; }

    public double WorldMatrixM41 { get; set; }

    public double WorldMatrixM42 { get; set; }

    public double WorldMatrixM43 { get; set; }

    public double WorldMatrixM44 { get; set; }
}
