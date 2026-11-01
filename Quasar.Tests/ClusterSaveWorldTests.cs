using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MudBlazor.Services;
using Quasar.Components.Dashboard;
using Quasar.Models;
using Quasar.Services;
using Quasar.Services.Auth;
using Admin = CometWorks.ClusterGateway.AdminContract.V1;
using Xunit;

namespace Quasar.Tests;

public sealed class ClusterSaveWorldTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SaveDisablesImmediatelyOnPageOutageAndCannotSubmitWithCachedCapabilities(bool compact)
    {
        await using var context = new BunitContext();
        context.Services.AddMudServices();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var auth = context.AddAuthorization();
        auth.SetAuthorized("admin");
        auth.SetPolicies(QuasarPolicyNames.ClusterManage);
        context.Services.AddSingleton(new QuasarRoleMapper(new(), null!));
        context.Services.AddScoped<QuasarPermissionService>();
        context.Services.AddSingleton(new ClusterCommandService(null!, null!, null!, null!));
        context.Services.AddSingleton(new ClusterCatalog(NullLogger<ClusterCatalog>.Instance, new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build()));
        context.Services.AddSingleton(new QuasarConfigProfileCatalog(NullLogger<QuasarConfigProfileCatalog>.Instance));
        context.Services.AddSingleton(new ClusterOperationStore(Path.Combine(Path.GetTempPath(), "save-world-" + Guid.NewGuid().ToString("N"))));
        context.Services.AddSingleton(new ClusterUpdatePreparationService(null!, null!, null!, null!, null!, null!, null!, null!, null!, null!));
        context.ComponentFactories.AddStub<ClusterPluginConfigurationPanel>();
        using var http = new HttpClient(new Gateway());
        context.Services.AddSingleton(new ClusterGatewayClient(http));
        var page = context.Render<ClusterAdminPanel>(p => p
            .Add(c => c.Cluster, new() { UniqueName = "test", GatewayUrl = "http://gateway.test", GoalState = DedicatedServerGoalState.On })
            .Add(c => c.Compact, compact).Add(c => c.GatewayAvailable, true));
        string selector = compact ? "button[aria-label='Save world']" : ".quasar-tooltip button";
        Assert.False(page.Find(selector).HasAttribute("disabled"));
        page.Render(p => p.Add(c => c.GatewayAvailable, false));
        Assert.True(page.Find(selector).HasAttribute("disabled"));
        Assert.Contains("Gateway admin connection is unavailable", page.Find(selector).ParentElement!.GetAttribute("title"));
        // The handler guard must work even when invoked after capabilities were cached.
        // Commands has no dependencies: an attempted submission would fail this test.
        await page.InvokeAsync(() => (Task)typeof(ClusterAdminPanel).GetMethod("SaveAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(page.Instance, null)!);
        await page.InvokeAsync(() => page.Instance.SubmitAsync(new("save-all")));
        page.Render(p => p.Add(c => c.GatewayAvailable, true));
        Assert.False(page.Find(selector).HasAttribute("disabled"));
    }

    private sealed class Gateway : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var capabilities = new Admin.AdminCapabilities("gateway", Admin.AdminScope.Manage, ["POST /admin/v1/save-all"], new Dictionary<string, string>());
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            { Content = JsonContent.Create(new Admin.AdminEnvelope<Admin.AdminCapabilities>(1, DateTimeOffset.UtcNow, capabilities)) };
            response.Headers.Add("X-Cluster-Gateway-Protocol", "1");
            return Task.FromResult(response);
        }
    }
}
