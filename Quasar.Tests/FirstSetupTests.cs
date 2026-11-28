using Quasar.Client.Components;
using System.Reflection;
using Microsoft.AspNetCore.Components.Forms;
using Bunit;
using Bunit.TestDoubles;
using Magnetar.Protocol.Runtime;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using MudBlazor;
using MudBlazor.Services;
using Quasar.Components.Pages;
using Quasar.Client.Formatting;
using Quasar.Models;
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
            Assert.Equal(OperatingSystem.IsLinux() && clusterPermission,
                provider.FindAll("button").Any(b => b.TextContent.Trim() == "Cluster"));
            await provider.Find(".mud-overlay").ClickAsync();
            Assert.Single(provider.FindComponents<ImportExistingServerDialog>());

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
            Assert.Empty(provider.FindComponents<ImportExistingServerDialog>());
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
        }
        await using (var context = Context())
        {
            var provider = context.Render<MudDialogProvider>();
            var page = context.Render<Home>();
            await page.WaitForAssertionAsync(() => Assert.Contains("First Setup", page.Markup));
            Assert.Empty(provider.FindComponents<ImportExistingServerDialog>());
            await page.FindAll("button").Single(b => b.TextContent.Trim() == "Skip setup").ClickAsync();
            Assert.DoesNotContain("<h2>First Setup</h2>", page.Markup);
        }
        await using (var context = Context())
        {
            var provider = context.Render<MudDialogProvider>();
            var page = context.Render<Home>();
            Assert.DoesNotContain("<h2>First Setup</h2>", page.Markup);
            Assert.Empty(provider.FindComponents<ImportExistingServerDialog>());
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
            Assert.Empty(provider.FindComponents<ImportExistingServerDialog>());
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
            var dialog = provider.FindComponent<ImportExistingServerDialog>();
            await dialog.InvokeAsync(() => dialog.Instance.ImportCompleted.InvokeAsync(imported.WorldTemplateId));
            page.Instance.Dispose();
            page.Dispose();
            await provider.Find("button[title='Close setup']").ClickAsync();
        }
        await using (var context = Context())
        {
            var provider = context.Render<MudDialogProvider>();
            var page = context.Render<Home>();
            await page.WaitForAssertionAsync(() => Assert.Contains("First Setup", page.Markup));
            Assert.Empty(provider.FindComponents<ImportExistingServerDialog>());
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
            Assert.Empty(provider.FindComponents<ImportExistingServerDialog>());
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
            var start = await operations.ExecuteAsync("fleet", "cluster.goal.set", "start", "admin", new { goal = "On" },
                _ => Task.FromResult(new CometWorks.ClusterGateway.AdminContract.V1.AdminEnvelope<ClusterGoalResult>(1, DateTimeOffset.UtcNow,
                    new("fleet", Quasar.Models.DedicatedServerGoalState.On, DateTimeOffset.UtcNow))), default);
            await page.Find("button[aria-label='Refresh fleet']").ClickAsync();
            await page.WaitForAssertionAsync(() => Assert.Contains("Cluster start requested · completed", page.Markup));
            var details = page.Find(".quasar-activity details");
            Assert.False(details.HasAttribute("open"));
            Assert.Contains(
                "Cluster start requested · completed",
                details.QuerySelector("summary")!.TextContent
            );
            Assert.Equal(
                new[] { "cluster.goal.set", start.OperationId },
                details.QuerySelectorAll("code").Select(code => code.TextContent)
            );
            Assert.Equal(
                new[] { start.CreatedAt.ToString("O"), start.UpdatedAt.ToString("O") },
                details.QuerySelectorAll("time").Select(time => time.GetAttribute("datetime"))
            );
            Assert.Empty(context.Services.GetRequiredService<AgentRegistry>().GetAgents());
            await operations.ExecuteAsync<object, object>("fleet", "cluster.save-all", "failed-save", "admin", new { },
                _ => throw new ClusterPackageException("sensitive transport detail"), default);
            await page.Find("button[aria-label='Refresh fleet']").ClickAsync();
            await page.WaitForAssertionAsync(() => Assert.Contains("Cluster world save · failed", page.Markup));
            Assert.Contains("cluster_package_failed", page.Find(".quasar-activity details dl").TextContent);
            Assert.DoesNotContain("sensitive transport detail", page.Markup);
        }
        await using (var context = Context())
        {
            var page = context.Render<Home>();
            await page.Find("button[aria-label='Refresh fleet']").ClickAsync();
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
        Assert.DoesNotContain("private cluster", page.Markup);
        var workspace = page.FindComponent<Bunit.TestDoubles.Stub<FleetWorkspace>>();
        Assert.Equal("allowed", Assert.Single(workspace.Instance.Parameters.Get(p => p.Dashboard).Rows).UniqueName);
    }

    [Fact]
    public async Task FleetChartRequestsClusterSlotHistoryAlongsideStandaloneServers()
    {
        await using var context = Context();
        await context.Services.GetRequiredService<ClusterCatalog>().CreateAsync(new("fleet", "My cluster", "http://gateway.test",""), default);
        await context.Services.GetRequiredService<DedicatedServerCatalog>().UpsertAsync(new() { UniqueName="standalone", DisplayName="Standalone", WorldSaveName="World" });
        var registry = context.Services.GetRequiredService<AgentRegistry>();
        AgentRuntimeState Node(string id, string slot, long epoch, string cluster = "fleet")
        {
            registry.UpsertHello(new() { AgentId = id, UniqueName = slot, ClusterMode = true, ClusterId = cluster, ClusterSlot = slot, ClusterNodeId = id, ClusterEpoch = epoch }, id, (_,_) => Task.CompletedTask);
            return registry.GetAgents().Single(a => a.AgentId == id);
        }
        var old = Node("old", "node-1", 1);
        var current = Node("new", "node-1", 2);
        var other = Node("other", "node-2", 1);
        Node("orphan", "node-1", 1, "missing");
        Node("pending", "node-3", 0);
        var user = (await context.Services.GetRequiredService<Microsoft.AspNetCore.Components.Authorization.AuthenticationStateProvider>().GetAuthenticationStateAsync()).User;
        async Task<Dictionary<string,string>> Sources() => (await Quasar.Services.Plugins.QuasarFleetDashboardOperations.Overview(new(user,context.Services), default)).Sources;
        var sources = await Sources();
        using var chart = new OperationsBrowserContext();
        chart.JSInterop.Mode = JSRuntimeMode.Loose;
        var panel = chart.Render<BrowserFleetChart>(p => p.Add(c => c.Sources, sources));
        var options = System.Text.Json.JsonSerializer.SerializeToElement(Assert.Single(chart.JSInterop.Invocations["quasarCharts.sync"]).Arguments[0]);
        Assert.Equal(new[] { "standalone", current.AnalyticsKey, other.AnalyticsKey }, options.GetProperty("servers").EnumerateArray().Select(v => v.GetString()));
        Assert.Equal("My cluster / node-1", options.GetProperty("names").GetProperty(current.AnalyticsKey).GetString());
        Assert.DoesNotContain(old.TelemetryKey, options.GetRawText());

        var added = Node("added", "node-3", 1);
        sources = await Sources();
        panel.Render(p => p.Add(c => c.Sources, sources));
        await panel.WaitForAssertionAsync(() => Assert.Equal(2, chart.JSInterop.Invocations["quasarCharts.sync"].Count));
        var latest = System.Text.Json.JsonSerializer.SerializeToElement(chart.JSInterop.Invocations["quasarCharts.sync"].Last().Arguments[0]);
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
        await page.Find("button[aria-label='Refresh fleet']").ClickAsync();
            await page.WaitForAssertionAsync(() => Assert.Equal(new[] { "1/ 1 running", "7/ 32 slots", NumberText.Display(0.90, "0.00") + "×", NumberText.Display(6.5, "0.0") + "GiB" },
            page.FindAll(".quasar-metric-value").Select(value => value.TextContent)));
        // Moderation endpoints are deliberately unavailable; the successful status remains useful.
        Assert.False(tracker.GetSnapshot("fleet")!.Available);
        Assert.True(tracker.GetSnapshot("fleet")!.StatusAvailable);

        handler.Status = handler.Status with { Counts = handler.Status.Counts with { ConnectedClients = 12 } };
        await tracker.RefreshAsync(cluster, default);
        await page.Find("button[aria-label='Refresh fleet']").ClickAsync();
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

    [Theory]
    [InlineData(true, ExistingServerKind.Vanilla)]
    [InlineData(false, ExistingServerKind.Torch)]
    public async Task ExistingServerZipWorksFromFirstSetupAndStandaloneImport(bool firstSetup, ExistingServerKind kind)
    {
        await using var context = Context();
        context.Render<MudPopoverProvider>();
        var dialogs = context.Render<MudDialogProvider>();
        await dialogs.InvokeAsync(() => context.Services.GetRequiredService<IDialogService>().ShowAsync<ImportExistingServerDialog>("Import",
            new DialogParameters { [nameof(ImportExistingServerDialog.OfferCreateNew)] = firstSetup }));
        if (firstSetup)
            await dialogs.FindAll("button").Single(button => button.TextContent.Trim() == "Import Existing Server").ClickAsync(new());
        await dialogs.FindAll("button").Single(button => button.TextContent.Trim() == (kind == ExistingServerKind.Torch ? "Torch" : "Vanilla Dedicated Server")).ClickAsync(new());
        await dialogs.FindAll("button").Single(button => button.TextContent.Trim() == "Next").ClickAsync(new());
        Assert.Single(dialogs.FindAll(".world-path-controls input"));
        Assert.Single(dialogs.FindAll(".world-path-controls button"));
        await dialogs.FindAll("button").Single(button => button.TextContent.Trim() == "Upload ZIP").ClickAsync(new());
        Assert.Empty(dialogs.FindAll("input[type=radio]"));
        using var zip = ExistingServerZipImportTests.CreateServerZip(kind, "Wrapper/");
        context.SelectedZip = zip.ToArray();
        dialogs.FindComponent<InputFile>().UploadFiles(InputFileContent.CreateFromBinary(zip.ToArray(), "server.zip"));
        await dialogs.FindAll("button").Single(button => button.TextContent.Trim() == "Analyze").ClickAsync(new());
        string configPath = dialogs.FindAll("p").Single(element => element.TextContent.StartsWith("Config: ")).TextContent[8..].Trim();
        Assert.True(File.Exists(configPath));
        // Returning to the upload step retains the staged files; no second browser upload is needed.
        await dialogs.FindAll("button").Single(button => button.TextContent.Trim() == "Back").ClickAsync(new());
        Assert.Contains("server.zip was read", dialogs.Markup);
        await dialogs.FindAll("button").Single(button => button.TextContent.Trim() == "Analyze").ClickAsync(new());
        await dialogs.FindAll("button").Single(button => button.TextContent.Trim() == "Next").ClickAsync(new());
        Assert.Contains("server.zip", dialogs.Markup);
        Assert.DoesNotContain("I confirm source server is stopped", dialogs.Markup);
        Assert.Empty(dialogs.FindAll("input[type=checkbox]"));
        Assert.False(dialogs.FindAll("button").Single(button => button.TextContent.Trim() == "Import").HasAttribute("disabled"));
        await dialogs.FindAll("button").Single(button => button.TextContent.Trim() == "Import").ClickAsync(new());
        var server = Assert.Single(context.Services.GetRequiredService<DedicatedServerCatalog>().GetServers());
        Assert.False(server.AutoStart);
        Assert.Equal(DedicatedServerGoalState.Off, server.GoalState);
        Assert.Equal("voxels", File.ReadAllText(Path.Combine(DedicatedServerPathResolver.Resolve(server).WorldSavePath, "Planet.vx2")));
        Assert.False(File.Exists(configPath));
    }

    [Fact]
    public async Task ExistingServerFolderStillRequiresStoppedConfirmation()
    {
        await using var context = Context();
        context.Render<MudPopoverProvider>();
        var dialogs = context.Render<MudDialogProvider>();
        await dialogs.InvokeAsync(() => context.Services.GetRequiredService<IDialogService>().ShowAsync<ImportExistingServerDialog>("Import"));
        await dialogs.FindAll("button").Single(button => button.TextContent.Trim() == "Vanilla Dedicated Server").ClickAsync(new());
        await dialogs.FindAll("button").Single(button => button.TextContent.Trim() == "Next").ClickAsync(new());
        string source = Path.Combine(_root, "source-server");
        using var zip = ExistingServerZipImportTests.CreateServerZip(ExistingServerKind.Vanilla);
        System.IO.Compression.ZipFile.ExtractToDirectory(zip, source);
        await dialogs.Find(".world-path-controls input").InputAsync(new() { Value = source });
        await dialogs.FindAll("button").Single(button => button.TextContent.Trim() == "Analyze").ClickAsync(new());
        await dialogs.FindAll("button").Single(button => button.TextContent.Trim() == "Next").ClickAsync(new());
        Assert.True(dialogs.FindAll("button").Single(button => button.TextContent.Trim() == "Import").HasAttribute("disabled"));
        var importer = context.Services.GetRequiredService<ExistingServerImportService>();
        var analysis = importer.Analyze(ExistingServerKind.Vanilla, source);
        var primary = analysis.Worlds[0].Path;
        var request = new ExistingServerImportRequest(ExistingServerKind.Vanilla, source, ExistingServerTransferMode.Copy,
            "Uploaded", "uploaded", [primary], primary, ExistingServerImportSections.Identity, false);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => importer.ImportAsync(request));
        Assert.Contains("Confirm that the source server is stopped", error.Message);
        await dialogs.Find("input[type=checkbox]").ChangeAsync(new() { Value = true });
        Assert.False(dialogs.FindAll("button").Single(button => button.TextContent.Trim() == "Import").HasAttribute("disabled"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelingOrSwitchingServerZipSourceRemovesUploadedFiles(bool switchSource)
    {
        await using var context = Context();
        var dialogs = context.Render<MudDialogProvider>();
        await dialogs.InvokeAsync(() => context.Services.GetRequiredService<IDialogService>().ShowAsync<ImportExistingServerDialog>("Import"));
        await dialogs.FindAll("button").Single(button => button.TextContent.Trim() == "Vanilla Dedicated Server").ClickAsync(new());
        await dialogs.FindAll("button").Single(button => button.TextContent.Trim() == "Next").ClickAsync(new());
        await dialogs.FindAll("button").Single(button => button.TextContent.Trim() == "Upload ZIP").ClickAsync(new());
        using var zip = ExistingServerZipImportTests.CreateServerZip(ExistingServerKind.Vanilla);
        context.SelectedZip = zip.ToArray();
        dialogs.FindComponent<InputFile>().UploadFiles(InputFileContent.CreateFromBinary(zip.ToArray(), "server.zip"));
        await dialogs.FindAll("button").Single(button => button.TextContent.Trim() == "Analyze").ClickAsync(new());
        string configPath = dialogs.FindAll("p").Single(element => element.TextContent.StartsWith("Config: ")).TextContent[8..].Trim();
        Assert.True(File.Exists(configPath));
        if (switchSource)
        {
            await dialogs.FindAll("button").Single(button => button.TextContent.Trim() == "Back").ClickAsync(new());
            await dialogs.FindAll("button").Single(button => button.TextContent.Trim() == "Server Folder").ClickAsync(new());
        }
        else
            await dialogs.FindAll("button").Single(button => button.TextContent.Trim() == "Cancel").ClickAsync(new());
        await dialogs.WaitForAssertionAsync(() => Assert.False(File.Exists(configPath)));
        Assert.Empty(context.Services.GetRequiredService<DedicatedServerCatalog>().GetServers());
    }

    [Fact]
    public async Task ReviewedFleetRemovalPreservesFilesAndRejectsStaleOrUnauthorizedRequests()
    {
        await using var context = Context();
        var catalog = context.Services.GetRequiredService<DedicatedServerCatalog>();
        await catalog.UpsertAsync(new() { UniqueName = "kept", DisplayName = "Kept", ServerPort = 27771, WorldSaveName = "World" });
        var original = catalog.GetServer("kept")!;
        var sdk = context.Services.GetRequiredService<Quasar.Client.DashboardSdk>();
        string worldPath = original.GetWorldSavePath();
        Directory.CreateDirectory(worldPath);
        string checkpoint = Path.Combine(worldPath, "Sandbox.sbc");
        await File.WriteAllTextAsync(checkpoint, "<MyObjectBuilder_Checkpoint />");
        var stale = new Quasar.Client.Fleet.FleetTarget("kept", original.UpdatedAtUtc);
        original.DisplayName = "Renamed";
        await catalog.UpsertAsync(original);
        await Assert.ThrowsAsync<JSException>(() => sdk.RequestAsync<bool>("fleet.delete", stale));
        var current = new Quasar.Client.Fleet.FleetTarget("kept", catalog.GetServer("kept")!.UpdatedAtUtc);
        var auth = context.Services.GetRequiredService<BunitAuthorizationContext>();
        auth.SetPolicies(QuasarPolicyNames.CanView);
        await Assert.ThrowsAsync<JSException>(() => sdk.RequestAsync<bool>("fleet.delete", current));
        Assert.NotNull(catalog.GetServer("kept"));
        auth.SetPolicies(QuasarPolicyNames.CanView, QuasarPolicyNames.CanEditServers);
        var review = await sdk.RequestAsync<Quasar.Client.Fleet.FleetPaths>("fleet.delete.review", current);
        Assert.Equal(MagnetarPaths.GetQuasarServerDirectory("kept"), review.Directory);
        Assert.True(await sdk.RequestAsync<bool>("fleet.delete", current));
        Assert.Null(catalog.GetServer("kept"));
        Assert.True(File.Exists(checkpoint));
    }

    [Fact]
    public async Task ChartRequestsFilterClusterKeysForScopedPrincipals()
    {
        await using var context = Context();
        var auth = context.Services.GetRequiredService<BunitAuthorizationContext>();
        auth.SetClaims(new System.Security.Claims.Claim(QuasarClaimTypes.Provider, QuasarAuthSchemes.ServicePrincipal),
            new System.Security.Claims.Claim(QuasarClaimTypes.Cluster, "allowed"));
        var principal = (await context.Services.GetRequiredService<Microsoft.AspNetCore.Components.Authorization.AuthenticationStateProvider>().GetAuthenticationStateAsync()).User;
        var clusters = context.Services.GetRequiredService<ClusterCatalog>();
        var registry = context.Services.GetRequiredService<AgentRegistry>();
        foreach (string id in new[] { "allowed", "private" })
        {
            await clusters.CreateAsync(new(id, id, "http://gateway.test", ""), default);
            registry.UpsertHello(new() { AgentId = id, UniqueName = id, ClusterMode = true, ClusterId = id,
                ClusterSlot = "one", ClusterNodeId = "node", ClusterEpoch = 1 }, "connection-" + id, (_,_)=>Task.CompletedTask);
        }
        var keys = registry.GetAgents().SelectMany(a => new[] { a.AnalyticsKey, a.TelemetryKey, Quasar.Services.Analytics.PluginStatsHistoryService.ClusterKey(a.ClusterId) }).Append("ordinary").ToArray();
        var allowed = await Quasar.Services.Plugins.QuasarFleetDashboardOperations.VisibleChartSources(new(principal, context.Services), keys);
        var permitted = registry.GetAgents().Single(a => a.ClusterId == "allowed");
        Assert.Equal(new[] {permitted.AnalyticsKey, permitted.TelemetryKey, Quasar.Services.Analytics.PluginStatsHistoryService.ClusterKey("allowed"), "ordinary"}.Order(), allowed.Order());
    }

    private OperationsBrowserContext Context(bool clusterPermission = true, ClusterGatewayClient? playerGateway = null)
    {
        var context = new OperationsBrowserContext(new StorageRuntime(_storage));
        context.Services.AddMudServices();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var auth = context.AddAuthorization();
        context.Services.AddSingleton(auth);
        auth.SetAuthorized("admin");
        auth.SetClaims(new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, "admin"));
        auth.SetPolicies(clusterPermission
            ? [QuasarPolicyNames.CanView, QuasarPolicyNames.CanEditServers, QuasarPolicyNames.CanEditConfigs, QuasarPolicyNames.ClusterQuery, QuasarPolicyNames.ClusterManage]
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
        context.Services.AddSingleton(new QuasarRoleMapper(new(), null!));
        context.Services.AddSingleton(new WebServiceOptions());
        context.Services.AddSingleton<Quasar.Services.Backup.ServerRestoreCoordinator>();
        context.Services.AddSingleton<ServerWorldImportService>();
        context.ComponentFactories.AddStub<FleetWorkspace>();
        context.ComponentFactories.AddStub<BrowserFleetChart>();
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
