using System;
using System.Collections.Generic;

namespace Magnetar.Protocol.Model;

public class AgentSnapshot
{
    public string UniqueName { get; set; } = string.Empty;

    public string AgentId { get; set; } = string.Empty;

    public string HostId { get; set; } = string.Empty;

    public string HostName { get; set; } = string.Empty;

    public string ServerId { get; set; } = string.Empty;

    public string ServerName { get; set; } = string.Empty;

    public string WorldName { get; set; } = string.Empty;

    public bool IsRunning { get; set; }

    public DateTimeOffset CapturedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public ServerMetrics Metrics { get; set; } = new();

    public string ProfilerMode { get; set; } = string.Empty;

    public ProfilerSnapshot? Profiler { get; set; }

    public List<PlayerSnapshot> Players { get; set; } = new List<PlayerSnapshot>();

    public List<KickedPlayerSnapshot> KickedPlayers { get; set; } = new List<KickedPlayerSnapshot>();

    public List<ChatMessageSnapshot> RecentChat { get; set; } = new List<ChatMessageSnapshot>();

    public List<DeathEventSnapshot> RecentDeaths { get; set; } = new List<DeathEventSnapshot>();

    public List<PluginRuntimeInfo> Plugins { get; set; } = new List<PluginRuntimeInfo>();
}
