using System.Text.Json;
using Bunit;
using Quasar.Components.Dashboard;
using Quasar.Host.Contract.V1;
using Quasar.Models;
using Quasar.Services;
using Magnetar.Protocol.Model;
using Admin = CometWorks.ClusterGateway.AdminContract.V1;
using Xunit;

namespace Quasar.Tests;

public sealed class ClusterHealthOverviewTests
{
    [Fact]
    public async Task PlayerRoutesUpdateOnAssignmentMovesAndDisconnectsWithoutGuessingMissingRoutes()
    {
        await using var context = new BunitContext();
        var fixture = Sample();
        var status = fixture.Status with { Counts = fixture.Status.Counts with { ConnectedClients = 1 } };
        var player = fixture.Clients[0] with { Node = null };
        var panel = context.Render<ClusterHealthOverview>(p => p.Add(c => c.Cluster, fixture.Cluster)
            .Add(c => c.Status, status).Add(c => c.Fleet, fixture.Fleet).Add(c => c.Clients, [player]));
        string Routed(string node) => panel.Find($"article[data-node='{node}']").ParentElement!
            .QuerySelector(".cluster-edge-label strong")!.TextContent;
        Assert.Equal("— routed players", Routed("node-1"));
        Assert.Contains("not reported for 1 connected player", panel.Markup);

        panel.Render(p => p.Add(c => c.Clients, [player with { Node = "node-1" }]));
        Assert.Equal("1 routed players", Routed("node-1"));
        Assert.Equal("0 routed players", Routed("node-2"));
        Assert.Empty(panel.FindAll(".cluster-routing-status"));
        panel.Render(p => p.Add(c => c.Clients, [player with { Node = "node-2" }]));
        Assert.Equal("0 routed players", Routed("node-1"));
        Assert.Equal("1 routed players", Routed("node-2"));
        Assert.Contains("stroke-width:1.1", panel.FindAll(".cluster-player-links path")[1].GetAttribute("style"));

        // One known route and one missing route: report a minimum, never a misleading exact zero.
        var known = player with { Node = "node-2" };
        panel.Render(p => p.Add(c => c.Status, status with { Counts = status.Counts with { ConnectedClients = 2 } })
            .Add(c => c.Clients, [known, known, player with { ClientId = 9999 }]));
        Assert.Equal("1+ routed players", Routed("node-2"));
        Assert.Equal("— routed players", Routed("node-1"));
        panel.Render(p => p.Add(c => c.Status, status with { Counts = status.Counts with { ConnectedClients = 0 } })
            .Add(c => c.Clients, []));
        Assert.Equal("0 routed players", Routed("node-2"));
        Assert.Empty(panel.FindAll(".cluster-routing-status"));
    }

    [Fact]
    public async Task LiveMapShowsRoutingSparesResourcesAndPhysicalServiceAddresses()
    {
        await using var context = new BunitContext();
        var fixture = Sample();
        var panel = context.Render<ClusterHealthOverview>(p => p
            .Add(c => c.Cluster, fixture.Cluster).Add(c => c.Status, fixture.Status)
            .Add(c => c.Fleet, fixture.Fleet).Add(c => c.Host, fixture.Host)
            .Add(c => c.Clients, fixture.Clients).Add(c => c.Partitions, fixture.Partitions)
            .Add(c => c.Hosts, fixture.Hosts));

        Assert.Equal(5, panel.FindAll(".cluster-game-nodes .cluster-process").Count);
        Assert.Single(panel.FindAll(".cluster-authority .cluster-process"));
        Assert.Single(panel.FindAll(".cluster-spare-label"));
        Assert.Equal(28, panel.FindAll(".cluster-cpu-bars > span").Count);
        Assert.Contains("553%", panel.Find(".cluster-health-totals").TextContent);
        Assert.Contains("42.4 GB", panel.Find(".cluster-health-totals").TextContent);
        Assert.Contains("62", panel.Find(".cluster-health-totals").TextContent);
        Assert.Contains("119", panel.Find(".cluster-health-totals").TextContent);
        Assert.Contains("18 routed players", panel.Markup);
        Assert.Contains("34 partitions", panel.Markup);
        Assert.Contains("0 routed players", panel.Markup);
        Assert.Contains("Host: Simulation host", panel.Markup);
        Assert.Contains("192.168.1.10:27016", panel.Markup);
        Assert.Contains("192.168.1.11:18400", panel.Markup);
        Assert.Contains("192.168.1.10:28417", panel.Markup);
        Assert.Contains("Additional player connections", panel.Markup);
        Assert.Contains("Internal cluster connection", panel.Markup);
        Assert.Contains("Internal node link · registered", panel.Markup);
        Assert.DoesNotContain("Direct Transport", panel.Markup);
        Assert.DoesNotContain(" DT", panel.Markup);

        // Export actual component markup for offline layout checks, without launching Quasar.
        if (Environment.GetEnvironmentVariable("QUASAR_CLUSTER_PREVIEW_OUTPUT") is { Length: > 0 } preview)
            await File.WriteAllTextAsync(preview, $"<div class=\"quasar-preview\" data-theme=\"dark\" style=\"{QuasarTheme.BuildCss(ThemePalette.QuasarDark(), true)}\">{panel.Markup}</div>");

        panel.Render(p => p.Add(c => c.Status, null));
        Assert.DoesNotContain("553%", panel.Markup);
        Assert.DoesNotContain("18 routed players", panel.Markup);
        Assert.Contains("Unobserved", panel.Markup);
        Assert.Contains("— routed players", panel.Markup);
    }

    [Fact]
    public async Task StaleResourcesAndFailedPartialQueriesAreNotShownAsZeroOrIncludedInTotals()
    {
        await using var context = new BunitContext();
        var fixture = Sample();
        var nodes = fixture.Fleet.Nodes.Select(n => n with { Telemetry = n.Telemetry! with { Connected = false } }).ToArray();
        var host = fixture.Host with { Gateways = [fixture.Host.Gateways![0] with {
            Metrics = new(DateTimeOffset.UtcNow.AddMinutes(-1), 800, 100L * 1073741824) }] };
        var panel = context.Render<ClusterHealthOverview>(p => p
            .Add(c => c.Cluster, fixture.Cluster).Add(c => c.Status, fixture.Status)
            .Add(c => c.Fleet, new ClusterFleet("sample", nodes)).Add(c => c.Host, host));
        Assert.Empty(panel.FindAll(".cluster-cpu-bars"));
        Assert.Contains("0/7 processes report RAM", panel.Markup);
        Assert.DoesNotContain("0 routed players", panel.Markup);
        Assert.DoesNotContain("0 partitions", panel.Markup);
        Assert.Contains("— routed players", panel.Markup);
        Assert.DoesNotContain("100.0 GB", panel.Markup);
        Assert.Contains("62", panel.Find(".cluster-health-totals").TextContent);
    }

    [Fact]
    public async Task IdleNodesHideCpuAndSimSpeedUntilTheyResumeWithoutMisclassifyingSlowActiveNodes()
    {
        await using var context = new BunitContext();
        var fixture = Sample();
        var nodes = fixture.Fleet.Nodes.ToArray();
        foreach (var index in new[] { 1, 4 })
            nodes[index] = nodes[index] with { Telemetry = nodes[index].Telemetry! with {
                TelemetryPaused = true, Metrics = new ServerMetrics { SimSpeed = .07f,
                    ServerCpuLoadPercent = index == 1 ? 63 : 4, MemoryWorkingSetMb = 1024 } } };
        // Low sim speed alone is a performance reading, not an idle/sleep signal.
        nodes[0] = nodes[0] with { Telemetry = nodes[0].Telemetry! with {
            Metrics = new ServerMetrics { SimSpeed = .07f, ServerCpuLoadPercent = 142, MemoryWorkingSetMb = 2048 } } };
        // World Authority has no regular-node partition workload and must not become "Idle".
        nodes[5] = nodes[5] with { Telemetry = nodes[5].Telemetry! with { TelemetryPaused = true } };
        var panel = context.Render<ClusterHealthOverview>(p => p.Add(c => c.Cluster, fixture.Cluster)
            .Add(c => c.Status, fixture.Status).Add(c => c.Fleet, fixture.Fleet with { Nodes = nodes }).Add(c => c.Host, fixture.Host));
        var ordinary = panel.Find("article[data-node='node-2']");
        var spare = panel.Find("article[data-node='node-5']");
        foreach (var card in new[] { ordinary, spare })
        {
            Assert.Equal("Idle", card.QuerySelector(".cluster-health-badge")!.TextContent);
            Assert.Contains("Waiting for work", card.TextContent);
            Assert.Contains("1.0 GB RAM", card.TextContent);
            Assert.Contains("CPU chart paused while idle", card.TextContent);
            Assert.DoesNotContain("Simulation speed", card.TextContent);
            Assert.Empty(card.QuerySelectorAll(".cluster-cpu-bars"));
        }
        Assert.Contains("Hot spare", spare.TextContent);
        var slowActive = panel.Find("article[data-node='node-1']");
        Assert.Equal("Active", slowActive.QuerySelector(".cluster-health-badge")!.TextContent);
        Assert.Contains("Simulation speed 0.07", slowActive.TextContent);
        Assert.Single(slowActive.QuerySelectorAll(".cluster-cpu-bars"));
        Assert.Single(panel.Find("article[data-node='world-authority']").QuerySelectorAll(".cluster-cpu-bars"));
        Assert.Contains("553%", panel.Find(".cluster-health-totals").TextContent); // Real idle overhead still costs CPU.

        nodes[1] = nodes[1] with { Telemetry = nodes[1].Telemetry! with { TelemetryPaused = false } };
        panel.Render(p => p.Add(c => c.Fleet, fixture.Fleet with { Nodes = nodes }));
        ordinary = panel.Find("article[data-node='node-2']");
        Assert.Equal("Active", ordinary.QuerySelector(".cluster-health-badge")!.TextContent);
        Assert.Single(ordinary.QuerySelectorAll(".cluster-cpu-bars"));
        Assert.Contains("Simulation speed", ordinary.TextContent);

        nodes[4] = nodes[4] with { Telemetry = nodes[4].Telemetry! with { Connected = false } };
        panel.Render(p => p.Add(c => c.Fleet, fixture.Fleet with { Nodes = nodes }));
        Assert.Empty(panel.FindAll("article[data-activity='idle']"));
        Assert.Contains("Live process telemetry unavailable", panel.Find("article[data-node='node-5']").TextContent);
    }

    [Fact]
    public void IdleNeedsCurrentRegularNodeTelemetryAndAValidLease()
    {
        var node = Sample().Fleet.Nodes[0];
        Assert.False(ClusterOverview.IsIdle(node));
        node = node with { Telemetry = node.Telemetry! with { TelemetryPaused = true } };
        Assert.True(ClusterOverview.IsIdle(node));
        Assert.False(ClusterOverview.IsIdle(node with { Registry = node.Registry with { LeaseFresh = false } }));
        Assert.False(ClusterOverview.IsIdle(node with { Registry = node.Registry with { LeaseExpires = DateTimeOffset.UtcNow.AddSeconds(-1) } }));
        Assert.False(ClusterOverview.IsIdle(node with { Registry = node.Registry with { State = Admin.NodeState.Dead } }));
        Assert.False(ClusterOverview.IsIdle(node with { Telemetry = node.Telemetry with { Connected = false } }));
        Assert.False(ClusterOverview.IsIdle(node with { Registry = node.Registry with { Role = Admin.NodeRole.WorldAuthority } }));
    }

    [Fact]
    public void ServicesUseActiveSpecificationAndStripCredentialsFromUrls()
    {
        var fixture = Sample();
        var cluster = fixture.Cluster;
        cluster.ActiveDeployment = new("active", [], DateTimeOffset.UtcNow) { Provenance = new(null!, "hash", cluster.Preparation!) };
        cluster.Preparation = cluster.Preparation! with { SpecificationJson = cluster.Preparation.SpecificationJson.Replace("27016", "49000") };
        cluster.GatewayUrl = "https://user:password@registry.example:27032/admin?token=secret";
        var nodes = ClusterOverview.Nodes(cluster, [], fixture.Fleet.Nodes);
        var endpoints = ClusterOverview.Endpoints(cluster, nodes, fixture.Hosts);
        Assert.Equal(27016, ClusterOverview.GamePort(cluster));
        Assert.Contains(endpoints, e => e.Address == "192.168.1.10:27016");
        Assert.DoesNotContain(endpoints, e => e.Address.Contains("49000"));
        Assert.Contains(endpoints, e => e.Address == "registry.example:27032");
        Assert.DoesNotContain(endpoints, e => e.Address.Contains("password") || e.Address.Contains("secret"));
        Assert.Equal(endpoints.Length, endpoints.DistinctBy(e => (e.Process, e.Service, e.Address)).Count());
        var ipv6 = fixture.Cluster.Clone();
        ipv6.ActiveDeployment = null;
        ipv6.Preparation = new("hash", """{"gatewayHost":"v6","directListen":"[::]:27116"}""", "", []);
        Assert.Contains(ClusterOverview.Endpoints(ipv6, [], [new("v6", "IPv6", "fd00::1", 18400, "credential")]),
            e => e.Address == "[fd00::1]:27116" && e.Bind == "[::]:27116");
    }

    [Fact]
    public void ConfiguredMissingSlotsRemainVisibleAndRegistryPromotionOverridesConfiguredSpare()
    {
        var fixture = Sample();
        var empty = ClusterOverview.Nodes(fixture.Cluster, [], []);
        Assert.Equal(6, empty.Length);
        Assert.Single(empty.Where(n => n.Spare));
        var promoted = fixture.Fleet.Nodes[^2] with { Registry = fixture.Fleet.Nodes[^2].Registry with { Spare = false } };
        Assert.DoesNotContain(ClusterOverview.Nodes(fixture.Cluster, [], [promoted]), n => n.Spare);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"steamListen\":\"0.0.0.0:0\"}")]
    [InlineData("{\"steamListen\":\"bad address\"}")]
    public void UnknownGamePortNeverFallsBackToTheRegistryAdministrationPort(string specification)
    {
        var cluster = new ClusterDefinition { GatewayUrl = "http://registry:28016", Preparation = new("hash", specification, "", []) };
        Assert.Null(ClusterOverview.GamePort(cluster));
        cluster.Preparation = null;
        Assert.Null(ClusterOverview.GamePort(cluster));
    }

    [Theory]
    [InlineData(553, true)]
    [InlineData(0, true)]
    [InlineData(double.NaN, false)]
    [InlineData(double.PositiveInfinity, false)]
    [InlineData(-1, false)]
    public async Task CpuBarsAreBoundedAndRetainTheActualTotal(double cpu, bool available)
    {
        await using var context = new BunitContext();
        var panel = context.Render<ClusterResourceUsage>(p => p.Add(c => c.CpuPercent, cpu));
        Assert.Equal(available ? 4 : 0, panel.FindAll(".cluster-cpu-bars > span").Count);
        if (cpu == 553)
        {
            Assert.Contains("553% CPU", panel.Markup);
            Assert.Contains("+1.5 cores", panel.Markup);
            Assert.All(panel.FindAll("i"), bar => Assert.Equal("height:100%", bar.GetAttribute("style")));
        }
    }

    internal sealed record Fixture(ClusterDefinition Cluster, Admin.ClusterStatus Status, ClusterFleet Fleet,
        HostStatus Host, Admin.ClientSummary[] Clients, Admin.PartitionSummary[] Partitions, EnrolledClusterHost[] Hosts);
    internal static Fixture Sample()
    {
        int[] players = [18, 7, 24, 13, 0, 0], partitions = [34, 12, 47, 26, 0, 0];
        double[] ram = [9.6, 5.1, 12.8, 7.9, 2.4, 3.4];
        float[] cpu = [142, 63, 188, 97, 4, 21];
        var nodes = Enumerable.Range(0, 6).Select(i => new Admin.NodeStatus(i == 5 ? "world-authority" : $"node-{i + 1}",
            i == 5 ? "world-authority" : $"slot-{i + 1}", 1, i == 5 ? Admin.NodeRole.WorldAuthority : Admin.NodeRole.Regular,
            Admin.NodeState.Active, $"192.168.1.10:{28417 + i}", $"192.168.1.10:{28517 + i}", DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddMinutes(1), 0, 0, i % 2 == 0 || i == 5 ? "host-a" : "host-b", true, i == 4)).ToArray();
        var specification = JsonSerializer.Serialize(new { gatewayHost = "host-a", gatewayControl = "192.168.1.10:27032", steamListen = "0.0.0.0:27016", directListen = "192.168.1.10:27116",
            nodes = nodes.Select(n => new { slotKey = n.SlotKey, nodeId = n.Node, host = n.Host, role = n.Role == Admin.NodeRole.WorldAuthority ? "WA" : "regular", spare = n.Spare, backend = n.Endpoint, control = n.ControlEndpoint }) });
        var cluster = new ClusterDefinition { UniqueName = "sample", DisplayName = "Sample cluster", GoalState = DedicatedServerGoalState.On,
            GatewayUrl = "http://192.168.1.10:27032", Preparation = new("hash", specification, "", []) };
        var status = new Admin.ClusterStatus("sample", "world", Admin.ClusterPhase.Serving, Admin.StartupKind.Warm, null, null,
            false, false, [], new(62, 6, 119, 0, 0, 0, 0, 0, 0, 0, 0, 0), new("world-authority", 1, 1, DateTimeOffset.UtcNow.AddMinutes(1)), nodes, [], true,
            Admin.AdminHealth.Healthy, [], DateTimeOffset.UtcNow);
        var fleet = new ClusterFleet("sample", nodes.Select((n, i) => new ClusterFleetNode(n,
            new(n.Node, n.Node, true, DateTimeOffset.UtcNow, new ServerMetrics { MemoryWorkingSetMb = (long)(ram[i] * 1024), ServerCpuLoadPercent = cpu[i], SimSpeed = 1 },
                null, null, [], []))).ToArray());
        var gateway = new GatewayStatus("sample", GatewayGoal.On, GatewayObservedState.Running, "hash", "revision", [], "", 42, DateTimeOffset.UtcNow, null,
            Metrics: new(DateTimeOffset.UtcNow, 38, (long)(1.2 * 1073741824)));
        var clients = nodes.SelectMany((node, i) => Enumerable.Range(0, players[i]).Select(p => new Admin.ClientSummary((ulong)(i * 100 + p), null, null,
            "accepted", false, node.Node, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow))).ToArray();
        var parts = nodes.SelectMany((node, i) => Enumerable.Range(0, partitions[i]).Select(p => new Admin.PartitionSummary((ulong)(i * 100 + p), node.Node,
            1, 1, DateTimeOffset.UtcNow.AddMinutes(1), false, false, false))).ToArray();
        return new(cluster, status, fleet, new("executor", "host-a", [], [gateway]), clients, parts,
            [new("host-a", "Gateway host", "192.168.1.10", 18400, "credential"), new("host-b", "Simulation host", "192.168.1.11", 18400, "credential")]);
    }
}
