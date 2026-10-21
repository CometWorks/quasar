using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Magnetar.Protocol.Runtime;
using Microsoft.Extensions.Logging.Abstractions;
using Quasar.Models;
using Quasar.Services;
using Quasar.Services.Backup;
using Xunit;

namespace Quasar.Tests;

[Collection("Exact server creation")]
public sealed class AgentUpgradeSafetyTests
{
    [Theory]
    [InlineData(DedicatedServerProcessState.Starting)]
    [InlineData(DedicatedServerProcessState.Running)]
    public async Task RefusedAdoptedAgentCannotTriggerHealthOrPlannedRestart(DedicatedServerProcessState state)
    {
        if (!OperatingSystem.IsLinux()) return;
        string root = Path.Combine(Path.GetTempPath(), "agent-upgrade-" + Guid.NewGuid().ToString("N"));
        var cache = typeof(MagnetarPaths).GetField("_cachedQuasarDirectory", BindingFlags.NonPublic | BindingFlags.Static)!;
        object? oldRoot = cache.GetValue(null);
        cache.SetValue(null, root);
        Process? adoptedProcess = null;
        using var child = Process.Start(new ProcessStartInfo("sleep", "60") { UseShellExecute = false })!;
        try
        {
            using var catalog = new DedicatedServerCatalog(NullLogger<DedicatedServerCatalog>.Instance);
            await catalog.UpsertAsync(new DedicatedServerDefinition
            {
                UniqueName = "old", WorldSaveName = "World", AutoStart = true, GoalState = DedicatedServerGoalState.On,
                AutoRestartOnUnhealthy = true, EnableHealthMonitoring = true,
                AgentStartupGraceSeconds = 60, AgentStartupHardLimitSeconds = 600,
            });
            string path = MagnetarPaths.GetQuasarSupervisorStatePath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
            {
                servers = new[] { new { uniqueName = "old", processId = child.Id, state = (int)state,
                    startedAtUtc = DateTimeOffset.UtcNow.AddHours(-2) } },
            }));
            using var supervisor = new DedicatedServerSupervisor(catalog, new(null!, null!, null!, null!),
                null!, null!, null!, new WebServiceOptions(), new ServerRestoreCoordinator(),
                NullLogger<DedicatedServerSupervisor>.Instance);
            object? Invoke(string name, params object[] args) => typeof(DedicatedServerSupervisor)
                .GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(supervisor, args);
            Invoke("SyncDefinitions");
            Invoke("RestorePersistedRuntimeState");
            var states = (IDictionary)typeof(DedicatedServerSupervisor).GetField("_states", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(supervisor)!;
            object adopted = states["old"]!;
            var type = adopted.GetType();
            adoptedProcess = (Process)type.GetProperty("Process")!.GetValue(adopted)!;
            Assert.True((bool)type.GetProperty("AwaitingAdoptedAgent")!.GetValue(adopted)!);
            type.GetProperty("AgentWatchSinceUtc")!.SetValue(adopted, DateTimeOffset.UtcNow.AddHours(-1));
            await ((Task)Invoke("ReconcileAsync", CancellationToken.None)!).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(child.HasExited);
            var result = Assert.Single(supervisor.GetSnapshots());
            Assert.Equal(state, result.State);
            Assert.Equal(DedicatedServerHealthState.Unhealthy, result.HealthState);
            Assert.Null(result.LastRestart);
            // Planned maintenance must also wait for authenticated telemetry or explicit admin action.
            Assert.False((bool)Invoke("CanRunPlannedRestart", adopted)!);
        }
        finally
        {
            if (adoptedProcess is not null) adoptedProcess.EnableRaisingEvents = false;
            if (!child.HasExited) child.Kill();
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            adoptedProcess?.Dispose();
            cache.SetValue(null, oldRoot);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
