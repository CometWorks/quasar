namespace Magnetar.Protocol.Model;

/// <summary>
/// The Alt-F10 Entity List row actions Quasar can run, besides deletion.
/// </summary>
public enum EntityAction
{
    /// <summary>Zero the linear and angular velocity of the entity, and of every grid physically connected to a grid.</summary>
    Stop = 0,

    /// <summary>Turn off the power producers of a grid and the grids logically connected to it.</summary>
    Depower = 1,

    /// <summary>Turn the power producers of a grid and its logically connected grids back on.</summary>
    PowerOn = 2,

    /// <summary>Give every block of a grid and of its mechanically connected subgrids to nobody.</summary>
    RemoveOwner = 3,
}

/// <summary>
/// Request body for <see cref="Transport.ServerCommandType.EntityAction"/>.
/// Serialized as JSON into <see cref="Transport.ServerCommandEnvelope.Payload"/>.
/// </summary>
public class EntityActionRequest
{
    public long EntityId { get; set; }

    public EntityAction Action { get; set; }
}
