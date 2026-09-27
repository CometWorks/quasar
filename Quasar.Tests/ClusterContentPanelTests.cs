using Bunit;
using Microsoft.Extensions.DependencyInjection;
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
        await monitor.CheckCoreAsync(default);
        await using var context = new BunitContext();
        context.Services.AddMudServices();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var auth = context.AddAuthorization();
        auth.SetAuthorized("viewer");
        if (manage) auth.SetPolicies(QuasarPolicyNames.ClusterManage);
        context.Services.AddSingleton(monitor);
        context.Services.AddSingleton(new QuasarRoleMapper(new QuasarAuthOptions(), null!));
        context.Services.AddSingleton<QuasarPermissionService>();
        var panel = context.Render<ClusterContentPanel>(p => p.Add(c => c.Cluster, fixture.Cluster));
        Assert.Contains("Mods and plugins", panel.Markup);
        Assert.Contains("Applying content updates is unavailable", panel.Markup);
        Assert.Contains("No change observed", panel.Markup);
        Assert.Equal(manage, panel.FindAll("button").Any(b => b.TextContent.Contains("Check content updates")));
        fixture.Timestamp++;
        await monitor.CheckCoreAsync(default);
        await panel.WaitForAssertionAsync(() => Assert.Contains("Changed since first check", panel.Markup));
        Assert.Contains("https://steamcommunity.com/sharedfiles/filedetails/changelog/123456", panel.Markup);
    }
}
