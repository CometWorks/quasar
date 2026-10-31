namespace CometWorks.ClusterGateway.AdminContract.V1;

/// <summary>Available only with capabilities.features.offlinePartitions == "v1".</summary>
public sealed record OfflinePartitionCatalog(OfflinePartitionSummary[] Partitions);
public sealed record OfflinePartitionSummary(ulong Partition, string SaveId, string OfflineBy,
    DateTimeOffset OfflineAt, long? OfflineGameFrame, long EstimatedSnapshotBytes,
    string? LoadRequestedNode, string? LoadReason, string[] Holders, OfflineGroupSummary[] Groups);
public sealed record OfflineGroupSummary(long Group, long[] Members, string? State, string? Confidence,
    string[] Unknowns, long? FrozenFrame, long? SimulatedThrough, OfflineObservation[] Observations);
public sealed record OfflineObservation(long Block, int Samples, double[] Mean, double[] Minimum,
    double[] Maximum, double[] Variance);
public sealed record OfflinePartitionDetails(string Node, long Epoch, ulong Partition, bool Offline,
    string? SaveId, OfflineGroupSummary[] Groups, OfflineCompensationResult[] Results);
public sealed record OfflinePrediction(string Node, long Epoch, ulong Partition, string SaveId,
    double Hours, OfflineCompensationResult[] Results);
public sealed record OfflineCacheView(string Node, long Epoch, OfflineRateEntry[] Entries);
public sealed record OfflineRateEntry(string Key, double Formula, double Rate, int Samples,
    double Spread, bool Scripted, int Epoch = 0);
public sealed record OfflineCacheClearRequest(string Key,
    [property: System.Text.Json.Serialization.JsonRequired] int ExpectedEpoch);
public sealed record OfflineZoneResult(long Block, bool Exists, bool Activating, bool Exhausted,
    double RemainingFrames, double ActivationRemainingFrames);
public sealed record OfflineCompensationResult(long Group, long FromFrame, long CompensatedTo,
    long Frames, int Events, bool Capped, string? Confidence, string? Skipped, double EnergyDelta,
    IReadOnlyDictionary<string, decimal> ItemDelta,
    IReadOnlyDictionary<long, double> ProducerEnergyMWh,
    IReadOnlyDictionary<long, double> ConsumerEnergyMWh,
    IReadOnlyDictionary<long, double> StorageInputEnergyMWh,
    IReadOnlyDictionary<long, Dictionary<string, decimal>> ProducerFuelConsumed,
    IReadOnlyDictionary<long, double> ProductionUnits,
    IReadOnlyDictionary<long, decimal> UpkeepConsumed, OfflineZoneResult[] Zones, long[] DisabledZones);
