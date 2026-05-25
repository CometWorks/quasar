using System;

namespace Magnetar.Protocol.Model;

public class AgentHello
{
    public string InstanceId { get; set; } = string.Empty;

    public string AgentId { get; set; } = string.Empty;

    public string NodeId { get; set; } = string.Empty;

    public string NodeName { get; set; } = string.Empty;

    public string ServerId { get; set; } = string.Empty;

    public string ServerName { get; set; } = string.Empty;

    public string WorldName { get; set; } = string.Empty;

    public string PluginId { get; set; } = string.Empty;

    public string PluginVersion { get; set; } = string.Empty;

    public int ProcessId { get; set; }

    public string ProcessName { get; set; } = string.Empty;

    public string GameVersion { get; set; } = string.Empty;

    public DateTimeOffset ConnectedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
