using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using MudBlazor;
using MudBlazor.Services;
using Quasar.Components.Dashboard;
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
        using var catalog = new ClusterCatalog(NullLogger<ClusterCatalog>.Instance, new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Quasar:ClusterCatalogPath"] = Path.Combine(Path.GetDirectoryName(fixture.Path)!, "clusters") }).Build());
        await monitor.CheckCoreAsync(default);
        await using var context = new BunitContext();
        context.Services.AddMudServices();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var auth = context.AddAuthorization();
        auth.SetAuthorized("viewer");
        if (manage) auth.SetPolicies(QuasarPolicyNames.ClusterManage);
        context.Services.AddSingleton(monitor);
        context.Services.AddSingleton(catalog);
        context.Services.AddSingleton(new QuasarRoleMapper(new QuasarAuthOptions(), null!));
        context.Services.AddSingleton<QuasarPermissionService>();
        var panel = context.Render<ClusterContentPanel>(p => p.Add(c => c.Cluster, fixture.Cluster));
        var expansion = panel.FindComponent<MudExpansionPanel>();
        Assert.False(expansion.Instance.Expanded);
        await panel.InvokeAsync(() => expansion.Instance.ExpandAsync());
        Assert.True(expansion.Instance.Expanded);
        Assert.Contains("Mods and plugins", panel.Markup);
        Assert.Contains("Applying content updates is unavailable", panel.Markup);
        Assert.Contains("No change observed", panel.Markup);
        Assert.Equal(manage, panel.FindAll("button").Any(b => b.TextContent.Contains("Check content updates")));
        await panel.InvokeAsync(() => expansion.Instance.CollapseAsync());
        Assert.Equal(manage, panel.FindAll("button").Any(b => b.TextContent.Contains("Queue mod and plugin updates")));
        fixture.Timestamp++;
        await monitor.CheckCoreAsync(default);
        await panel.WaitForAssertionAsync(() => Assert.Contains("Changed since first check", panel.Markup));
        Assert.True(expansion.Instance.Expanded);
        await panel.InvokeAsync(() => expansion.Instance.CollapseAsync());
        await monitor.CheckCoreAsync(default);
        await panel.InvokeAsync(() => Assert.False(expansion.Instance.Expanded));
        Assert.Contains("https://steamcommunity.com/sharedfiles/filedetails/changelog/123456", panel.Markup);
        var reopened = context.Render<ClusterContentPanel>(p => p.Add(c => c.Cluster, fixture.Cluster));
        Assert.True(reopened.FindComponent<MudExpansionPanel>().Instance.Expanded);
        fixture.Timestamp--;
        await monitor.CheckCoreAsync(default);
        await reopened.WaitForAssertionAsync(() => Assert.False(reopened.FindComponent<MudExpansionPanel>().Instance.Expanded));
    }
}
