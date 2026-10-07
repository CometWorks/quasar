using System.Security.Claims;
using System.Text.Json;
using Magnetar.Protocol.Model;
using Quasar.Models;
using Quasar.Services;
using Quasar.Services.Auth;
using Quasar.Services.Updates;
using Xunit;

namespace Quasar.Tests;

public sealed class InstanceNotificationTests
{
    private readonly InstanceNotificationMonitor _monitor = new();
    private readonly NotificationPreferences _options = new() { Crashes = true, LowSimSpeed = true,
        SustainedSeconds = 5, CooldownSeconds = 30, Recoveries = true };
    private readonly DedicatedServerRuntimeSnapshot _server = new() { UniqueName = "alpha", State = DedicatedServerProcessState.Running };
    private readonly AgentRuntimeState _agent = new() { IsConnected = true, ConnectionId = "connection",
        Snapshot = new() { UniqueName = "alpha", IsRunning = true, Metrics = new() { SimSpeed = 0.5f } } };
    private readonly DateTimeOffset _start = DateTimeOffset.Parse("2026-10-07T12:00:00Z");
    private static ClaimsPrincipal Viewer => new(new ClaimsIdentity([new Claim(ClaimTypes.Role, QuasarRoles.Viewer)], "test"));

    [Fact]
    public void LegacyPushStateKeepsUpdatesAndLeavesInstanceAlertsOptIn()
    {
        var state = JsonSerializer.Deserialize<PushNotificationService.PushState>(
            """{"publicKey":"public","privateKey":"private","subscriptions":[]}""", new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var options = PushNotificationService.Preferences(state, "Steam", "account");
        options.Validate();
        Assert.True(options.Updates);
        Assert.False(options.Crashes || options.HealthRestarts || options.LowSimSpeed || options.HighCpu
            || options.HighMemory || options.UnsavedWorld || options.Recoveries);
    }

    [Fact]
    public void PreferencesRoundTripAndRemainScopedToProviderAndAccount()
    {
        var state = new PushNotificationService.PushState("public", "private", [],
            [new("Steam", "first", _options with { Updates = false }), new("Steam", "second", new())]);
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var loaded = JsonSerializer.Deserialize<PushNotificationService.PushState>(JsonSerializer.Serialize(state, json), json)!;
        Assert.Equal(_options.SimSpeedThreshold, PushNotificationService.Preferences(loaded, "Steam", "first").SimSpeedThreshold);
        Assert.False(PushNotificationService.Preferences(loaded, "Steam", "first").Updates);
        Assert.False(PushNotificationService.Preferences(loaded, "Steam", "second").Crashes);
        Assert.False(PushNotificationService.Preferences(loaded, "other-provider", "first").Crashes);
    }

    [Fact]
    public void InvalidThresholdsAreRejectedAtServiceBoundary()
    {
        Assert.Throws<InvalidDataException>(() => (_options with { SimSpeedThreshold = float.NaN }).Validate());
        Assert.Throws<InvalidDataException>(() => (_options with { SimSpeedThreshold = 0 }).Validate());
        Assert.Throws<InvalidDataException>(() => (_options with { CpuThreshold = float.PositiveInfinity }).Validate());
        Assert.Throws<InvalidDataException>(() => (_options with { MemoryThresholdMb = 0 }).Validate());
        Assert.Throws<InvalidDataException>(() => (_options with { UnsavedThresholdMinutes = 0 }).Validate());
        Assert.Throws<InvalidDataException>(() => (_options with { SustainedSeconds = 4 }).Validate());
        Assert.Throws<InvalidDataException>(() => (_options with { CooldownSeconds = 29 }).Validate());
    }

    [Fact]
    public void CrashRecoveryIsCapturedBeforeInstanceRestartsAndDoesNotRepeat()
    {
        Observe(0);
        _server.State = DedicatedServerProcessState.Restarting;
        _server.LastRestart = new() { Cause = DedicatedServerRestartCause.CrashRecovery, RequestedAtUtc = _start.AddSeconds(1) };
        _server.LastMessage = "Process exited with code 1. Restarting.";
        Observe(1);
        _server.State = DedicatedServerProcessState.Running;
        Observe(2);
        Observe(3);
        var notice = Assert.Single(Notices(3));
        Assert.Contains("crashed", notice.Title);
        Assert.Equal("/", notice.Url);
        Assert.Contains("code 1", notice.Body);
        var subscription = new PushNotificationService.StoredSubscription("Steam", "account", "endpoint", "key", "auth", notice.Key);
        Assert.Empty(PushNotificationService.PendingNotices(Notices(3), subscription));
    }

    [Fact]
    public void FaultsNotifyOnceButStartupAndRequestedStopsDoNotReplay()
    {
        _server.State = DedicatedServerProcessState.Faulted;
        Observe(0);
        Assert.Empty(Notices(0));
        _server.State = DedicatedServerProcessState.Running;
        Observe(1);
        _server.State = DedicatedServerProcessState.Stopping;
        Observe(2);
        _server.State = DedicatedServerProcessState.Stopped;
        Observe(3);
        Assert.Empty(Notices(3));
        _server.State = DedicatedServerProcessState.Faulted;
        Observe(4);
        Observe(4);
        Assert.Single(Notices(4));
    }

    [Fact]
    public void HealthRestartsHaveTheirOwnOptIn()
    {
        _options.HealthRestarts = true;
        Observe(0);
        _server.LastRestart = new() { Cause = DedicatedServerRestartCause.Manual, RequestedAtUtc = _start.AddSeconds(1) };
        Observe(1);
        Assert.Empty(Notices(1));
        _server.LastRestart = new() { Cause = DedicatedServerRestartCause.HealthPolicy, RequestedAtUtc = _start.AddSeconds(2), Reason = "Simulation stalled." };
        Observe(2);
        Assert.Equal("Simulation stalled.", Assert.Single(Notices(2)).Body);
    }

    [Fact]
    public void MetricsRequireSustainedSamplesRespectCooldownAndNotifyRecoveryOnce()
    {
        Observe(0);
        Observe(4);
        Assert.Empty(Notices(4));
        Observe(5);
        Observe(5);
        Assert.Single(Notices(5));
        Observe(20);
        Assert.Single(Notices(20));
        Observe(35);
        Assert.Equal(2, Notices(35).Length);
        _agent.Snapshot!.Metrics.SimSpeed = 1;
        Observe(36);
        Observe(37);
        Assert.Contains("recovered", Assert.Single(Notices(37).Where(n => n.Title.Contains("recovered"))).Title);
        _agent.Snapshot.Metrics.SimSpeed = 0.5f;
        Observe(38);
        Observe(43);
        Assert.Equal(3, Notices(43).Length); // Cooldown also spans brief recoveries.
    }

    [Fact]
    public void MissingStaleAndDisconnectedTelemetryCannotTriggerOrRecover()
    {
        _options.HighMemory = true;
        _options.LowSimSpeed = false;
        _agent.Snapshot!.Metrics.MemoryWorkingSetMb = 9000;
        Observe(0);
        Observe(5);
        Assert.Single(Notices(5));
        _agent.Snapshot.Metrics.MemoryWorkingSetMb = null;
        Observe(6);
        Assert.Single(Notices(6));
        _agent.IsConnected = false;
        Observe(7);
        _agent.IsConnected = true;
        _agent.Snapshot.Metrics.MemoryWorkingSetMb = 9000;
        _agent.Snapshot.CapturedAtUtc = _start;
        _agent.LastSnapshotReceivedUtc = _start;
        ObserveAt(60);
        Assert.Single(Notices(60));
        Observe(61);
        Observe(65);
        Assert.Single(Notices(65));
        Observe(66);
        Assert.Equal(2, Notices(66).Length);
    }

    [Fact]
    public void AllUsefulMetricRulesAreIndependentAndCpuCanExceedOneCore()
    {
        _options.HighCpu = _options.HighMemory = _options.UnsavedWorld = true;
        var metrics = _agent.Snapshot!.Metrics;
        metrics.ServerCpuLoadPercent = 250;
        metrics.MemoryWorkingSetMb = 9000;
        metrics.UnsavedGameTimeSeconds = 1000;
        Observe(0);
        Observe(5);
        Assert.Equal(4, Notices(5).Length);
        Assert.Contains(Notices(5), n => n.Title.Contains("CPU") && n.Body.Contains("one logical CPU"));
    }

    [Fact]
    public void UnknownAndStaleSamplesResetDurationButPreserveCooldown()
    {
        _options.LowSimSpeed = false;
        _options.HighMemory = true;
        _options.CooldownSeconds = 300;
        _agent.Snapshot!.Metrics.MemoryWorkingSetMb = 9000;
        Observe(0);
        Observe(5);
        _agent.Snapshot.Metrics.MemoryWorkingSetMb = null;
        Observe(6);
        _agent.Snapshot.Metrics.MemoryWorkingSetMb = 9000;
        Observe(7);
        Observe(12);
        Assert.Single(Notices(12));
        ObserveAt(50); // Re-reading old telemetry must not restart the cooldown.
        Observe(51);
        Observe(56);
        Assert.Single(Notices(56));
        for (var seconds = 76; seconds <= 316; seconds += 20) Observe(seconds);
        Assert.Equal(2, Notices(316).Length);
    }

    [Fact]
    public void ClusterMetricsRequireUnambiguousIdentityAndRespectAccess()
    {
        var snapshot = _agent.Snapshot!;
        snapshot.ClusterMode = true;
        snapshot.ClusterId = "cluster-alpha";
        snapshot.ClusterSlot = "worker";
        snapshot.ClusterNodeId = "node-1";
        snapshot.ClusterEpoch = 1;
        var clusters = new[] { new ClusterDefinition { UniqueName = snapshot.ClusterId } };
        Observe(0, clusters: clusters);
        Observe(5, clusters: clusters);
        Assert.Equal("/clusters/cluster-alpha", Assert.Single(Notices(5)).Url);
        var restricted = new ClaimsPrincipal(new ClaimsIdentity([new Claim(QuasarClaimTypes.Provider, QuasarAuthSchemes.ServicePrincipal)], "test"));
        Assert.Empty(_monitor.GetNotices("Steam", "account", restricted, _start.AddSeconds(5)));
        _monitor.Reset("Steam", "account");
        Observe(6, clusters: clusters, agents: [_agent, _agent.Clone()]);
        Observe(11, clusters: clusters, agents: [_agent, _agent.Clone()]);
        Assert.Empty(Notices(11));
    }

    [Fact]
    public void AccountsHaveIndependentThresholdsAndSavingClearsOnlyTheirHistory()
    {
        Observe(0);
        Observe(0, subject: "other", options: _options with { SimSpeedThreshold = 0.1f });
        Observe(5);
        Observe(5, subject: "other", options: _options with { SimSpeedThreshold = 0.1f });
        Assert.Single(Notices(5));
        Assert.Empty(_monitor.GetNotices("Steam", "other", Viewer, _start.AddSeconds(5)));
        _monitor.Reset("Steam", "other");
        Assert.Single(Notices(5));
        _monitor.Reset("Steam", "account");
        Assert.Empty(Notices(5));
    }

    [Fact]
    public void IncidentHistoryExpiresAndIsBounded()
    {
        _options.LowSimSpeed = false;
        Observe(0);
        for (var i = 1; i <= 105; i++)
        {
            _server.LastRestart = new() { Cause = DedicatedServerRestartCause.CrashRecovery, RequestedAtUtc = _start.AddSeconds(i) };
            Observe(i);
        }
        Assert.Equal(100, Notices(105).Length);
        Assert.Empty(Notices(86505));
    }

    private UpdateNotice[] Notices(int seconds) => _monitor.GetNotices("Steam", "account", Viewer, _start.AddSeconds(seconds));
    private void Observe(int seconds, string subject = "account", NotificationPreferences? options = null,
        ClusterDefinition[]? clusters = null, AgentRuntimeState[]? agents = null)
    {
        _agent.Snapshot!.CapturedAtUtc = _agent.LastSnapshotReceivedUtc = _start.AddSeconds(seconds);
        _monitor.Observe("Steam", subject, options ?? _options, [_server], agents ?? [_agent], clusters ?? [], _start.AddSeconds(seconds));
    }
    private void ObserveAt(int seconds) => _monitor.Observe("Steam", "account", _options, [_server], [_agent], [], _start.AddSeconds(seconds));
}
