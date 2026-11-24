using Quasar.Services;

namespace Quasar.Client.Fleet;

public sealed record FleetCounts(int ConfiguredServers, int RunningServers, int UnobservedClusters,
    int? PlayersOnline, int? PlayerCapacity, int ReportingServers, double? AverageSimSpeed,
    int SimulationProcesses, double? MemoryGiB, int MemoryProcesses, int HealthWarnings, int HealthErrors);
public sealed record FleetRow(string UniqueName, string DisplayName, bool IsCluster, string State, string Goal, int? Port,
    string Profile, string ProfileName, string WorldSaveName, string? Host, string? Address, bool Connected, int? Players,
    int? Capacity, float? SimSpeed, float? Cpu, long? MemoryMb, int? ProcessId, string Health, string Message,
    string? Error, string? ErrorLogKind, string[] Details, bool LaunchReady, bool Importable, bool Deployed, DateTimeOffset Revision)
{
    public string? GatewayUrl { get; init; }
    public string? Pcu { get; init; }
    public int? Mods { get; init; }
    public int? UptimeSeconds { get; init; }
    public FleetPluginLoad? Plugins { get; init; }
    public string? PriorityWarning { get; init; }
    internal string PlayerCountText => Players is int online
        ? Capacity is int capacity ? $"{online}/{capacity}" : online.ToString()
        : "—";
    internal string MemoryText => MemoryMb is long mb ? $"{mb / 1024d:0.0} GiB" : "—";
    public bool Active => State is "Running" or "Starting" or "Stopping" or "Restarting";
}
public sealed record FleetPluginLoad(int Loaded, int? Configured, string? Warning);
public sealed record FleetProfile(string Id, string Name, string WorldTemplateId);
public sealed record FleetActivity(DateTimeOffset At, string DisplayName, string Message, string Level, FleetOperation? ClusterDetails);
public sealed record FleetOperation(string OperationId, string Kind, DateTimeOffset StartedAt, string? ErrorCode);
public sealed record FleetDashboard(FleetCounts Counts, FleetRow[] Rows, FleetAgent[] UnmanagedAgents, Dictionary<string, string> Sources,
    FleetActivity[] Activity, WorldTemplate[] Templates, FleetProfile[] Profiles, ManagedRuntimeWarmupSnapshot Warmup,
    bool HealthDisabled, bool ConsentNeeded, bool Linux, bool EditServers, bool EditConfigs, bool Control, bool ClusterManage, bool ClusterQuery);
public sealed record FleetAgent(string Id, string UniqueName, string Name, string Host, string? Cluster,
    string Slot, string Node, long Epoch, bool Connected, bool Running);
public sealed record FleetLiveRequest(string UniqueName, string? AgentId = null);
public sealed record FleetTarget(string UniqueName, DateTimeOffset Revision);
public sealed record FleetPaths(string Directory, string World);
public sealed record FleetTemplateInput(string Name, string Description);
public sealed record FleetLive(FleetPlayer[] Players, FleetChat[] Chat, string[] Results, bool Saving, DateTimeOffset? LastSave, long? UnsavedSeconds);
public sealed record FleetPlayer(string SteamId, string Name, string Platform, string Service, string Faction, string Role, int Ping);
public sealed record FleetChat(DateTimeOffset? At, string Author, string Channel, string Content);
