using System.Reflection;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Quasar.Client.Clusters;
using Quasar.Models;
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
        await using var context = new QuasarBunitContext();
        context.Services.AddMudServices();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddScoped<Quasar.Client.DashboardSdk>();
        context.ComponentFactories.AddStub<ClusterPluginConfigurationPanel>();
        var cluster = BrowserClusterFixture.Dashboard(new() { UniqueName = "test", GoalState = DedicatedServerGoalState.On });
        var capabilities = new Admin.AdminCapabilities("gateway", Admin.AdminScope.Manage, ["POST /admin/v1/save-all"], new Dictionary<string, string>());
        var page = context.Render<ClusterAdminPanel>(p => p
            .Add(c => c.Cluster, cluster)
            .Add(c => c.Capabilities, capabilities).Add(c => c.Compact, compact).Add(c => c.GatewayAvailable, true));
        string selector = compact ? "button[aria-label='Save world']" : ".quasar-tooltip button";
        Assert.False(page.Find(selector).HasAttribute("disabled"));
        page.Render(p => p.Add(c => c.GatewayAvailable, false));
        Assert.True(page.Find(selector).HasAttribute("disabled"));
        Assert.Contains("Gateway admin connection is unavailable", page.Find(selector).ParentElement!.GetAttribute("title"));
        // The handler guard must work even when invoked after capabilities were cached.
        await page.InvokeAsync(() => (Task)typeof(ClusterAdminPanel).GetMethod("SaveAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(page.Instance, null)!);
        await page.InvokeAsync(() => page.Instance.SubmitAsync(new("save-all")));
        Assert.DoesNotContain(context.JSInterop.Invocations, call => call.Identifier == "quasarSdk.uploadJson");
        page.Render(p => p.Add(c => c.GatewayAvailable, true));
        Assert.False(page.Find(selector).HasAttribute("disabled"));
    }

}
