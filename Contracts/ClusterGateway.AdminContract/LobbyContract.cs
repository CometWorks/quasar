using System.Text.Json.Serialization;

namespace CometWorks.ClusterGateway.AdminContract.V1;

public sealed record LobbyPosition(
    [property: JsonRequired] double X,
    [property: JsonRequired] double Y,
    [property: JsonRequired] double Z
);

/// <summary>Set GridId and Partition to adopt a station, or BlueprintXml and Position to create one.</summary>
public sealed record LobbyUpdate(
    [property: JsonRequired] long ExpectedRevision,
    long? GridId = null,
    ulong? Partition = null,
    string? BlueprintXml = null,
    LobbyPosition? Position = null
)
{
    public string? ValidationError()
    {
        if (ExpectedRevision < 0) return "A nonnegative revision is required.";
        if (BlueprintXml is null && Position is null && (GridId is null or > 0) && Partition > 0) return null;
        if (GridId.HasValue || Partition.HasValue || string.IsNullOrWhiteSpace(BlueprintXml) || Position is null)
            return "Choose a station grid and partition or supply a blueprint and position.";
        if (!double.IsFinite(Position.X) || !double.IsFinite(Position.Y) || !double.IsFinite(Position.Z))
            return "World coordinates must be finite metres.";
        return System.Text.Encoding.UTF8.GetByteCount(BlueprintXml) <= MaxBlueprintBytes ? null : "Blueprint exceeds 1 MiB.";
    }

    public const int MaxBlueprintBytes = 1024 * 1024;
}

public sealed record LobbyAnchor(long GridId, ulong Partition, string Name, LobbyPosition Position);

public sealed record LobbyStatus(long Revision, LobbyAnchor? Anchor, string? Owner, string State, string? PendingOperation = null);
