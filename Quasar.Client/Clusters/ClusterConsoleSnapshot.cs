namespace Quasar.Client.Clusters;

public sealed record ClusterConsoleSnapshot(ClusterEvent[] Events, long NextCursor, long EarliestRetained, DateTimeOffset Observed, ClusterLog[] Logs, string? GatewayError = null);
public sealed record ClusterEvent(long Seq, DateTimeOffset At, string Type, string? Actor, string? Target, string? Data);
public sealed record ClusterLog(string Node, DateTimeOffset TimestampUtc, string Plugin, string Level, string Message, string? Exception);
