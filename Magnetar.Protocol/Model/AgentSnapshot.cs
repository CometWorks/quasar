using System;
using System.Collections.Generic;

namespace Magnetar.Protocol.Model;

public class AgentSnapshot
{
    public string InstanceId { get; set; } = string.Empty;

    public string AgentId { get; set; } = string.Empty;

    public string NodeId { get; set; } = string.Empty;

    public string NodeName { get; set; } = string.Empty;

    public string ServerId { get; set; } = string.Empty;

    public string ServerName { get; set; } = string.Empty;

    public string WorldName { get; set; } = string.Empty;

    public bool IsRunning { get; set; }

    public DateTimeOffset CapturedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public ServerMetrics Metrics { get; set; } = new();

    public List<PlayerSnapshot> Players { get; set; } = new List<PlayerSnapshot>();

    public List<ChatMessageSnapshot> RecentChat { get; set; } = new List<ChatMessageSnapshot>();

    public List<PluginRuntimeInfo> Plugins { get; set; } = new List<PluginRuntimeInfo>();
}
