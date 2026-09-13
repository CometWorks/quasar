using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using Quasar.Host.Contract.V1;
using Quasar.Models;
using Quasar.Services;
using Quasar.Services.Auth;
using Xunit;

namespace Quasar.Tests;

public sealed class ClusterApiRouteTests
{
    [Theory]
    [InlineData("/api/v1/clusters/{uniqueName}/fleet", false)]
    [InlineData("/api/v1/clusters/{uniqueName}/events", false)]
    [InlineData("/api/v1/clusters/{uniqueName}/commands", true)]
    public void ExpandedRoutesRetainQueryAndManageSeparation(string path, bool manage)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton<ClusterCatalog>(_ => null!);
        builder.Services.AddSingleton<ClusterGatewayClient>(_ => null!);
        builder.Services.AddSingleton<ClusterHostClient>(_ => null!);
        builder.Services.AddSingleton<ClusterOperationStore>(_ => null!);
        builder.Services.AddSingleton<ClusterCommandService>(_ => null!);
        builder.Services.AddSingleton<ClusterReconciler>(_ => null!);
        builder.Services.AddSingleton<ClusterFleetService>(_ => null!);
        var app = builder.Build();
        app.MapClusterApi(new QuasarAuthOptions { Enabled = true });
        var endpoint = Assert.Single(((IEndpointRouteBuilder)app).DataSources.SelectMany(s => s.Endpoints)
            .OfType<RouteEndpoint>(), e => e.RoutePattern.RawText == path);
        var policies = endpoint.Metadata.GetOrderedMetadata<Microsoft.AspNetCore.Authorization.IAuthorizeData>().Select(p => p.Policy).ToArray();
        Assert.Contains(QuasarPolicyNames.ClusterQuery, policies);
        Assert.Equal(manage, policies.Contains(QuasarPolicyNames.ClusterManage));
    }

    [Fact]
    public void GatewayApplyRouteIsPut()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton<ClusterCatalog>(_ => null!);
        builder.Services.AddSingleton<ClusterGatewayClient>(_ => null!);
        builder.Services.AddSingleton<ClusterHostClient>(_ => null!);
        builder.Services.AddSingleton<ClusterOperationStore>(_ => null!);
        builder.Services.AddSingleton<ClusterCommandService>(_ => null!);
        builder.Services.AddSingleton<ClusterReconciler>(_ => null!);
        WebApplication app = builder.Build();
        app.MapClusterApi(new QuasarAuthOptions { Enabled = false });

        RouteEndpoint endpoint = Assert.Single(((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>(),
            route => route.RoutePattern.RawText == "/api/v1/clusters/{uniqueName}/host/gateway");

        Assert.Contains("PUT", endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods);
    }

    [Fact]
    public void GoalAndLifecycleRoutesAreHeadlessApiRoutes()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton<ClusterCatalog>(_ => null!);
        builder.Services.AddSingleton<ClusterGatewayClient>(_ => null!);
        builder.Services.AddSingleton<ClusterHostClient>(_ => null!);
        builder.Services.AddSingleton<ClusterOperationStore>(_ => null!);
        builder.Services.AddSingleton<ClusterCommandService>(_ => null!);
        builder.Services.AddSingleton<ClusterReconciler>(_ => null!);
        WebApplication app = builder.Build();
        app.MapClusterApi(new QuasarAuthOptions { Enabled = false });
        RouteEndpoint[] endpoints = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints).OfType<RouteEndpoint>().ToArray();

        Assert.Contains(endpoints, route => route.RoutePattern.RawText == "/api/v1/clusters/{uniqueName}/goal"
            && route.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Contains("PUT"));
        Assert.Contains(endpoints, route => route.RoutePattern.RawText == "/api/v1/clusters/{uniqueName}/lifecycle"
            && route.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Contains("GET"));
    }

    [Fact]
    public void GatewayGoalAcceptsContractString()
    {
        GatewaySpec spec = JsonSerializer.Deserialize<GatewaySpec>("""
            {
              "clusterId":"demo",
              "goal":"On",
              "bundleManifestPath":"/bundle/manifest.json",
              "bundleManifestSha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
              "configRevision":"r1",
              "ports":[28000],
              "runRoot":"/runs/demo"
            }
            """, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        Assert.Equal(GatewayGoal.On, spec.Goal);
    }

    [Fact]
    public void ClusterGoalAcceptsHeadlessApiString()
    {
        ClusterGoalRequest request = JsonSerializer.Deserialize<ClusterGoalRequest>(
            """{"goal":"Off"}""", new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        Assert.Equal(DedicatedServerGoalState.Off, request.Goal);
    }
}
