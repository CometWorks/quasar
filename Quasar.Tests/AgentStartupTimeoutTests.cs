using Quasar.Models;
using Quasar.Services;
using Xunit;

namespace Quasar.Tests;

public sealed class AgentStartupTimeoutTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"quasar-agent-startup-timeout-{Guid.NewGuid():N}");

    [Fact]
    public void LegacyDefaultMigratesToActivityAwareDefaults()
    {
        var definition = DedicatedServerCatalog.Normalize(new DedicatedServerDefinition
        {
            UniqueName = "legacy",
            AgentStartupGraceSeconds = DedicatedServerDefinition.LegacyAgentStartupGraceSeconds,
            AgentStartupHardLimitSeconds = 0,
        });

        Assert.Equal(DedicatedServerDefinition.DefaultAgentStartupGraceSeconds, definition.AgentStartupGraceSeconds);
        Assert.Equal(DedicatedServerDefinition.DefaultAgentStartupHardLimitSeconds, definition.AgentStartupHardLimitSeconds);
    }

    [Fact]
    public void ExplicitSoftTimeoutIsPreservedAfterMigration()
    {
        var definition = DedicatedServerCatalog.Normalize(new DedicatedServerDefinition
        {
            UniqueName = "configured",
            AgentStartupGraceSeconds = 180,
            AgentStartupHardLimitSeconds = 600,
        });

        Assert.Equal(180, definition.AgentStartupGraceSeconds);
        Assert.Equal(600, definition.AgentStartupHardLimitSeconds);
    }

    [Fact]
    public void LogActivityExtendsSoftTimeoutButNotHardLimit()
    {
        var started = DateTimeOffset.Parse("2026-09-11T10:00:00Z");

        Assert.Equal(
            DedicatedServerSupervisor.AgentStartupTimeoutKind.Waiting,
            DedicatedServerSupervisor.EvaluateAgentStartupTimeout(started, null, started.AddSeconds(59), 60, 600));
        Assert.Equal(
            DedicatedServerSupervisor.AgentStartupTimeoutKind.InactivityExpired,
            DedicatedServerSupervisor.EvaluateAgentStartupTimeout(started, null, started.AddSeconds(60), 60, 600));
        Assert.Equal(
            DedicatedServerSupervisor.AgentStartupTimeoutKind.Waiting,
            DedicatedServerSupervisor.EvaluateAgentStartupTimeout(
                started,
                started.AddSeconds(570),
                started.AddSeconds(599),
                60,
                600));
        Assert.Equal(
            DedicatedServerSupervisor.AgentStartupTimeoutKind.HardLimitExpired,
            DedicatedServerSupervisor.EvaluateAgentStartupTimeout(
                started,
                started.AddSeconds(599),
                started.AddSeconds(600),
                60,
                600));
    }

    [Fact]
    public void FindsNewestPostStartActivityAcrossDedicatedServerAndMagnetarLogs()
    {
        var dsPath = Path.Combine(_root, "DedicatedServer");
        var magnetarPath = Path.Combine(_root, "Magnetar");
        Directory.CreateDirectory(dsPath);
        Directory.CreateDirectory(magnetarPath);
        var started = DateTimeOffset.Parse("2026-09-11T10:00:00Z");

        WriteLog(dsPath, "SpaceEngineersDedicated-old.log", started.AddMinutes(-1));
        WriteLog(dsPath, "SpaceEngineersDedicated.log", started.AddSeconds(20));
        WriteLog(magnetarPath, "info_20260911_100000000.log", started.AddSeconds(30));

        var activity = DedicatedServerSupervisor.FindLatestStartupLogActivity(
            dsPath,
            magnetarPath,
            started,
            started.AddSeconds(40));

        Assert.NotNull(activity);
        Assert.Equal(started.AddSeconds(30), activity.Value.ObservedAtUtc);
        Assert.Equal("Magnetar", activity.Value.Kind);
    }

    [Fact]
    public void IgnoresLogsThatWereNotUpdatedAfterWatchStarted()
    {
        var dsPath = Path.Combine(_root, "DedicatedServer");
        Directory.CreateDirectory(dsPath);
        var started = DateTimeOffset.Parse("2026-09-11T10:00:00Z");
        WriteLog(dsPath, "SpaceEngineersDedicated.log", started.AddSeconds(-1));

        Assert.Null(DedicatedServerSupervisor.FindLatestStartupLogActivity(
            dsPath,
            Path.Combine(_root, "missing"),
            started,
            started.AddSeconds(30)));
    }

    private static void WriteLog(string directory, string fileName, DateTimeOffset lastWriteUtc)
    {
        var path = Path.Combine(directory, fileName);
        File.WriteAllText(path, "startup progress");
        File.SetLastWriteTimeUtc(path, lastWriteUtc.UtcDateTime);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
