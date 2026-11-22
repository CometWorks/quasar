using System.Text.Json.Serialization;

namespace Quasar.Client.Operations;

public sealed record EntityTarget(string Key, string DisplayName, string UniqueName, bool IsCluster);
public sealed record EntityTargets(EntityTarget[] Targets, bool CanControl, bool CanManageClusters, string[] LiveOwners);
public sealed record EntitySummary(
    [property: JsonNumberHandling(JsonNumberHandling.WriteAsString | JsonNumberHandling.AllowReadingFromString)] long EntityId,
    string DisplayName, string TypeTag, string? SubType, string Name, string Details, string TypeLabel,
    decimal? Quantity, int? BlockCount, int? Pcu, double SizeMeters, string? OwnerName,
    [property: JsonNumberHandling(JsonNumberHandling.WriteAsString | JsonNumberHandling.AllowReadingFromString)] ulong? OwnerSteamId,
    string Position);
public sealed record EntityQueryRow(EntitySummary Entity, string NodeLabel, string AgentId, string TelemetryKey,
    string? DeleteUnavailableReason, string[] Observers);
public sealed record EntityQueryResult(EntityQueryRow[] Rows, int TotalCount, int? TotalEntityCount, DateTimeOffset CapturedAtUtc,
    IReadOnlyList<string> Warnings, int QueriedNodes, int ExpectedNodes);
public enum EntityAction { Stop, Depower, PowerOn, RemoveOwner }
