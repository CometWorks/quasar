using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using Quasar.Client.Clusters;
using Quasar.Services;
using Quasar.Services.Auth;
using Xunit;

namespace Quasar.Tests;

public sealed class ClusterContentPanelTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ContentReviewShowsObservationsAndRestrictsManualChecks(bool manage)
    {
        using var fixture = new ClusterContentMonitorTests.Fixture();
        using var monitor = fixture.Create();
        fixture.Cluster.GatewayUrl = "http://gateway.test";
        using var cluster = new BrowserClusterFixture(fixture.Cluster);
        await monitor.CheckCoreAsync(default);
        await using var context = new OperationsBrowserContext();
        context.Services.AddMudServices();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var auth = context.AddAuthorization();
        auth.SetAuthorized("viewer");
        auth.SetPolicies(manage ? [QuasarPolicyNames.ClusterQuery, QuasarPolicyNames.ClusterManage] : [QuasarPolicyNames.ClusterQuery]);
        context.Services.AddSingleton(monitor);
        context.Services.AddSingleton(cluster.Catalog);
        var panel = context.Render<ClusterContentPanel>(p => p.Add(c => c.UniqueName, fixture.Cluster.UniqueName).Add(c => c.CanManage, manage));
        var expansion = panel.FindComponent<MudExpansionPanel>();
        Assert.False(expansion.Instance.Expanded);
        await panel.InvokeAsync(() => expansion.Instance.ExpandAsync());
        Assert.True(expansion.Instance.Expanded);
        Assert.DoesNotContain("until cluster and Magnetar support", panel.Markup);
        Assert.Equal(manage, panel.FindAll("button").Any(b => b.TextContent.Contains("Check content updates")));
        await panel.InvokeAsync(() => expansion.Instance.CollapseAsync());
        Assert.Equal(manage, panel.FindAll("button").Any(b => b.TextContent.Contains("Queue mod and plugin updates")));
        fixture.Timestamp++;
        await monitor.CheckCoreAsync(default);
        await panel.WaitForAssertionAsync(() => Assert.Contains("Changed since first check", panel.Markup), TimeSpan.FromSeconds(15));
        Assert.True(expansion.Instance.Expanded);
        await panel.InvokeAsync(() => expansion.Instance.CollapseAsync());
        await monitor.CheckCoreAsync(default);
        await panel.InvokeAsync(() => Assert.False(expansion.Instance.Expanded));
        Assert.Contains("https://steamcommunity.com/sharedfiles/filedetails/changelog/123456", panel.Markup);
        var reopened = context.Render<ClusterContentPanel>(p => p.Add(c => c.UniqueName, fixture.Cluster.UniqueName).Add(c => c.CanManage, manage));
        Assert.True(reopened.FindComponent<MudExpansionPanel>().Instance.Expanded);
        fixture.Timestamp--;
        await monitor.CheckCoreAsync(default);
        await reopened.WaitForAssertionAsync(() => Assert.False(reopened.FindComponent<MudExpansionPanel>().Instance.Expanded), TimeSpan.FromSeconds(15));
    }
}
