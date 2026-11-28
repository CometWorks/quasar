using Quasar.Client;
using Quasar.Services.Auth;
using System.Text.Json;
using System.Net;
using System.Net.Http.Json;
using Bunit;
using CometWorks.ClusterGateway.AdminContract.V1;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MudBlazor.Services;
using Quasar.Host.Contract.V1;
using Quasar.Client.Clusters;
using Quasar.Models;
using Quasar.Services;
using Quasar.Services.PluginSdk;
using Xunit;

namespace Quasar.Tests;

public sealed class ClusterDetailPanelTests
{
    [Theory]
    [InlineData(DedicatedServerGoalState.Off)]
    [InlineData(DedicatedServerGoalState.On)]
    public async Task TopologyKeepsIndependentObservationsAndClearsFailedEdgeCountsOnRefresh(DedicatedServerGoalState goal)
    {
        string credential = "QUASAR_TEST_HOST_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(credential, "fixture-credential");
        try
        {
            await using var context = new OperationsBrowserContext();
            context.Services.AddMudServices();
            context.JSInterop.Mode = JSRuntimeMode.Loose;
            var fixture = ClusterHealthOverviewTests.Sample();
            fixture.Cluster.GoalState = goal;
            using var handler = new PartialGateway(fixture);
            handler.Recovery = new(RecoveryReadinessState.AtRisk, null, null, null, null,
                new(1, 0, 0, 0), new(1, 0, 0, 0), new(1, 0, 0, 0), new(false, null, 0, 0), [], []);
            using var http = new HttpClient(handler);
            var gateway = new ClusterGatewayClient(http);
            var host = new ClusterHostClient(http);
            context.Services.AddSingleton(gateway);
            context.Services.AddSingleton(host);
            context.Services.AddSingleton(new ClusterFleetService(gateway, new AgentRegistry(null!, null!, null!, null!), new PluginLogStream()));
            context.Services.AddSingleton(new ClusterHostCatalog(null!, Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))));
            context.Services.AddSingleton(new ClusterReconciler(null!, gateway, host, null!, NullLogger<ClusterReconciler>.Instance));
            context.Services.AddSingleton(new QuasarConfigProfileCatalog(NullLogger<QuasarConfigProfileCatalog>.Instance));
            context.Services.AddSingleton(new QuasarWorldTemplateCatalog(NullLogger<QuasarWorldTemplateCatalog>.Instance));
            fixture.Cluster.HostCommandUrl = "http://host.test";
            fixture.Cluster.HostCommandTokenEnvironmentVariable = credential;
            using var cluster = new BrowserClusterFixture(fixture.Cluster);
            context.Services.AddSingleton(cluster.Catalog);
            var auth = context.AddAuthorization();
            auth.SetAuthorized("observer"); auth.SetPolicies(QuasarPolicyNames.ClusterQuery);
            context.Render<MudBlazor.MudPopoverProvider>();
            var runtime = await RuntimeAsync(context, fixture.Cluster.UniqueName);
            var panel = context.Render<ClusterDetailPanel>(p => p.Add(c => c.Cluster, BrowserClusterFixture.Dashboard(fixture.Cluster))
                .Add(c => c.ShowTopology, true).Add(c => c.Runtime, runtime));
            Assert.NotNull(runtime.Capabilities);
            Assert.Contains("18 routed players", panel.Markup);
            Assert.Contains("34 partitions", panel.Markup);
            Assert.Contains("38% CPU", panel.Find(".cluster-gateway").TextContent);
            if (goal == DedicatedServerGoalState.On)
            {
                Assert.DoesNotContain("AtRisk", panel.Markup);
            }
            handler.Recovery = handler.Recovery with { State = RecoveryReadinessState.Ready,
                LatestPoint = new("save-1", RecoveryConsistency.CutConsistent, 42) };
            handler.FailEdges = true;
            handler.FailCapabilities = true;
            runtime = await RuntimeAsync(context, fixture.Cluster.UniqueName);
            panel.Render(p => p.Add(c => c.Runtime, runtime));
            Assert.DoesNotContain("18 routed players", panel.Markup);
            Assert.DoesNotContain("34 partitions", panel.Markup);
            Assert.Contains("38% CPU", panel.Find(".cluster-gateway").TextContent);
            Assert.Null(runtime.Capabilities);
            if (goal == DedicatedServerGoalState.On)
            {
                Assert.DoesNotContain("CutConsistent", panel.Markup);
                Assert.DoesNotContain("cluster time", panel.Markup);
            }
        }
        finally { Environment.SetEnvironmentVariable(credential, null); }
    }

    private sealed class PartialGateway(ClusterHealthOverviewTests.Fixture fixture) : HttpMessageHandler
    {
        internal volatile bool FailEdges;
        internal volatile bool FailCapabilities;
        internal RecoveryReadiness? Recovery;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            bool host = request.RequestUri!.Host == "host.test";
            object? value = request.RequestUri.AbsolutePath switch
            {
                "/host/v1/status" => fixture.Host,
                "/admin/v1/capabilities" when !FailCapabilities => new AdminCapabilities("gateway", AdminScope.Query, [], new Dictionary<string, string>()),
                "/admin/v1/status" => fixture.Status,
                "/admin/v1/plan" => Array.Empty<NodePlan>(),
                "/admin/v1/recovery-readiness" => Recovery,
                "/admin/v1/clients" when !FailEdges => fixture.Clients,
                "/admin/v1/partitions" when !FailEdges => fixture.Partitions,
                _ => null,
            };
            var response = new HttpResponseMessage(value is null ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)
            {
                Content = host ? JsonContent.Create(new HostEnvelope<object>(1, DateTimeOffset.UtcNow, value!))
                    : value is null ? JsonContent.Create(new AdminErrorEnvelope(1, DateTimeOffset.UtcNow, new("not_ready", "Fixture unavailable")))
                    : JsonContent.Create(new AdminEnvelope<object>(1, DateTimeOffset.UtcNow, value)),
            };
            response.Headers.Add(host ? HostProtocol.HeaderName : "X-Cluster-Gateway-Protocol", "1");
            return Task.FromResult(response);
        }
    }

    [Theory]
    [InlineData(DedicatedServerGoalState.Off, true, false, false)]
    [InlineData(DedicatedServerGoalState.Off, true, false, true)]
    [InlineData(DedicatedServerGoalState.Off, false, false, false)]
    [InlineData(DedicatedServerGoalState.Off, false, false, true)]
    [InlineData(DedicatedServerGoalState.On, true, true, false)]
    [InlineData(DedicatedServerGoalState.On, true, true, true)]
    public async Task GatewayConnectionWarningDependsOnGoalState(DedicatedServerGoalState goal, bool managed, bool shouldWarn, bool collapseDetails)
    {
        await using var context = new OperationsBrowserContext();
        context.Services.AddMudServices();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        using var http = new HttpClient(new OfflineGateway());
        var gateway = new ClusterGatewayClient(http);
        context.Services.AddSingleton(gateway);
        context.Services.AddSingleton(new ClusterFleetService(gateway, null!, null!));
        context.Services.AddSingleton(new ClusterHostCatalog(null!, Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))));
        context.Services.AddSingleton(new ClusterHostClient(http));
        context.Services.AddSingleton(new ClusterReconciler(null!, gateway, null!, null!, NullLogger<ClusterReconciler>.Instance));
        context.Services.AddSingleton(new QuasarConfigProfileCatalog(NullLogger<QuasarConfigProfileCatalog>.Instance));
        context.Services.AddSingleton(new QuasarWorldTemplateCatalog(NullLogger<QuasarWorldTemplateCatalog>.Instance));

        var definition = new ClusterDefinition { UniqueName = "test", GatewayUrl = "http://gateway.test", GoalState = goal,
            Gateway = managed ? new GatewaySpec("test", GatewayGoal.Off, "/bundle", new string('a', 64), "rev", [27032], "/run") : null };
        using var cluster = new BrowserClusterFixture(definition);
        context.Services.AddSingleton(cluster.Catalog);
        var auth = context.AddAuthorization();
        auth.SetAuthorized("observer"); auth.SetPolicies(QuasarPolicyNames.ClusterQuery);
        context.Render<MudBlazor.MudPopoverProvider>();
        var runtime = await RuntimeAsync(context, "test");
        var panel = context.Render<ClusterDetailPanel>(parameters => parameters
            .Add(p => p.Cluster, BrowserClusterFixture.Dashboard(definition)).Add(p => p.Runtime, runtime)
            .Add(p => p.CollapseDetails, collapseDetails));
        Assert.Null(runtime.Capabilities);
        Assert.Equal(shouldWarn, panel.Markup.Contains("Cannot reach the Cluster Registry", StringComparison.Ordinal));
        var labels = panel.FindAll("th").Select(cell => cell.TextContent).ToArray();
        Assert.Contains("Automatic cluster control", labels);
        Assert.Contains("Host management service", labels);
        Assert.Contains("Saved coordination state", labels);
        Assert.DoesNotContain("first reconcile pass", panel.Markup);
        if (collapseDetails)
        {
            Assert.False(panel.Find("details").HasAttribute("open"));
            Assert.All(panel.FindAll(".quasar-notice"), notice => Assert.Null(notice.Closest("details")));
        }

    }

    private static async Task<ClusterRuntime> RuntimeAsync(BunitContext context, string name)
    {
        var json = await context.Services.GetRequiredService<DashboardSdk>().ReadTextAsync("cluster.runtime", new ClusterPanelRequest(name));
        return JsonSerializer.Deserialize<ClusterRuntime>(json)!;
    }

    private sealed class OfflineGateway : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            throw new HttpRequestException(HttpRequestError.ConnectionError, "Gateway offline");
    }
}
