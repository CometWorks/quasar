using System.Net;
using System.Text.Json;
using Quasar.Models;
using Admin = CometWorks.ClusterGateway.AdminContract.V1;

namespace Quasar.Services;

// Presentation data only. Registry health and incarnation matching remain authoritative.
internal static class ClusterOverview
{
    internal sealed record Node(string Name, string? Host, Admin.NodeRole Role, bool Spare,
        Admin.NodePlan? Plan, ClusterFleetNode? Fleet);
    internal sealed record ServiceEndpoint(string Process, string Service, string Address, string? Bind = null);

    internal static bool IsIdle(ClusterFleetNode? node) => node is
        { Registry: { Role: Admin.NodeRole.Regular, State: Admin.NodeState.Active, LeaseFresh: true },
          Telemetry: { Connected: true, TelemetryPaused: true } }
        && node.Registry.LeaseExpires > DateTimeOffset.UtcNow;

    internal static JsonDocument? Specification(ClusterDefinition cluster)
    {
        // A staged update may change ports or placement. Never describe it as the running deployment.
        var preparation = cluster.ActiveDeployment is { } active ? active.Provenance?.Preparation : cluster.Preparation;
        try { return preparation is null ? null : JsonDocument.Parse(preparation.SpecificationJson); }
        catch (JsonException) { return null; }
    }

    internal static int? GamePort(ClusterDefinition cluster)
    {
        using var spec = Specification(cluster);
        return spec?.RootElement is { ValueKind: JsonValueKind.Object } root
            && Text(root, "steamListen") is { } listen && IPEndPoint.TryParse(listen, out var endpoint) && endpoint.Port > 0
                ? endpoint.Port : null;
    }

    internal static Node[] Nodes(ClusterDefinition cluster, Admin.NodePlan[] plan, ClusterFleetNode[] fleet)
    {
        var rows = ClusterFleetService.MergeNodes(plan, fleet).Select(row => new Node(
            row.Plan?.SlotKey ?? row.Fleet!.Registry.SlotKey ?? row.Fleet.Registry.Node,
            row.Fleet?.Registry.Host ?? row.Plan?.Host,
            row.Fleet?.Registry.Role ?? row.Plan!.Role, row.Fleet?.Registry.Spare == true,
            row.Plan, row.Fleet)).ToList();
        using var spec = Specification(cluster);
        if (spec?.RootElement is { ValueKind: JsonValueKind.Object } root && root.TryGetProperty("nodes", out var nodes)
            && nodes.ValueKind == JsonValueKind.Array)
            foreach (var node in nodes.EnumerateArray())
            {
                string? slot = Text(node, "slotKey");
                if (slot is null) continue;
                bool spare = node.TryGetProperty("spare", out var value) && value.ValueKind == JsonValueKind.True;
                var index = rows.FindIndex(row => row.Name == slot);
                if (index >= 0)
                {
                    // The Registry can promote a spare. Use its current role when it is present.
                    if (rows[index].Fleet is null) rows[index] = rows[index] with { Spare = spare };
                }
                else rows.Add(new(slot, Text(node, "host"), Text(node, "role") is "WA" or "worldAuthority"
                    ? Admin.NodeRole.WorldAuthority : Admin.NodeRole.Regular, spare, null, null));
            }
        return rows.ToArray();
    }

    internal static ServiceEndpoint[] Endpoints(ClusterDefinition cluster, Node[] nodes, IReadOnlyList<EnrolledClusterHost> hosts)
    {
        var endpoints = new List<ServiceEndpoint>();
        using var spec = Specification(cluster);
        var root = spec?.RootElement;
        string? gatewayHost = root is { } json ? Text(json, "gatewayHost") : null;
        string? gatewayAddress = hosts.FirstOrDefault(h => h.Id == gatewayHost)?.Address;
        void Add(string process, string service, string? address, string? hostAddress = null)
        {
            if (string.IsNullOrWhiteSpace(address)) return;
            string? bind = null;
            if (IPEndPoint.TryParse(address, out var endpoint))
            {
                if ((endpoint.Address.Equals(IPAddress.Any) || endpoint.Address.Equals(IPAddress.IPv6Any))
                    && IPAddress.TryParse(hostAddress, out var physical))
                {
                    bind = endpoint.ToString();
                    endpoint = new(physical, endpoint.Port);
                }
                address = endpoint.ToString();
            }
            else if (Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
                address = uri.GetComponents(UriComponents.HostAndPort, UriFormat.SafeUnescaped);
            else return;
            endpoints.Add(new(process, service, address, bind));
        }
        Add("Gateway", "Cluster Registry administration", cluster.GatewayUrl);
        if (root is { ValueKind: JsonValueKind.Object } configuration)
        {
            Add("Gateway", "Cluster Registry administration", Text(configuration, "gatewayControl"));
            Add("Gateway", "Steam player connections", Text(configuration, "steamListen"), gatewayAddress);
            Add("Gateway", "Additional player connections", Text(configuration, "directListen"), gatewayAddress);
            if (configuration.TryGetProperty("nodes", out var configured) && configured.ValueKind == JsonValueKind.Array)
                foreach (var node in configured.EnumerateArray())
                {
                    string name = Text(node, "slotKey") ?? Text(node, "nodeId") ?? "Node";
                    Add(name, "Internal cluster connection", Text(node, "backend"));
                    Add(name, "Node management", Text(node, "control"));
                }
        }
        foreach (var node in nodes.Where(n => n.Fleet is not null))
        {
            Add(node.Name, "Internal cluster connection", node.Fleet!.Registry.Endpoint);
            Add(node.Name, "Node management", node.Fleet.Registry.ControlEndpoint);
        }
        var deployedHosts = cluster.ActiveDeployment?.Hosts;
        var hostIds = (deployedHosts?.Select(h => h.HostId) ?? nodes.Select(n => n.Host).OfType<string>()).ToHashSet();
        foreach (var host in hosts.Where(h => hostIds.Contains(h.Id)))
            Add(host.Name, "Host executor management", new IPEndPoint(IPAddress.Parse(host.Address), host.CommandPort).ToString());
        foreach (var host in deployedHosts ?? [])
            if (!hosts.Any(h => h.Id == host.HostId)) Add(host.HostId, "Host executor management", host.CommandUrl);
        if (deployedHosts is null) Add("Gateway host", "Host executor management", cluster.HostCommandUrl);
        return endpoints.DistinctBy(e => (e.Process, e.Service, e.Address)).ToArray();
    }

    private static string? Text(JsonElement value, string key) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(key, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString() : null;
}
