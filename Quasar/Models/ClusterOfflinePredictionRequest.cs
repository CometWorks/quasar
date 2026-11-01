namespace Quasar.Models;

public sealed record ClusterOfflinePredictionRequest(string Node, ulong Partition, double Hours);
