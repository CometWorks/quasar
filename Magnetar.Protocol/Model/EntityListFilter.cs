namespace Magnetar.Protocol.Model;

/// <summary>
/// Request parameters for <see cref="Transport.ServerCommandType.ListEntities"/>.
/// Serialized as JSON into <see cref="Transport.ServerCommandEnvelope.Payload"/>.
/// </summary>
public class EntityListFilter
{
    /// <summary>"All" | "Grid" | "Character" | "Float" | "Voxel".</summary>
    public string TypeTag { get; set; } = "All";

    /// <summary>Free-text match against display name or entity id. Empty matches everything.</summary>
    public string Search { get; set; } = string.Empty;

    /// <summary>Maximum number of entities to return after filtering. Clamped server-side.</summary>
    public int Limit { get; set; } = 500;

    /// <summary>Number of matching entities to skip for paging.</summary>
    public int Offset { get; set; }
}
