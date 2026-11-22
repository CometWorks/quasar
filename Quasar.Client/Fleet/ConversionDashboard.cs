namespace Quasar.Client.Fleet;

public sealed record ConversionSource(string? Server, string? Cluster, Guid? Id = null);
public sealed record ConversionDestination(string Id, string Name);
public sealed record ConversionDraft(string Cluster, bool CreateCluster, string ServerId, string Name, int Port, string Profile);
public sealed record ConversionProgress(string Phase, string? Destination, string? Error, bool Running);
public sealed record ConversionPageState(ConversionDestination[] Clusters, ConversionDestination[] Profiles,
    ConversionDraft Draft, string[] Warnings, string? ReviewError, ConversionProgress? Progress);
public sealed record ConversionSubmission(ConversionSource Source, ConversionDraft Draft, Guid Attempt);
