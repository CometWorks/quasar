using Magnetar.Protocol.Discovery;

namespace Quasar.Services;

public sealed class WebServiceState
{
    public WebServiceState(
        WebServiceOptions options,
        AgentRegistry registry,
        DedicatedServerCatalog serverCatalog,
        DedicatedServerSupervisor supervisor)
    {
        Options = options;
        Registry = registry;
        ServerCatalog = serverCatalog;
        Supervisor = supervisor;
    }

    public WebServiceOptions Options { get; }

    public AgentRegistry Registry { get; }

    public DedicatedServerCatalog ServerCatalog { get; }

    public DedicatedServerSupervisor Supervisor { get; }

    public WebServiceDiscoveryManifest CurrentManifest { get; set; } = new();
}
