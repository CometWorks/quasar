namespace Magnetar.Protocol.Model;

/// <summary>
/// Payload of <see cref="Transport.ServerCommandType.ResolvePositionOwner"/>: asks a cluster
/// node whether it simulates the area around a position.
/// </summary>
public class PositionOwnerRequest
{
    public double X { get; set; }

    public double Y { get; set; }

    public double Z { get; set; }

    public double RadiusMeters { get; set; }
}

/// <summary>The node's nearest grid or character it owns within the radius, if any.</summary>
public class PositionOwnerResult
{
    public bool Found { get; set; }

    public double DistanceMeters { get; set; }

    public long EntityId { get; set; }
}
