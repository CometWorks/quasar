using System.Net;
using System.Net.Http.Json;
using Bunit;
using CometWorks.ClusterGateway.AdminContract.V1;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MudBlazor.Services;
using Quasar.Host.Contract.V1;
using Quasar.Components.Dashboard;
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
            await using var context = new BunitContext();
            context.Services.AddMudServices();
            context.JSInterop.Mode = JSRuntimeMode.Loose;
            var fixture = ClusterHealthOverviewTests.Sample();
            fixture.Cluster.GoalState = goal;
            using var handler = new PartialGateway(fixture);
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
            var availability = new List<bool>();
            var panel = context.Render<ClusterDetailPanel>(p => p.Add(c => c.Cluster, fixture.Cluster)
                .Add(c => c.ShowTopology, true).Add(c => c.ShowConfigurationLinks, false)
                .Add(c => c.GatewayAvailabilityChanged, available => availability.Add(available)));
            Assert.Equal([true], availability);
            Assert.Contains("18 routed players", panel.Markup);
            Assert.Contains("34 partitions", panel.Markup);
            Assert.Contains("38% CPU", panel.Find(".cluster-gateway").TextContent);
            handler.FailEdges = true;
            handler.FailCapabilities = true;
            await panel.WaitForAssertionAsync(() => Assert.DoesNotContain("18 routed players", panel.Markup), TimeSpan.FromSeconds(10));
            Assert.Contains("— routed players", panel.Markup);
            Assert.DoesNotContain("34 partitions", panel.Markup);
            Assert.Contains("38% CPU", panel.Find(".cluster-gateway").TextContent);
            Assert.Equal([true, false], availability);
        }
        finally { Environment.SetEnvironmentVariable(credential, null); }
    }

    private sealed class PartialGateway(ClusterHealthOverviewTests.Fixture fixture) : HttpMessageHandler
    {
        internal volatile bool FailEdges;
        internal volatile bool FailCapabilities;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            bool host = request.RequestUri!.Host == "host.test";
            object? value = request.RequestUri.AbsolutePath switch
            {
                "/host/v1/status" => fixture.Host,
                "/admin/v1/capabilities" when !FailCapabilities => new AdminCapabilities("gateway", AdminScope.Query, [], new Dictionary<string, string>()),
                "/admin/v1/status" => fixture.Status,
                "/admin/v1/plan" => Array.Empty<NodePlan>(),
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
        await using var context = new BunitContext();
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

        var availability = new List<bool>();
        var panel = context.Render<ClusterDetailPanel>(parameters => parameters
            .Add(p => p.Cluster, new ClusterDefinition { UniqueName = "test", GatewayUrl = "http://gateway.test", GoalState = goal,
                Gateway = managed ? new GatewaySpec("test", GatewayGoal.Off, "/bundle", "hash", "rev", [], "/run") : null })
            .Add(p => p.ShowConfigurationLinks, false).Add(p => p.CollapseDetails, collapseDetails)
            .Add(p => p.GatewayAvailabilityChanged, available => availability.Add(available)));

        Assert.Equal([false], availability);
        Assert.Equal(shouldWarn, panel.Markup.Contains("Cannot reach the Cluster Registry", StringComparison.Ordinal));
        if (collapseDetails)
        {
            Assert.False(panel.Find("details").HasAttribute("open"));
            Assert.Contains("Desired state", panel.Find("details").TextContent);
            Assert.All(panel.FindAll(".quasar-notice"), notice => Assert.Null(notice.Closest("details")));
        }

    }

    [Fact]
    public async Task PollUpdatesVisibleStatusWithoutParentRender()
    {
        await using var context = new BunitContext();
        context.Services.AddMudServices();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        using var handler = new ChangingGateway();
        using var http = new HttpClient(handler);
        var gateway = new ClusterGatewayClient(http);
        var host = new ClusterHostClient(http);
        context.Services.AddSingleton(gateway);
        context.Services.AddSingleton(new ClusterFleetService(gateway, null!, null!));
        context.Services.AddSingleton(new ClusterHostCatalog(null!, Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))));
        context.Services.AddSingleton(host);
        context.Services.AddSingleton(new ClusterReconciler(null!, gateway, host, null!, NullLogger<ClusterReconciler>.Instance));
        context.Services.AddSingleton(new QuasarConfigProfileCatalog(NullLogger<QuasarConfigProfileCatalog>.Instance));
        context.Services.AddSingleton(new QuasarWorldTemplateCatalog(NullLogger<QuasarWorldTemplateCatalog>.Instance));
        var panel = context.Render<ClusterDetailPanel>(parameters => parameters
            .Add(p => p.Cluster, new ClusterDefinition { UniqueName = "test", GatewayUrl = "http://gateway.test" })
            .Add(p => p.ShowConfigurationLinks, false));
        Assert.Contains("First observation", panel.Markup);
        handler.Message = "Later observation";
        await panel.WaitForAssertionAsync(() => Assert.Contains("Later observation", panel.Markup), TimeSpan.FromSeconds(10));
        Assert.DoesNotContain("First observation", panel.Markup);
    }

    private sealed class ChangingGateway : HttpMessageHandler
    {
        internal volatile string Message = "First observation";
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = JsonContent.Create(new AdminErrorEnvelope(1, DateTimeOffset.UtcNow, new AdminError("not_ready", Message))),
            };
            response.Headers.Add("X-Cluster-Gateway-Protocol", "1");
            return Task.FromResult(response);
        }
    }

    private sealed class OfflineGateway : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            throw new HttpRequestException(HttpRequestError.ConnectionError, "Gateway offline");
    }
}
