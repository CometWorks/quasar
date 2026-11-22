namespace Quasar.Client.Fleet;

public sealed record AgentPlugin(string Id, string Name);
public sealed record ConfigAgent(string Id, string ConnectionId, string Name, string HostName, string? ClusterId, AgentPlugin[] Plugins);
public sealed record LivePlugins(ConfigAgent[] Agents, bool CanEdit, long ConfigRevision, long LogRevision);
public sealed record PluginSelection(string AgentId, string ConnectionId, string PluginId);
public sealed record PluginLogRequest(string Server, string Plugin, string Level, string Text, string Range, int Limit,
    int Page, int PageSize, string? Sort, bool Descending);
public sealed record PluginLogLine(DateTimeOffset TimestampUtc, string Server, string Level, string Plugin, string Message, string? Exception);
public sealed record PluginLogResult(PluginLogLine[] Lines, int Total, ConversionDestination[] Servers, string[] Plugins);
