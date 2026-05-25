using Magnetar.Protocol.Discovery;

namespace Quasar.Services;

public sealed class WebServiceState
{
    public WebServiceState(
        WebServiceOptions options,
        AgentRegistry registry,
        DedicatedServerInstanceCatalog instanceCatalog,
        DedicatedServerSupervisor supervisor)
    {
        Options = options;
        Registry = registry;
        InstanceCatalog = instanceCatalog;
        Supervisor = supervisor;
    }

    public WebServiceOptions Options { get; }

    public AgentRegistry Registry { get; }

    public DedicatedServerInstanceCatalog InstanceCatalog { get; }

    public DedicatedServerSupervisor Supervisor { get; }

    public WebServiceDiscoveryManifest CurrentManifest { get; set; } = new();
}
