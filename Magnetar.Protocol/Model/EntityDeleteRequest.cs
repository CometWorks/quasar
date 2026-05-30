namespace Magnetar.Protocol.Model;

/// <summary>
/// Request body for <see cref="Transport.ServerCommandType.DeleteEntity"/>.
/// Serialized as JSON into <see cref="Transport.ServerCommandEnvelope.Payload"/>.
/// </summary>
public class EntityDeleteRequest
{
    public long EntityId { get; set; }
}
