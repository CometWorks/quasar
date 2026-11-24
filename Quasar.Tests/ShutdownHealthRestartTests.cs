using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Magnetar.Protocol.Runtime;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Quasar.Models;
using Quasar.Services;
using Quasar.Services.Backup;
using Xunit;

namespace Quasar.Tests;

/// <summary>
/// Quasar's automatic recovery must never cost players progress: no restarts for a brief agent
/// outage or while Quasar shuts down, a cap on health restarts, and no kill without an admin.
/// </summary>
[Collection("Exact server creation")]
public sealed class ShutdownHealthRestartTests
{
    // ApplicationStopping closes every agent socket before the supervisor's StopAsync runs.
    // A reconcile in that gap used to read the disconnect as a failed server and start a
    // health-policy restart of a server Quasar meant to leave running.
    [Fact]
    public Task AgentDisconnectDuringQuasarShutdownDoesNotTriggerHealthRestart() => WithRunningServer(async server =>
    {
        server.Set("LastAgentTelemetryUtc", DateTimeOffset.UtcNow.AddMinutes(-5));
        server.Lifetime.StopApplication();
        await server.ReconcileAsync();

        Assert.False(server.Child.HasExited);
        var result = server.Snapshot();
        Assert.Equal(DedicatedServerProcessState.Running, result.State);
        Assert.Equal(DedicatedServerHealthState.Unhealthy, result.HealthState);
        Assert.Null(result.LastRestart);
    });

    [Fact]
    public Task BriefAgentDisconnectIsAWarningUntilTheHeartbeatTimeout() => WithRunningServer(async server =>
    {
        server.Set("LastAgentTelemetryUtc", DateTimeOffset.UtcNow.AddSeconds(-5));
        await server.ReconcileAsync();
        var waiting = server.Snapshot();
        Assert.Equal(DedicatedServerHealthState.Warning, waiting.HealthState);
        Assert.Contains("waiting for it to reconnect", waiting.HealthSummary);
        Assert.Null(waiting.LastRestart);

        server.Set("LastAgentTelemetryUtc", DateTimeOffset.UtcNow.AddSeconds(-25));
        await server.ReconcileAsync();
        var gone = server.Snapshot();
        Assert.Equal(DedicatedServerHealthState.Unhealthy, gone.HealthState);
        Assert.StartsWith("Quasar.Agent disconnected.", gone.HealthSummary);
    }, autoRestart: false);

    [Fact]
    public Task HealthRestartsPauseAfterTheLimitAndAskForAnAdmin() => WithRunningServer(async server =>
    {
        var times = (List<DateTimeOffset>)server.Get("HealthRestartTimesUtc")!;
        for (var i = 0; i < 3; i++)
            times.Add(DateTimeOffset.UtcNow.AddMinutes(-10 + i));
        server.Set("LastAgentTelemetryUtc", DateTimeOffset.UtcNow.AddMinutes(-5));

        await server.ReconcileAsync();

        Assert.False(server.Child.HasExited);
        var result = server.Snapshot();
        Assert.Null(result.LastRestart);
        Assert.True((bool)server.Get("HealthRestartsPaused")!);
        Assert.NotNull(result.AttentionAtUtc);
        Assert.Contains("paused automatic health restarts", result.AttentionMessage);

        // Next pass: still paused, and the card says so.
        var raisedAt = result.AttentionAtUtc;
        await server.ReconcileAsync();
        result = server.Snapshot();
        Assert.Equal(raisedAt, result.AttentionAtUtc);
        Assert.Contains("Automatic health restarts are paused", result.HealthSummary);
    });

    [Fact]
    public Task StopWithoutAnAgentWaitsInsteadOfKillingAndOnlyKillEndsIt() => WithRunningServer(async server =>
    {
        await server.Supervisor.StopServerAsync("live", TimeSpan.FromSeconds(1));

        Assert.False(server.Child.HasExited);
        var stopping = server.Snapshot();
        Assert.Equal(DedicatedServerProcessState.Stopping, stopping.State);
        Assert.Equal("Waiting for Quasar.Agent to reconnect to save and stop.", stopping.LastMessage);

        await server.Supervisor.KillServerAsync("live").WaitAsync(TimeSpan.FromSeconds(10));
        await server.Child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(DedicatedServerGoalState.Off, server.Catalog.GetServer("live")!.GoalState);
    }, autoRestart: false);

    private static async Task WithRunningServer(Func<RunningServer, Task> test, bool autoRestart = true)
    {
        if (!OperatingSystem.IsLinux()) return;
        string root = Path.Combine(Path.GetTempPath(), "recovery-policy-" + Guid.NewGuid().ToString("N"));
        var cache = typeof(MagnetarPaths).GetField("_cachedQuasarDirectory", BindingFlags.NonPublic | BindingFlags.Static)!;
        object? oldRoot = cache.GetValue(null);
        cache.SetValue(null, root);
        Process? adoptedProcess = null;
        using var child = Process.Start(new ProcessStartInfo("sleep", "60") { UseShellExecute = false })!;
        using var lifetime = new StoppingLifetime();
        try
        {
            using var catalog = new DedicatedServerCatalog(NullLogger<DedicatedServerCatalog>.Instance);
            await catalog.UpsertAsync(new DedicatedServerDefinition
            {
                UniqueName = "live", WorldSaveName = "World", AutoStart = true, GoalState = DedicatedServerGoalState.On,
                AutoRestartOnUnhealthy = autoRestart, EnableHealthMonitoring = true, AgentHeartbeatTimeoutSeconds = 20,
            });
            string path = MagnetarPaths.GetQuasarSupervisorStatePath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
            {
                servers = new[] { new { uniqueName = "live", processId = child.Id,
                    state = (int)DedicatedServerProcessState.Running, startedAtUtc = DateTimeOffset.UtcNow.AddHours(-2) } },
            }));
            using var supervisor = new DedicatedServerSupervisor(catalog, new(null!, null!, null!, null!),
                null!, null!, null!, new WebServiceOptions(), new ServerRestoreCoordinator(),
                NullLogger<DedicatedServerSupervisor>.Instance, lifetime: lifetime);
            var server = new RunningServer(supervisor, catalog, child, lifetime);
            server.Invoke("SyncDefinitions");
            server.Invoke("RestorePersistedRuntimeState");
            adoptedProcess = (Process)server.Get("Process")!;
            // A running server whose agent had been online: past adoption, no startup watch.
            server.Set("AwaitingAdoptedAgent", false);
            server.Set("AgentWatchSinceUtc", null);
            await test(server);
        }
        finally
        {
            if (adoptedProcess is not null) adoptedProcess.EnableRaisingEvents = false;
            if (!child.HasExited) child.Kill();
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            cache.SetValue(null, oldRoot);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private sealed record RunningServer(
        DedicatedServerSupervisor Supervisor,
        DedicatedServerCatalog Catalog,
        Process Child,
        StoppingLifetime Lifetime)
    {
        private object State => ((IDictionary)typeof(DedicatedServerSupervisor)
            .GetField("_states", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(Supervisor)!)["live"]!;

        public object? Invoke(string name, params object[] args) => typeof(DedicatedServerSupervisor)
            .GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(Supervisor, args);

        public object? Get(string property) => State.GetType().GetProperty(property)!.GetValue(State);

        public void Set(string property, object? value) => State.GetType().GetProperty(property)!.SetValue(State, value);

        public Task ReconcileAsync() =>
            ((Task)Invoke("ReconcileAsync", CancellationToken.None)!).WaitAsync(TimeSpan.FromSeconds(5));

        public DedicatedServerRuntimeSnapshot Snapshot() => Assert.Single(Supervisor.GetSnapshots());
    }

    private sealed class StoppingLifetime : IHostApplicationLifetime, IDisposable
    {
        private readonly CancellationTokenSource _stopping = new();
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => _stopping.Token;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() => _stopping.Cancel();
        public void Dispose() => _stopping.Dispose();
    }
}
