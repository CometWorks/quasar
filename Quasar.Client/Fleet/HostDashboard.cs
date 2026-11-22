namespace Quasar.Client.Fleet;

public sealed record InProcessHost(string HostName, int ServerCount, int ConnectedAgents, int PlayersOnline);
public sealed record HostRegistration(string Id, string Name, string Address);
public sealed record HostExecutor(string Identity, string HostName, string ExecutorId, bool Reachable,
    string[] Clusters, string[] Gateways, int Configured, int Attachments, string Error, HostRegistration[] Registrations);
public sealed record HostSnapshot(HostExecutor[] Executors, InProcessHost[] Agents, string[] Warnings, bool CanManage);
public sealed record EnrollmentSnapshot(HostRegistration[] Hosts, bool Connected, string[] Warnings);
public sealed record EnrollmentTicket(string Command, DateTimeOffset ExpiresAt);
public sealed record HostInstallInput(string Id, string Origin, string? SshAddress = null,
    string? SshUser = null, int SshPort = 22, string? IdentityFile = null);
