using System;
using System.Collections.Generic;

namespace Magnetar.Protocol.Model;

/// <summary>
/// Response body for <see cref="Transport.ServerCommandType.ListEntities"/>.
/// Serialized as JSON into <see cref="Transport.ServerCommandResult.Payload"/>.
/// </summary>
public class EntityListResult
{
    /// <summary>The page of entities matching the filter, after offset/limit.</summary>
    public List<EntitySummary> Entities { get; set; } = new();

    /// <summary>Total entities matching the filter before paging.</summary>
    public int TotalCount { get; set; }

    /// <summary>Total live entities on the server before filtering.</summary>
    public int TotalEntityCount { get; set; }

    public DateTimeOffset CapturedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
