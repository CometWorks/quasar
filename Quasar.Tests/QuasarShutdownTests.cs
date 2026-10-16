using System.Diagnostics;
using System.Net.WebSockets;
using System.Reflection;
using Magnetar.Protocol.Runtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Quasar.Services;
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
    public async Task StandaloneRestartFromBlazorDispatcherSavesStateThenStops()
    {
        await WithShutdownServiceAsync(false, async (service, lifetime, _) =>
        {
            var dispatcher = Dispatcher.CreateDefault();
            await Task.Run(() => dispatcher.InvokeAsync(() => service.RestartWorkerAsync()))
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(lifetime.Stopped);
        });
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
