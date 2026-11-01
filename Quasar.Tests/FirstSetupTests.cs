using System.Reflection;
using Bunit;
using Bunit.TestDoubles;
using Magnetar.Protocol.Runtime;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using MudBlazor;
using MudBlazor.Services;
using Quasar.Components.Dashboard;
using Quasar.Components.Pages;
using Quasar.Services;
using Quasar.Services.Auth;
using Xunit;

namespace Quasar.Tests;

[Collection("Exact server creation")]
public sealed class FirstSetupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "first-setup-" + Guid.NewGuid().ToString("N"));
    private readonly FieldInfo _cache = typeof(MagnetarPaths).GetField("_cachedQuasarDirectory", BindingFlags.NonPublic | BindingFlags.Static)!;
    private readonly object? _previous;
    private readonly Dictionary<string, string> _storage = [];

    public FirstSetupTests() { _previous = _cache.GetValue(null); _cache.SetValue(null, _root); }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InitialChoiceRequiresExplicitDismissalAndOnlyOffersLinuxClustersWithPermission(bool clusterPermission)
    {
        await using (var context = Context(clusterPermission))
        {
            var provider = context.Render<MudDialogProvider>();
            var page = context.Render<Home>();
            await provider.WaitForAssertionAsync(() => Assert.Contains("Set Up First Server", provider.Markup));
            Assert.Contains("Single Magnetar", provider.Markup);
            Assert.Equal(OperatingSystem.IsLinux() && clusterPermission,
                provider.FindAll("button").Any(b => b.TextContent.Trim() == "Cluster"));
            await provider.Find(".mud-overlay").ClickAsync();
            Assert.Single(provider.FindComponents<ExistingServerImportDialog>());

            // Circuit/page teardown can cancel a modal; that is not a user dismissal.
            page.Instance.Dispose();
            page.Dispose();
            await provider.Find("button[title='Close setup']").ClickAsync();
        }
        await using (var context = Context(clusterPermission))
        {
            var provider = context.Render<MudDialogProvider>();
            context.Render<Home>();
            await provider.WaitForAssertionAsync(() => Assert.Contains("Set Up First Server", provider.Markup));
            await provider.FindAll("button").Single(b => b.TextContent.Trim() == "Skip setup").ClickAsync();
        }
        await using (var context = Context(clusterPermission))
        {
            var provider = context.Render<MudDialogProvider>();
            var page = context.Render<Home>();
            Assert.Empty(provider.FindComponents<ExistingServerImportDialog>());
            var reopening = page.FindAll("button").Single(b => b.TextContent.Trim() == "Setup guide").ClickAsync();
            await provider.WaitForAssertionAsync(() => Assert.Contains("Set Up First Server", provider.Markup));
            await provider.Find("button[title='Close setup']").ClickAsync();
            await reopening;
        }
    }

    [Fact]
    public async Task SingleMagnetarGuideSurvivesRefreshAndServerCreationUntilExplicitlySkipped()
    {
        await using (var context = Context())
        {
            var provider = context.Render<MudDialogProvider>();
            var page = context.Render<Home>();
            await provider.WaitForAssertionAsync(() => Assert.Contains("Single Magnetar", provider.Markup));
            await provider.FindAll("button").Single(b => b.TextContent.Trim() == "Single Magnetar").ClickAsync();
            await page.WaitForAssertionAsync(() => Assert.Contains("First Setup", page.Markup));
            await context.Services.GetRequiredService<DedicatedServerCatalog>().UpsertAsync(new()
                { UniqueName = "first", DisplayName = "First", WorldSaveName = "World", ServerPort = 27016 });
            Assert.Contains("First Setup", page.Markup);
        }
        await using (var context = Context())
        {
            var provider = context.Render<MudDialogProvider>();
            var page = context.Render<Home>();
            await page.WaitForAssertionAsync(() => Assert.Contains("First Setup", page.Markup));
            Assert.Empty(provider.FindComponents<ExistingServerImportDialog>());
            await page.FindAll("button").Single(b => b.TextContent.Trim() == "Skip setup").ClickAsync();
            Assert.DoesNotContain("<h2>First Setup</h2>", page.Markup);
        }
        await using (var context = Context())
        {
            var provider = context.Render<MudDialogProvider>();
            var page = context.Render<Home>();
            Assert.DoesNotContain("<h2>First Setup</h2>", page.Markup);
            Assert.Empty(provider.FindComponents<ExistingServerImportDialog>());
        }
    }

    [Fact]
    public async Task ImportChoiceReopensAfterRefreshAndStillRequiresExplicitCancel()
    {
        await using (var context = Context())
        {
            var provider = context.Render<MudDialogProvider>();
            var page = context.Render<Home>();
            await provider.WaitForAssertionAsync(() => Assert.Contains("Import Existing Server", provider.Markup));
            await provider.FindAll("button").Single(b => b.TextContent.Trim() == "Import Existing Server").ClickAsync();
            Assert.Contains("What currently runs this server?", provider.Markup);
            page.Instance.Dispose();
            page.Dispose();
            await provider.Find("button[title='Close setup']").ClickAsync();
        }
        await using (var context = Context())
        {
            var provider = context.Render<MudDialogProvider>();
            context.Render<Home>();
            await provider.WaitForAssertionAsync(() => Assert.Contains("What currently runs this server?", provider.Markup));
            await provider.FindAll("button").Single(b => b.TextContent.Trim() == "Cancel").ClickAsync();
        }
    }

    [Fact]
    public async Task LinuxClusterChoiceNavigatesToGuidedSetupAndLeavesAResumePanel()
    {
        if (!OperatingSystem.IsLinux()) return;
        await using (var context = Context())
        {
            var provider = context.Render<MudDialogProvider>();
            context.Render<Home>();
            await provider.WaitForAssertionAsync(() => Assert.Contains("Single Magnetar", provider.Markup));
            await provider.FindAll("button").Single(b => b.TextContent.Trim() == "Cluster").ClickAsync();
            Assert.EndsWith("/clusters/new", context.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().Uri);
        }
        await using (var context = Context())
        {
            var provider = context.Render<MudDialogProvider>();
            var page = context.Render<Home>();
            await page.WaitForAssertionAsync(() => Assert.Contains("First Setup · Cluster", page.Markup));
            Assert.Equal("clusters/new", page.Find("a.quasar-button-primary").GetAttribute("href"));
            Assert.Empty(provider.FindComponents<ExistingServerImportDialog>());
            await page.FindAll("button").Single(b => b.TextContent.Trim() == "Skip setup").ClickAsync();
        }
    }

    [Fact]
    public async Task CompletedImportResumesTheGuideAfterRefreshInsteadOfStartingAnotherImport()
    {
        await using (var context = Context())
        {
            var provider = context.Render<MudDialogProvider>();
            var page = context.Render<Home>();
            await provider.WaitForAssertionAsync(() => Assert.Contains("Import Existing Server", provider.Markup));
            await provider.FindAll("button").Single(b => b.TextContent.Trim() == "Import Existing Server").ClickAsync();
            var imported = new Quasar.Models.DedicatedServerDefinition
                { UniqueName = "imported", DisplayName = "Imported", WorldSaveName = "World", ServerPort = 27016 };
            await context.Services.GetRequiredService<DedicatedServerCatalog>().UpsertAsync(imported);
            var dialog = provider.FindComponent<ExistingServerImportDialog>();
            await dialog.InvokeAsync(() => dialog.Instance.ImportCompleted.InvokeAsync(new(imported, new(), [], [])));
            page.Instance.Dispose();
            page.Dispose();
            await provider.Find("button[title='Close setup']").ClickAsync();
        }
        await using (var context = Context())
        {
            var provider = context.Render<MudDialogProvider>();
            var page = context.Render<Home>();
            await page.WaitForAssertionAsync(() => Assert.Contains("First Setup", page.Markup));
            Assert.Empty(provider.FindComponents<ExistingServerImportDialog>());
        }
    }

    [Fact]
    public async Task SavedGuideDoesNotBypassPermissionsOrDisappearWhenPermissionsReturn()
    {
        await using (var context = Context())
        {
            var provider = context.Render<MudDialogProvider>();
            context.Render<Home>();
            await provider.WaitForAssertionAsync(() => Assert.Contains("Single Magnetar", provider.Markup));
            await provider.FindAll("button").Single(b => b.TextContent.Trim() == "Single Magnetar").ClickAsync();
        }
        await using (var context = Context())
        {
            context.Services.GetRequiredService<BunitAuthorizationContext>().SetPolicies(QuasarPolicyNames.CanView);
            var provider = context.Render<MudDialogProvider>();
            var page = context.Render<Home>();
            Assert.DoesNotContain("<h2>First Setup</h2>", page.Markup);
            Assert.Empty(provider.FindComponents<ExistingServerImportDialog>());
        }
        await using (var context = Context())
        {
            context.Render<MudDialogProvider>();
            var page = context.Render<Home>();
            await page.WaitForAssertionAsync(() => Assert.Contains("First Setup", page.Markup));
        }
    }

    [Fact]
    public async Task BackStepSurvivesRefreshAndNextReturnsToServerCreation()
    {
        await using (var context = Context())
        {
            string source = Path.Combine(_root, "source-world");
            Directory.CreateDirectory(source);
            await File.WriteAllTextAsync(Path.Combine(source, "Sandbox.sbc"), "<MyObjectBuilder_Checkpoint />");
            var world = await context.Services.GetRequiredService<QuasarWorldTemplateCatalog>().ImportAsync("Setup world", "", source);
            await context.Services.GetRequiredService<QuasarConfigProfileCatalog>().UpsertAsync(new()
                { ConfigProfileId = "setup", Name = "Setup", SourceWorldTemplateId = world.WorldTemplateId });
            var provider = context.Render<MudDialogProvider>();
            var page = context.Render<Home>();
            await provider.WaitForAssertionAsync(() => Assert.Contains("Single Magnetar", provider.Markup));
            await provider.FindAll("button").Single(b => b.TextContent.Trim() == "Single Magnetar").ClickAsync();
            Assert.Contains("Step 3 of 5", page.Markup);
            await page.FindAll("button").Single(b => b.TextContent.Trim() == "Back").ClickAsync();
            Assert.Contains("Step 2 of 5", page.Markup);
        }
        await using (var context = Context())
        {
            context.Render<MudDialogProvider>();
            var page = context.Render<Home>();
            await page.WaitForAssertionAsync(() => Assert.Contains("Step 2 of 5", page.Markup));
            await page.FindAll("button").Single(b => b.TextContent.Trim() == "Next").ClickAsync();
            Assert.Contains("Step 3 of 5", page.Markup);
        }
    }

    [Fact]
    public async Task DashboardShowsClusterOperationsWithoutAnAgentAndAfterRefresh()
    {
        await using (var context = Context())
        {
            await context.Services.GetRequiredService<ClusterCatalog>().CreateAsync(new("fleet", "My cluster", "http://gateway.test", ""), default);
            var operations = context.Services.GetRequiredService<ClusterOperationStore>();
            var page = context.Render<Home>();
            Assert.Contains("No recent activity", page.Markup);
            await operations.ExecuteAsync("fleet", "cluster.goal.set", "start", "admin", new { goal = "On" },
                _ => Task.FromResult(new CometWorks.ClusterGateway.AdminContract.V1.AdminEnvelope<ClusterGoalResult>(1, DateTimeOffset.UtcNow,
                    new("fleet", Quasar.Models.DedicatedServerGoalState.On, DateTimeOffset.UtcNow))), default);
            await page.WaitForAssertionAsync(() => Assert.Contains("Cluster start requested · completed", page.Markup));
            Assert.Contains("My cluster", page.Find(".quasar-activity").TextContent);
            Assert.Empty(context.Services.GetRequiredService<AgentRegistry>().GetAgents());
            await operations.ExecuteAsync<object, object>("fleet", "cluster.save-all", "failed-save", "admin", new { },
                _ => throw new ClusterPackageException("sensitive transport detail"), default);
            await page.WaitForAssertionAsync(() => Assert.Contains("Cluster world save · failed", page.Markup));
            Assert.DoesNotContain("sensitive transport detail", page.Markup);
        }
        await using (var context = Context())
        {
            var page = context.Render<Home>();
            await page.WaitForAssertionAsync(() => Assert.Contains("Cluster start requested · completed", page.Markup));
        }
    }

    [Fact]
    public async Task ClusterActivityHonorsServicePrincipalClusterAccess()
    {
        await using var context = Context();
        var catalog = context.Services.GetRequiredService<ClusterCatalog>();
        foreach (string id in new[] { "allowed", "private" })
        {
            await catalog.CreateAsync(new(id, id + " cluster", "http://gateway.test", ""), default);
            await context.Services.GetRequiredService<ClusterOperationStore>().ExecuteAsync(id, "cluster.save-all", "save", "admin", new { },
                _ => Task.FromResult(new CometWorks.ClusterGateway.AdminContract.V1.AdminEnvelope<string>(1, DateTimeOffset.UtcNow, "saved")), default);
        }
        context.Services.GetRequiredService<BunitAuthorizationContext>().SetClaims(
            new System.Security.Claims.Claim(QuasarClaimTypes.Provider, QuasarAuthSchemes.ServicePrincipal),
            new System.Security.Claims.Claim(QuasarClaimTypes.Cluster, "allowed"));
        var page = context.Render<Home>();
        Assert.Contains("allowed cluster", page.Find(".quasar-activity").TextContent);
        Assert.DoesNotContain("private cluster", page.Markup);
        var chart = page.FindComponent<Bunit.TestDoubles.Stub<FleetTelemetryChart>>();
        Assert.Equal("allowed", Assert.Single(chart.Instance.Parameters.Get(p => p.Clusters)).UniqueName);
    }

    [Fact]
    public async Task FleetChartRequestsClusterSlotHistoryAlongsideStandaloneServers()
    {
        await using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        using var branding = new BrandingService(NullLogger<BrandingService>.Instance);
        context.Services.AddSingleton(branding);
        context.Services.AddSingleton(new ThemePreferenceService(new(context.JSInterop.JSRuntime), branding, context.JSInterop.JSRuntime));
        AgentRuntimeState Node(string id, string slot, long epoch, string cluster = "fleet") => new()
        {
            AgentId = id, IsConnected = true,
            Hello = new() { ClusterMode = true, ClusterId = cluster, ClusterSlot = slot, ClusterNodeId = id, ClusterEpoch = epoch },
        };
        var old = Node("old", "node-1", 1);
        var current = Node("new", "node-1", 2);
        var other = Node("other", "node-2", 1);
        var panel = context.Render<FleetTelemetryChart>(p => p
            .Add(c => c.Servers, [new() { UniqueName = "standalone", DisplayName = "Standalone" }])
            .Add(c => c.Clusters, [new() { UniqueName = "fleet", DisplayName = "My cluster" }])
            .Add(c => c.Agents, [old, current, other, Node("orphan", "node-1", 1, "missing"), Node("pending", "node-3", 0)]));
        var options = System.Text.Json.JsonSerializer.SerializeToElement(Assert.Single(context.JSInterop.Invocations["quasarCharts.sync"]).Arguments[0]);
        Assert.Equal(new[] { "standalone", current.AnalyticsKey, other.AnalyticsKey }, options.GetProperty("servers").EnumerateArray().Select(v => v.GetString()));
        Assert.Equal("My cluster / node-1", options.GetProperty("names").GetProperty(current.AnalyticsKey).GetString());
        Assert.DoesNotContain(old.TelemetryKey, options.GetRawText());
        Assert.Contains("Standalone servers and cluster nodes", panel.Markup);

        var added = Node("added", "node-3", 1);
        panel.Render(p => p.Add(c => c.Agents, [current, other, added]));
        await panel.WaitForAssertionAsync(() => Assert.Equal(2, context.JSInterop.Invocations["quasarCharts.sync"].Count));
        var latest = System.Text.Json.JsonSerializer.SerializeToElement(context.JSInterop.Invocations["quasarCharts.sync"].Last().Arguments[0]);
        Assert.Contains(added.AnalyticsKey, latest.GetProperty("servers").EnumerateArray().Select(v => v.GetString()));
    }

    [Fact]
    public async Task ClusterOnlyDashboardFillsSummaryTilesAndRefreshesFromRegistryObservations()
    {
        var fixture = FleetSummaryTests.Sample();
        using var handler = new StatusOnlyGateway(fixture.Status);
        using var http = new HttpClient(handler);
        await using var context = Context(playerGateway: new(http));
        var cluster = await context.Services.GetRequiredService<ClusterCatalog>().CreateAsync(
            new("fleet", "My cluster", "http://gateway.test", ""), default);
        var registry = context.Services.GetRequiredService<AgentRegistry>();
        var agents = (Dictionary<string, AgentRuntimeState>)typeof(AgentRegistry)
            .GetField("_agents", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(registry)!;
        foreach (var agent in fixture.Agents) agents[agent.AgentId] = agent;
        var page = context.Render<Home>();
        Assert.Equal("—/ — slots", page.FindAll(".quasar-metric-value")[1].TextContent);
        var tracker = context.Services.GetRequiredService<ClusterPlayerService>();
        await tracker.RefreshAsync(cluster, default);
        await page.WaitForAssertionAsync(() => Assert.Equal(new[] { "1/ 1 running", "7/ 32 slots", "0.90×", "6.5GiB" },
            page.FindAll(".quasar-metric-value").Select(value => value.TextContent)));
        // Moderation endpoints are deliberately unavailable; the successful status remains useful.
        Assert.False(tracker.GetSnapshot("fleet")!.Available);
        Assert.True(tracker.GetSnapshot("fleet")!.StatusAvailable);

        handler.Status = handler.Status with { Counts = handler.Status.Counts with { ConnectedClients = 12 } };
        await tracker.RefreshAsync(cluster, default);
        await page.WaitForAssertionAsync(() => Assert.Equal("12/ 32 slots", page.FindAll(".quasar-metric-value")[1].TextContent));
    }

    private sealed class StatusOnlyGateway(CometWorks.ClusterGateway.AdminContract.V1.ClusterStatus status) : HttpMessageHandler
    {
        public CometWorks.ClusterGateway.AdminContract.V1.ClusterStatus Status = status;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            bool available = request.RequestUri!.AbsolutePath.EndsWith("/status");
            var response = new HttpResponseMessage(available ? System.Net.HttpStatusCode.OK : System.Net.HttpStatusCode.ServiceUnavailable)
            {
                Content = available
                    ? System.Net.Http.Json.JsonContent.Create(new CometWorks.ClusterGateway.AdminContract.V1.AdminEnvelope<CometWorks.ClusterGateway.AdminContract.V1.ClusterStatus>(1, DateTimeOffset.UtcNow, Status))
                    : System.Net.Http.Json.JsonContent.Create(new CometWorks.ClusterGateway.AdminContract.V1.AdminErrorEnvelope(1, DateTimeOffset.UtcNow, new("unavailable", "Moderation unavailable"))),
            };
            response.Headers.Add("X-Cluster-Gateway-Protocol", "1");
            return Task.FromResult(response);
        }
    }

    private BunitContext Context(bool clusterPermission = true, ClusterGatewayClient? playerGateway = null)
    {
        var context = new BunitContext();
        context.Services.AddMudServices();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var auth = context.AddAuthorization();
        context.Services.AddSingleton(auth);
        auth.SetAuthorized("admin");
        auth.SetPolicies(clusterPermission
            ? [QuasarPolicyNames.CanView, QuasarPolicyNames.CanEditServers, QuasarPolicyNames.CanEditConfigs, QuasarPolicyNames.ClusterManage]
            : [QuasarPolicyNames.CanView, QuasarPolicyNames.CanEditServers, QuasarPolicyNames.CanEditConfigs]);
        var servers = new DedicatedServerCatalog(NullLogger<DedicatedServerCatalog>.Instance);
        var worlds = new QuasarWorldTemplateCatalog(NullLogger<QuasarWorldTemplateCatalog>.Instance);
        var profiles = new QuasarConfigProfileCatalog(NullLogger<QuasarConfigProfileCatalog>.Instance);
        var registry = new AgentRegistry(null!, null!, null!, null!);
        var resolver = new ManagedDedicatedServerRuntimeResolver(NullLogger<ManagedDedicatedServerRuntimeResolver>.Instance, null!, new(), null!, null!);
        var warmup = new ManagedRuntimeWarmupService(resolver, NullLogger<ManagedRuntimeWarmupService>.Instance);
        context.Services.AddSingleton(servers);
        context.Services.AddSingleton(worlds);
        context.Services.AddSingleton(profiles);
        context.Services.AddSingleton(registry);
        context.Services.AddSingleton(warmup);
        context.Services.AddSingleton(new DedicatedServerSupervisor(servers, registry, null!, resolver, warmup, new(), null!, NullLogger<DedicatedServerSupervisor>.Instance));
        context.Services.AddSingleton(new ClusterCatalog(NullLogger<ClusterCatalog>.Instance, new ConfigurationBuilder().Build()));
        context.Services.AddSingleton(new ClusterOperationStore(Path.Combine(_root, "operations")));
        context.Services.AddSingleton(new ClusterPlayerService(null!, playerGateway!, null!, NullLogger<ClusterPlayerService>.Instance));
        context.Services.AddSingleton(new DataHandlingConsentCatalog(NullLogger<DataHandlingConsentCatalog>.Instance));
        context.Services.AddSingleton(new QuasarWorkshopModResolver(new(), null!, null!, NullLogger<QuasarWorkshopModResolver>.Instance));
        context.Services.AddSingleton(new ExistingServerImportService(servers, worlds, profiles, NullLogger<ExistingServerImportService>.Instance));
        context.Services.AddSingleton(new BrowserStorageService(new StorageRuntime(_storage)));
        context.Services.AddSingleton(new QuasarRoleMapper(new(), null!));
        context.Services.AddScoped<QuasarPermissionService>();
        context.Services.AddSingleton(new WebServiceOptions());
        context.Services.AddScoped<ServerManagementActions>();
        context.ComponentFactories.AddStub<Servers>();
        context.ComponentFactories.AddStub<ServerCard>();
        context.ComponentFactories.AddStub<ClusterCard>();
        context.ComponentFactories.AddStub<FleetTelemetryChart>();
        return context;
    }

    private sealed class StorageRuntime(Dictionary<string, string> storage) : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => InvokeAsync<TValue>(identifier, default, args);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            string key = (string)args![0]!;
            if (identifier == "quasarConfigs.getLocalStorage")
                return ValueTask.FromResult((TValue)(object?)storage.GetValueOrDefault(key)!);
            storage[key] = (string)args[1]!;
            return ValueTask.FromResult(default(TValue)!);
        }
    }

    public void Dispose()
    {
        _cache.SetValue(null, _previous);
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
