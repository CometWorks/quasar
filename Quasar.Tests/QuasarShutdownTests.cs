using System.Diagnostics;
using System.Net.WebSockets;
using System.Reflection;
using Magnetar.Protocol.Runtime;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Quasar.Services;
using Quasar.Services.Plugins;
using Quasar.Services.Updates;
using Quasar.Updates;
using Xunit;

namespace Quasar.Tests;

[CollectionDefinition("Quasar shutdown", DisableParallelization = true)]
public sealed class QuasarShutdownCollection;

[Collection("Quasar shutdown")]
public sealed class QuasarShutdownTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShutdownFromBlazorDispatcherSavesStateThenStops(bool underBootstrap)
    {
        await WithShutdownServiceAsync(underBootstrap, async (service, lifetime, root) =>
        {
            var dispatcher = Dispatcher.CreateDefault();
            // The synchronous entrypoint used to block this dispatcher waiting for
            // a file-save continuation that needed the very same dispatcher.
            await Task.Run(() => dispatcher.InvokeAsync(() => service.ShutdownQuasarPreservingServersAsync()))
                .WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(lifetime.Stopped);
            Assert.Equal(underBootstrap, File.Exists(Path.Combine(root, "launcher-shutdown-request")));
        });
    }

    [Fact]
    public async Task FailedBootstrapRequestKeepsWorkerRunning()
    {
        await WithShutdownServiceAsync(true, async (service, lifetime, root) =>
        {
            Directory.CreateDirectory(Path.Combine(root, "launcher-shutdown-request"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.ShutdownQuasarPreservingServersAsync());
            Assert.False(lifetime.Stopped);
        });
    }

    [Fact]
    public async Task StandaloneRestartFromBlazorDispatcherLeavesWorkerAndStateUntouched()
    {
        await WithShutdownServiceAsync(false, async (service, lifetime, root) =>
        {
            var dispatcher = Dispatcher.CreateDefault();
            await Assert.ThrowsAsync<InvalidOperationException>(() => Task.Run(() => dispatcher.InvokeAsync(() => service.RestartWorkerAsync()))
                .WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.False(lifetime.Stopped);
            Assert.False(File.Exists(Path.Combine(root, "supervisor-state.json")));
        });
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("invalid")]
    [InlineData("unavailable")]
    [InlineData("stale-pid")]
    public async Task UnverifiedNativeRestartDoesNotStopOrDrain(string status)
    {
        await WithNativeInstallationAsync(() => WithShutdownServiceAsync(true, async (service, lifetime, root) =>
        {
            if (status == "invalid") QuasarUpdateJournal.WriteText(BootstrapUpdateSupport.CapabilityPath, "{");
            else if (status != "missing") WriteCapability(status != "unavailable", status == "stale-pid" ? -1 : Environment.ProcessId);
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.RestartWorkerAsync());
            Assert.False(lifetime.Stopped);
            Assert.False(File.Exists(Path.Combine(root, "supervisor-state.json")));
            Assert.False(File.Exists(MagnetarPaths.GetQuasarWorkerRestartRequestPath()));
            Assert.Null(QuasarUpdateJournal.Read());
        }));
    }

    [Fact]
    public async Task VerifiedNativeRestartPersistsStateAndQueuesUpdaterWithoutStopping()
    {
        await WithNativeInstallationAsync(() => WithShutdownServiceAsync(true, async (service, lifetime, root) =>
        {
            WriteCapability(true, Environment.ProcessId);
            await service.RestartWorkerAsync();
            Assert.False(lifetime.Stopped);
            Assert.True(File.Exists(Path.Combine(root, "supervisor-state.json")));
            Assert.False(File.Exists(MagnetarPaths.GetQuasarWorkerRestartRequestPath()));
            var operation = Assert.IsType<QuasarUpdateOperation>(QuasarUpdateJournal.Read());
            Assert.True(operation.RestartOnly);
            Assert.True(operation.ActivateWhenReady);
            Assert.Equal(UpdateStage.Ready, operation.Stage);
        }));
    }

    [Fact]
    public async Task PendingUpdateRejectsRestartBeforeDrainAndKeepsOperation()
    {
        await WithNativeInstallationAsync(() => WithShutdownServiceAsync(true, async (service, lifetime, root) =>
        {
            WriteCapability(true, Environment.ProcessId);
            var pending = new QuasarUpdateOperation { Stage = UpdateStage.Downloading };
            QuasarUpdateJournal.Write(pending);
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.RestartWorkerAsync());
            Assert.Equal(pending.Id, QuasarUpdateJournal.Read()!.Id);
            Assert.False(lifetime.Stopped);
            Assert.False(File.Exists(Path.Combine(root, "supervisor-state.json")));
        }));
    }

    [Fact]
    public async Task FailedLegacyRestartRequestLeavesWorkerRunning()
    {
        await WithShutdownServiceAsync(true, async (service, lifetime, _) =>
        {
            Directory.CreateDirectory(MagnetarPaths.GetQuasarWorkerRestartRequestPath());
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.RestartWorkerAsync());
            Assert.False(lifetime.Stopped);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LegacyRestartAvailabilityUsesLauncherToken(bool underBootstrap)
    {
        Assert.Equal(underBootstrap, new WebServiceOptions { LauncherToken = underBootstrap ? "legacy" : "" }.RestartCapability.CanRestart);
    }

    private static void WriteCapability(bool canRestart, int pid) => QuasarUpdateJournal.WriteJson(
        BootstrapUpdateSupport.CapabilityPath, new BootstrapRestartCapability { CanRestart = canRestart, ProcessId = pid });

    [Fact]
    public async Task AutomaticPluginRestartWaitsForNativeVerificationThenQueuesOnce()
    {
        await WithNativeInstallationAsync(() => WithShutdownServiceAsync(true, async (service, lifetime, _) =>
        {
            var options = new QuasarUpdateOptions { AutoUpdateUiPlugins = true };
            var web = new WebServiceOptions { LauncherToken = "native-health-token" };
            using var credentials = new GitHubUpdateCredentialsCatalog(NullLogger<GitHubUpdateCredentialsCatalog>.Instance, new EphemeralDataProtectionProvider());
            using var updates = new QuasarUpdateService(new NoNetwork(), options, web, credentials, NullLogger<QuasarUpdateService>.Instance);
            var catalog = QuasarUiPluginCatalog.Create(new ConfigurationBuilder().Build(), null!, loadPluginAssemblies: false);
            var hub = new QuasarUiPluginHubCatalogService(NullLogger<QuasarUiPluginHubCatalogService>.Instance,
                new NoNetwork(), null!, null!, new QuasarUiPluginStateStore(), catalog, null!, null!, options, web, updates);
            var pending = typeof(QuasarUiPluginHubCatalogService).GetProperty("AutomaticRestartPending", BindingFlags.Instance | BindingFlags.NonPublic)!;
            pending.SetValue(hub, true);
            await hub.RestartAfterAutomaticUpdatesAsync(service, CancellationToken.None);
            Assert.Equal(true, pending.GetValue(hub));
            Assert.Null(QuasarUpdateJournal.Read());
            WriteCapability(true, Environment.ProcessId);
            await hub.RestartAfterAutomaticUpdatesAsync(service, CancellationToken.None);
            var operation = QuasarUpdateJournal.Read()!;
            Assert.True(operation.RestartOnly);
            Assert.Equal(false, pending.GetValue(hub));
            await hub.RestartAfterAutomaticUpdatesAsync(service, CancellationToken.None);
            Assert.Equal(operation.Id, QuasarUpdateJournal.Read()!.Id);
            Assert.False(lifetime.Stopped);
        }));
    }

    private sealed class NoNetwork : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("No network expected.");
    }

    private static async Task WithNativeInstallationAsync(Func<Task> test)
    {
        var property = typeof(QuasarServiceInstallation).GetProperty(nameof(QuasarServiceInstallation.Current))!;
        var previous = property.GetValue(null);
        property.SetValue(null, new QuasarServiceInstallation());
        try { await test(); }
        finally { property.SetValue(null, previous); }
    }

    [Fact]
    public async Task ShutdownFinishesWhileABrowserTabHoldsAConnection()
    {
        await WithQuasarRootAsync(async _ =>
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromMinutes(30));
            await using var app = builder.Build();
            app.UseWebSockets();
            // Stands for the circuit a tab opens again right after SignalR closed the old one on stopping.
            app.Map("/tab", async context =>
            {
                using var socket = await context.WebSockets.AcceptWebSocketAsync();
                await socket.ReceiveAsync(new byte[1], CancellationToken.None);
            });
            Program.StopWebServerOnStopping(app);
            await app.StartAsync();

            using var tab = new ClientWebSocket();
            var address = new Uri(app.Urls.Single().Replace("http", "ws") + "/tab");
            await tab.ConnectAsync(address, CancellationToken.None);

            var options = new WebServiceOptions();
            using var supervisor = new DedicatedServerSupervisor(
                null!, null!, null!, null!, null!, options, null!, NullLogger<DedicatedServerSupervisor>.Instance);
            var service = new QuasarShutdownService(app.Lifetime, supervisor, options, NullLogger<QuasarShutdownService>.Instance);
            var run = app.WaitForShutdownAsync();
            var stopwatch = Stopwatch.StartNew();
            await service.ShutdownQuasarPreservingServersAsync();
            await run.WaitAsync(Program.WebServerDrainTimeout + TimeSpan.FromSeconds(10));
            Assert.InRange(stopwatch.Elapsed, TimeSpan.Zero, Program.WebServerDrainTimeout + TimeSpan.FromSeconds(5));
        });
    }

    private static Task WithShutdownServiceAsync(
        bool underBootstrap, Func<QuasarShutdownService, TestLifetime, string, Task> test) =>
        WithQuasarRootAsync(async root =>
        {
            var options = new WebServiceOptions { LauncherToken = underBootstrap ? "test-token" : "" };
            using var supervisor = new DedicatedServerSupervisor(
                null!, null!, null!, null!, null!, options, null!, NullLogger<DedicatedServerSupervisor>.Instance);
            var lifetime = new TestLifetime(root);
            var service = new QuasarShutdownService(lifetime, supervisor, options, NullLogger<QuasarShutdownService>.Instance);
            await test(service, lifetime, root);
        });

    private static async Task WithQuasarRootAsync(Func<string, Task> test)
    {
        var root = Path.Combine(Path.GetTempPath(), $"quasar-shutdown-test-{Guid.NewGuid():N}");
        var cache = typeof(MagnetarPaths).GetField("_cachedQuasarDirectory", BindingFlags.NonPublic | BindingFlags.Static)!;
        var previousRoot = cache.GetValue(null);
        cache.SetValue(null, root);
        try
        {
            await test(root);
        }
        finally
        {
            cache.SetValue(null, previousRoot);
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private sealed class TestLifetime(string root) : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public bool Stopped { get; private set; }

        public void StopApplication()
        {
            Assert.True(File.Exists(Path.Combine(root, "supervisor-state.json")));
            Stopped = true;
        }
    }
}
