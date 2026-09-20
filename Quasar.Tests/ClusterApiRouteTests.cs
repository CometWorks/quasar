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
    [InlineData("/api/v1/clusters/{uniqueName}", true)]
    [InlineData("/api/v1/clusters/{uniqueName}/registration", true)]
    [InlineData("/api/v1/clusters/setup", true)]
    [InlineData("/api/v1/clusters/{uniqueName}/package-release", false)]
    [InlineData("/api/v1/clusters/{uniqueName}/package", true)]
    [InlineData("/api/v1/clusters/{uniqueName}/package-selection", false)]
    [InlineData("/api/v1/clusters/{uniqueName}/package-selection", true)]
    [InlineData("/api/v1/clusters/{uniqueName}/dependency-candidate", false)]
    [InlineData("/api/v1/clusters/{uniqueName}/dependencies", false)]
    [InlineData("/api/v1/clusters/{uniqueName}/dependencies", true)]
    [InlineData("/api/v1/clusters/{uniqueName}/deployment-inputs", true)]
    [InlineData("/api/v1/clusters/{uniqueName}/deployment", true)]
    [InlineData("/api/v1/clusters/{uniqueName}/backups", true)]
    [InlineData("/api/v1/clusters/{uniqueName}/backups", false)]
    [InlineData("/api/v1/clusters/{uniqueName}/restore", true)]
    [InlineData("/api/v1/clusters/{uniqueName}/update", true)]
    [InlineData("/api/v1/clusters/{uniqueName}/update", false)]
    [InlineData("/api/v1/clusters/{uniqueName}/update/abandon", true)]
    [InlineData("/api/v1/clusters/{uniqueName}/operations/{operationId}", true)]
    [InlineData("/api/v1/clusters/{uniqueName}/operations/{operationId}", false)]
    [InlineData("/api/v1/clusters/{uniqueName}/deployment-preparation", true)]
    [InlineData("/api/v1/clusters/{uniqueName}/gateway/recover", true)]
    [InlineData("/api/v1/clusters/{uniqueName}/recover", true)]
    [InlineData("/api/v1/clusters/{uniqueName}/deployment", false)]
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
            .OfType<RouteEndpoint>(), e => e.RoutePattern.RawText == path
                && (path.EndsWith("/deployment-inputs") || e.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Any(m => m != "GET") == manage));
        var policies = endpoint.Metadata.GetOrderedMetadata<Microsoft.AspNetCore.Authorization.IAuthorizeData>().Select(p => p.Policy).ToArray();
        Assert.Contains(QuasarPolicyNames.ClusterQuery, policies);
        Assert.Equal(manage, policies.Contains(QuasarPolicyNames.ClusterManage));
    }

    [Theory]
    [InlineData("convert/from-server")]
    [InlineData("convert/to-server")]
    [InlineData("convert/review/{server}")]
    [InlineData("convert/plugins")]
    [InlineData("conversions/{id:guid}")]
    public void ConversionRequiresServerAndConfigurationPermissions(string suffix)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton<ClusterCatalog>(_ => null!);
        builder.Services.AddSingleton<ClusterGatewayClient>(_ => null!);
        builder.Services.AddSingleton<ClusterHostClient>(_ => null!);
        builder.Services.AddSingleton<ClusterOperationStore>(_ => null!);
        builder.Services.AddSingleton<ClusterCommandService>(_ => null!);
        builder.Services.AddSingleton<ClusterReconciler>(_ => null!);
        var app = builder.Build();
        app.MapClusterApi(new QuasarAuthOptions { Enabled = true });
        var endpoint = Assert.Single(((IEndpointRouteBuilder)app).DataSources.SelectMany(s => s.Endpoints)
            .OfType<RouteEndpoint>(), e => e.RoutePattern.RawText == "/api/v1/clusters/{uniqueName}/" + suffix);
        var policies = endpoint.Metadata.GetOrderedMetadata<Microsoft.AspNetCore.Authorization.IAuthorizeData>().Select(p => p.Policy);
        Assert.Contains(QuasarPolicyNames.ClusterQuery, policies);
        Assert.Contains(QuasarPolicyNames.ClusterManage, policies);
        Assert.Contains(QuasarPolicyNames.CanEditServers, policies);
        Assert.Contains(QuasarPolicyNames.CanEditConfigs, policies);
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

    [Fact]
    public void ServerDefinitionStoresNumericGoalForDowngradeAndReadsBothShapes()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        string json = JsonSerializer.Serialize(new DedicatedServerDefinition { GoalState = DedicatedServerGoalState.On }, options);

        Assert.Contains("\"goalState\":1", json);
        Assert.Equal(DedicatedServerGoalState.On, JsonSerializer.Deserialize<DedicatedServerDefinition>(json, options)!.GoalState);
        // Written by pre-release cluster builds.
        Assert.Equal(DedicatedServerGoalState.On, JsonSerializer.Deserialize<DedicatedServerDefinition>("""{"goalState":"On"}""", options)!.GoalState);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<DedicatedServerDefinition>("""{"goalState":7}""", options));
        // The cluster API and cluster.json keep the readable names.
        Assert.Contains("\"goal\":\"On\"", JsonSerializer.Serialize(new ClusterGoalRequest(DedicatedServerGoalState.On), options));
        Assert.Contains("\"goalState\":\"On\"", JsonSerializer.Serialize(new ClusterDefinition { GoalState = DedicatedServerGoalState.On }, options));
    }
}
