using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MudBlazor.Services;
using Quasar.Components.Dashboard;
using Quasar.Components.Pages;
using Quasar.Components.Shared;
using Quasar.Services;
using Quasar.Services.Auth;
using Quasar.Services.Updates;
using Xunit;

namespace Quasar.Tests;

public sealed class ClusterDetailPageTests
{
    // A typed replacement supports the page's component reference without running command polling.
    public sealed class AdminStub : ClusterAdminPanel
    {
        public override Task SetParametersAsync(ParameterView parameters)
        {
            parameters.SetParameterProperties(this);
            return Task.CompletedTask;
        }
    }

    [Theory]
    [InlineData("", "Overview")]
    [InlineData("#cluster-content", "Mods & plugins")]
    [InlineData("#cluster-configuration", "Maintenance")]
    [InlineData("#cluster-settings", "Settings")]
    public async Task ViewsHonorBookmarksAndKeepOverviewAndControlsMounted(string fragment, string initialView)
    {
        string root = Path.Combine(Path.GetTempPath(), "cluster-views-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var context = new BunitContext();
            context.Services.AddMudServices();
            context.JSInterop.Mode = JSRuntimeMode.Loose;
            var auth = context.AddAuthorization();
            auth.SetAuthorized("operator");
            auth.SetPolicies("ClusterManage");
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
                ["Quasar:ClusterCatalogPath"] = root }).Build();
            var catalog = new ClusterCatalog(NullLogger<ClusterCatalog>.Instance, config);
            await catalog.CreateAsync(new("test", "Test cluster", "http://registry.test", ""), default);
            context.Services.AddSingleton(catalog);
            context.Services.AddSingleton(new QuasarRoleMapper(new(), null!));
            context.Services.AddSingleton(new QuasarPermissionService(null!, null!, null!));
            context.Services.AddSingleton(new QuasarConfigProfileCatalog(NullLogger<QuasarConfigProfileCatalog>.Instance));
            context.Services.AddSingleton(new ClusterOperationStore(Path.Combine(root, "operations")));
            using var http = new HttpClient();
            context.Services.AddSingleton(new ClusterGatewayClient(http));
            context.Services.AddSingleton(new ClusterCommandService(null!, null!, null!, null!));
            context.Services.AddSingleton(new ClusterUpdatePreparationService(null!, null!, null!, null!, null!, null!, null!, null!, null!, null!));
            context.Services.AddSingleton(new ClusterReleaseMonitor(catalog, null!, null!, new(), NullLogger<ClusterReleaseMonitor>.Instance));
            context.Services.AddSingleton(new ClusterSetupService(null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!));
            context.ComponentFactories.AddStub<ClusterBetaNotice>();
            context.ComponentFactories.AddStub<ClusterConsoleButton>();
            context.ComponentFactories.AddStub<ClusterDeleteButton>();
            context.ComponentFactories.Add<ClusterAdminPanel, AdminStub>();
            context.ComponentFactories.AddStub<ClusterDetailPanel>();
            context.ComponentFactories.AddStub<ClusterFleetPanel>();
            context.ComponentFactories.AddStub<ClusterContentPanel>();
            context.ComponentFactories.AddStub<ClusterDeploymentPanel>();
            var navigation = context.Services.GetRequiredService<NavigationManager>();
            navigation.NavigateTo("/clusters/test" + fragment);
            var page = context.Render<ClusterDetail>(p => p.Add(c => c.UniqueName, "test"));
            Assert.Equal(initialView, page.Find("nav button[aria-pressed=true]").TextContent);
            Assert.All(page.FindAll("nav button"), button => Assert.Single(page.FindAll("#" + button.GetAttribute("aria-controls"))));
            var admin = page.FindComponent<AdminStub>();
            var overview = page.FindComponent<Bunit.TestDoubles.Stub<ClusterDetailPanel>>();
            page.FindAll("nav button").Single(b => b.TextContent == "Settings").Click();
            Assert.EndsWith("#cluster-settings", navigation.Uri);
            Assert.False(admin.Instance.Compact);
            page.FindAll("nav button").Single(b => b.TextContent == "Nodes & players").Click();
            Assert.False(page.Find("#cluster-nodes").HasAttribute("hidden"));
            var fleet = page.FindComponent<Bunit.TestDoubles.Stub<ClusterFleetPanel>>();
            page.FindAll("nav button").Single(b => b.TextContent == "Overview").Click();
            Assert.Same(admin.Instance, page.FindComponent<AdminStub>().Instance);
            Assert.Same(overview.Instance, page.FindComponent<Bunit.TestDoubles.Stub<ClusterDetailPanel>>().Instance);
            Assert.Same(fleet.Instance, page.FindComponent<Bunit.TestDoubles.Stub<ClusterFleetPanel>>().Instance);
            Assert.True(page.Find("#cluster-nodes").HasAttribute("hidden"));
            Assert.True(admin.Instance.Compact);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
