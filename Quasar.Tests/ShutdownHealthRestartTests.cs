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

[Collection("Exact server creation")]
public sealed class ShutdownHealthRestartTests
{
    // ApplicationStopping closes every agent socket before the supervisor's StopAsync runs.
    // A reconcile in that gap used to read the disconnect as a failed server and start a
    // health-policy restart of a server Quasar meant to leave running.
    [Fact]
    public async Task AgentDisconnectDuringQuasarShutdownDoesNotTriggerHealthRestart()
    {
        if (!OperatingSystem.IsLinux()) return;
        string root = Path.Combine(Path.GetTempPath(), "shutdown-health-" + Guid.NewGuid().ToString("N"));
        var cache = typeof(MagnetarPaths).GetField("_cachedQuasarDirectory", BindingFlags.NonPublic | BindingFlags.Static)!;
        object? oldRoot = cache.GetValue(null);
        cache.SetValue(null, root);
        Process? adoptedProcess = null;
        using var child = Process.Start(new ProcessStartInfo("sleep", "60") { UseShellExecute = false })!;
        var lifetime = new StoppingLifetime();
        try
        {
            using var catalog = new DedicatedServerCatalog(NullLogger<DedicatedServerCatalog>.Instance);
            await catalog.UpsertAsync(new DedicatedServerDefinition
            {
                UniqueName = "live", WorldSaveName = "World", AutoStart = true, GoalState = DedicatedServerGoalState.On,
                AutoRestartOnUnhealthy = true, EnableHealthMonitoring = true,
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
            object? Invoke(string name, params object[] args) => typeof(DedicatedServerSupervisor)
                .GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(supervisor, args);
            Invoke("SyncDefinitions");
            Invoke("RestorePersistedRuntimeState");
            var states = (IDictionary)typeof(DedicatedServerSupervisor).GetField("_states", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(supervisor)!;
            object live = states["live"]!;
            var type = live.GetType();
            adoptedProcess = (Process)type.GetProperty("Process")!.GetValue(live)!;
            // A running server whose agent was online until shutdown closed its socket.
            type.GetProperty("AwaitingAdoptedAgent")!.SetValue(live, false);
            type.GetProperty("AgentWatchSinceUtc")!.SetValue(live, null);

            lifetime.StopApplication();
            await ((Task)Invoke("ReconcileAsync", CancellationToken.None)!).WaitAsync(TimeSpan.FromSeconds(5));

            Assert.False(child.HasExited);
            var result = Assert.Single(supervisor.GetSnapshots());
            Assert.Equal(DedicatedServerProcessState.Running, result.State);
            Assert.Equal(DedicatedServerHealthState.Unhealthy, result.HealthState);
            Assert.Null(result.LastRestart);
        }
        finally
        {
            if (adoptedProcess is not null) adoptedProcess.EnableRaisingEvents = false;
            if (!child.HasExited) child.Kill();
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            adoptedProcess?.Dispose();
            cache.SetValue(null, oldRoot);
            if (Directory.Exists(root)) Directory.Delete(root, true);
            lifetime.Dispose();
        }
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
