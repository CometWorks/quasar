using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Quasar.Client.Clusters;
using Quasar.Plugin.Abstractions.Operations;
using Quasar.Services.Auth;
using Quasar.Services.Plugins;
using Xunit;
using Admin = CometWorks.ClusterGateway.AdminContract.V1;

namespace Quasar.Tests;

public sealed class ClusterBrowserOperationTests
{
    [Fact]
    public async Task LobbyMutationPreservesExactIdentifiersAndOnlyReturnsPublicOperationFields()
    {
        using var fixture = new OfflineGatewayFixture { LobbySupported = true };
        using var services = CreateServices(fixture);
        var operation = Find(services, "cluster.lobby.set");
        Assert.True(operation.Metadata.RequireDefaultPolicies);
        Assert.Contains(QuasarPolicyNames.ClusterMaintenance, operation.DefaultPolicies);
        var update = new Admin.LobbyUpdate(1, 9007199254740993, 7);
        var result = await operation.InvokeAsync(new(User("demo"), services), Input(update), default);
        var sent = Assert.Single(fixture.Requests, r => r.Method == "PUT");
        Assert.Equal(update, JsonSerializer.Deserialize<Admin.LobbyUpdate>(sent.Body, OfflineGatewayFixture.Json));
        var response = Assert.IsType<PanelOperation>(result);
        Assert.Equal("Succeeded", response.State);
        var status = await Find(services, "cluster.panel.operation").InvokeAsync(new(User("demo"), services), Input(response.OperationId), default);
        Assert.Equal(response, status);
        var json = JsonSerializer.Serialize(response);
        Assert.DoesNotContain("Idempotency", json);
        Assert.DoesNotContain("GatewayRequest", json);
    }

    [Fact]
    public async Task ReadAndMutationEnforceClusterScopeAndAdvancedMaintenanceFloor()
    {
        using var fixture = new OfflineGatewayFixture { LobbySupported = true };
        using var services = CreateServices(fixture);
        var denied = await Assert.ThrowsAsync<QuasarOperationException>(() =>
            Find(services, "cluster.lobby").InvokeAsync(new(User("elsewhere"), services), Input(0), default));
        Assert.Equal(404, denied.StatusCode);
        var restricted = PanelCommand.Create("offline-cache-clear", new Admin.OfflineCacheClearRequest("x", 1));
        using var stream = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(restricted));
        var caller = new QuasarOperationContext(User("demo", QuasarRoles.Editor), services) { Upload = stream, VerifyAccess = _ => Task.CompletedTask };
        var forbidden = await Assert.ThrowsAsync<QuasarOperationException>(() =>
            Find(services, "cluster.panel.command").InvokeAsync(caller, Input(0), default));
        Assert.Equal(403, forbidden.StatusCode);
        Assert.Empty(fixture.Requests);
    }

    [Fact]
    public async Task BlueprintUploadRetainsRevisionAndChecksAccessAtCommit()
    {
        using var fixture = new OfflineGatewayFixture { LobbySupported = true };
        using var services = CreateServices(fixture);
        var operation = Find(services, "cluster.lobby.upload");
        var target = new LobbyBlueprintTarget(1, new(20000000, 0, 100));
        const string blueprint = "<Definitions><ShipBlueprints/></Definitions>";
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(blueprint));
        var caller = new QuasarOperationContext(User("demo"), services) { Upload = stream, VerifyAccess = _ => throw new QuasarOperationException(403,"Access revoked.") };
        Assert.Equal(Admin.LobbyUpdate.MaxBlueprintBytes, operation.MaximumUploadBytes);
        var denied = await Assert.ThrowsAsync<QuasarOperationException>(() => operation.InvokeAsync(caller, Input(target), default));
        Assert.Equal(403, denied.StatusCode);
        Assert.Empty(fixture.Requests);
        await operation.InvokeAsync(caller with { VerifyAccess = _ => Task.CompletedTask }, Input(target), default);
        var sent = Assert.Single(fixture.Requests, r => r.Method == "PUT");
        Assert.Equal(new Admin.LobbyUpdate(1, BlueprintXml: blueprint, Position: target.Position),
            JsonSerializer.Deserialize<Admin.LobbyUpdate>(sent.Body, OfflineGatewayFixture.Json));
    }

    private static JsonElement Input<T>(T args) => JsonSerializer.SerializeToElement(ClusterPanelRequest.For("demo", args));
    private static QuasarOperation Find(IServiceProvider services, string id) => services.GetServices<QuasarOperation>().Single(o => o.OperationId == id);
    private static ClaimsPrincipal User(string cluster, string role = QuasarRoles.Admin) => new(new ClaimsIdentity(
        [new Claim(QuasarClaimTypes.Provider, QuasarAuthSchemes.ServicePrincipal), new Claim(QuasarClaimTypes.Cluster,cluster), new Claim(ClaimTypes.Role,role)], "fixture"));
    private static ServiceProvider CreateServices(OfflineGatewayFixture fixture)
    {
        var services = new ServiceCollection().AddLogging().AddAuthorization(Program.ConfigureAuthorizationPolicies)
            .AddSingleton(fixture.Gateway).AddSingleton(fixture.Commands).AddSingleton(fixture.Catalog).AddSingleton(fixture.Store);
        services.AddQuasarClusterDetail();
        return services.BuildServiceProvider();
    }
}
