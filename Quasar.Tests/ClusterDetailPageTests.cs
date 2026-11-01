using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MudBlazor.Services;
using Quasar.Host.Contract.V1;
using Quasar.Models;
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
    // Real Blazor updates same-page fragments in the browser without a server navigation event.
    private sealed class FragmentNavigation : NavigationManager
    {
        internal FragmentNavigation() => Initialize("http://localhost/", "http://localhost/");
        protected override void NavigateToCore(string uri, NavigationOptions options) => Uri = ToAbsoluteUri(uri).AbsoluteUri;
        internal void BrowserNavigate(string uri) { Uri = ToAbsoluteUri(uri).AbsoluteUri; NotifyLocationChanged(false); }
    }

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
    [InlineData("#cluster-nodes", "Nodes")]
    [InlineData("#cluster-events", "Events")]
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
            var created = await catalog.CreateAsync(new("test", "Test cluster", "http://registry.test", ""), default);
            string bundleHash = new('a', 64);
            var gateway = new GatewaySpec("test", GatewayGoal.Off, "/bundle", bundleHash, "revision", [27032], "/run");
            var attachment = new HostAttachmentSpec("test", "http://registry.test", "");
            var host = new ClusterHostRevision("host", "http://host.test", "TEST_HOST_TOKEN", new("test", "revision", bundleHash, attachment, gateway));
            await catalog.RecordActiveDeploymentAsync(created, new("revision", [host], DateTimeOffset.UtcNow), default);
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
            var navigation = new FragmentNavigation();
            context.Services.AddSingleton<NavigationManager>(navigation);
            navigation.NavigateTo("/clusters/test" + fragment);
            var page = context.Render<ClusterDetail>(p => p.Add(c => c.UniqueName, "test"));
            Assert.Equal(initialView is "Maintenance" or "Settings" ? initialView : "Overview", page.Find("nav button[aria-pressed=true]").TextContent);
            Assert.All(page.FindAll("nav button"), button => Assert.Equal(
                button.TextContent is "Nodes" or "Events" or "Mods & plugins", button.HasAttribute("disabled")));
            Assert.All(page.FindAll("nav button"), button => Assert.Single(page.FindAll("#" + button.GetAttribute("aria-controls"))));
            var admin = page.FindComponent<AdminStub>();
            var overview = page.FindComponent<Bunit.TestDoubles.Stub<ClusterDetailPanel>>();
            var availability = overview.Instance.Parameters.Get(p => p.GatewayAvailabilityChanged);
            await page.InvokeAsync(() => availability.InvokeAsync(true));
            Assert.Equal(initialView, page.Find("nav button[aria-pressed=true]").TextContent);
            Assert.All(page.FindAll("nav button"), button => Assert.False(button.HasAttribute("disabled")));
            page.FindAll("nav button").Single(b => b.TextContent == "Settings").Click();
            Assert.EndsWith("#cluster-settings", navigation.Uri);
            Assert.False(admin.Instance.Compact);
            page.FindAll("nav button").Single(b => b.TextContent == "Nodes").Click();
            Assert.False(page.Find("#cluster-nodes").HasAttribute("hidden"));
            var fleet = page.FindComponents<Bunit.TestDoubles.Stub<ClusterFleetPanel>>().Single(panel => !panel.Instance.Parameters.Get(p => p.ShowEvents));
            page.FindAll("nav button").Single(b => b.TextContent == "Overview").Click();
            Assert.Same(admin.Instance, page.FindComponent<AdminStub>().Instance);
            Assert.Same(overview.Instance, page.FindComponent<Bunit.TestDoubles.Stub<ClusterDetailPanel>>().Instance);
            Assert.Contains(page.FindComponents<Bunit.TestDoubles.Stub<ClusterFleetPanel>>(), panel => ReferenceEquals(fleet.Instance, panel.Instance));
            Assert.True(page.Find("#cluster-nodes").HasAttribute("hidden"));
            Assert.True(admin.Instance.Compact);

            page.FindAll("nav button").Single(b => b.TextContent == "Events").Click();
            Assert.False(page.Find("#cluster-events").HasAttribute("hidden"));
            Assert.Contains(page.FindComponents<Bunit.TestDoubles.Stub<ClusterFleetPanel>>(), panel => panel.Instance.Parameters.Get(p => p.ShowEvents));

            // Maintenance stays reachable during an outage; live-only views retain their bookmark.
            page.FindAll("nav button").Single(b => b.TextContent == "Maintenance").Click();
            var maintenance = page.FindComponent<Bunit.TestDoubles.Stub<ClusterDeploymentPanel>>();
            await page.InvokeAsync(() => availability.InvokeAsync(false));
            Assert.Equal("Maintenance", page.Find("nav button[aria-pressed=true]").TextContent);
            Assert.False(admin.Instance.GatewayAvailable);
            Assert.All(page.FindAll("nav button"), button => Assert.Equal(
                button.TextContent is "Nodes" or "Events" or "Mods & plugins", button.HasAttribute("disabled")));
            Assert.Contains("unavailable", page.Find("#cluster-gateway-connection").TextContent);
            Assert.False(page.Find("#cluster-configuration").HasAttribute("hidden"));
            await page.InvokeAsync(() => availability.InvokeAsync(true));
            Assert.Equal("Maintenance", page.Find("nav button[aria-pressed=true]").TextContent);
            Assert.Same(admin.Instance, page.FindComponent<AdminStub>().Instance);
            Assert.Same(maintenance.Instance, page.FindComponent<Bunit.TestDoubles.Stub<ClusterDeploymentPanel>>().Instance);
            page.FindAll("nav button").Single(b => b.TextContent == "Nodes").Click();
            await page.InvokeAsync(() => availability.InvokeAsync(false));
            Assert.Equal("Overview", page.Find("nav button[aria-pressed=true]").TextContent);
            await page.InvokeAsync(() => availability.InvokeAsync(true));
            Assert.Equal("Nodes", page.Find("nav button[aria-pressed=true]").TextContent);
            await page.InvokeAsync(() => navigation.BrowserNavigate("/clusters/test#cluster-nodes"));
            Assert.Equal("Nodes", page.Find("nav button[aria-pressed=true]").TextContent);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
