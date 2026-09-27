using System.Text.Json;
using Discord;
using Magnetar.Protocol.Model;
using Quasar.Models;
using Quasar.Services;
using Quasar.Services.Discord;
using Xunit;

namespace Quasar.Tests;

public sealed class DiscordPresenceTests
{
    private static readonly DiscordPresenceServer[] Servers =
    [
        new("alpha", "Alpha", true, 3, "Running"),
        new("cluster:beta", "Beta", true, 8, "Serving"),
        new("private", "Private", false, 5, "Faulted", Issue: true),
    ];

    [Fact]
    public void SelectionFiltersCountsNamesHealthAndAutomaticIndicatorTogether()
    {
        var options = new DiscordPresenceOptions { AllServers = false, SelectedTargets = ["ALPHA", "cluster:beta"], ShowServerNames = true };
        var result = DiscordPresence.Build(options, Servers);
        Assert.Equal(UserStatus.Online, result.Status);
        Assert.Equal("2/2 servers online, 11 players, Alpha, Beta", result.Activity);
        Assert.DoesNotContain("Private", result.Activity);
    }

    [Fact]
    public void ClusterContributesOneServerAndDeletedSelectionNeverFallsBackToAll()
    {
        var options = new DiscordPresenceOptions { AllServers = false, SelectedTargets = ["cluster:beta"] };
        Assert.Equal("1/1 servers online, 8 players", DiscordPresence.Build(options, Servers).Activity);
        options.SelectedTargets = ["removed"];
        Assert.Null(DiscordPresence.Build(options, Servers).Activity);
        Assert.Equal(UserStatus.Idle, DiscordPresence.Build(options, Servers).Status);
    }

    [Fact]
    public void StatesCanBeShownWithoutCountsOrHealth()
    {
        var result = DiscordPresence.Build(new() { ShowServerCount = false, ShowPlayerCount = false,
            ShowHealth = false, ShowServerStates = true }, Servers);
        Assert.Equal("Alpha: Running, Beta: Serving, Private: Faulted", result.Activity);
        Assert.Equal(UserStatus.DoNotDisturb, result.Status);
    }

    [Theory]
    [InlineData(DiscordPresenceStatus.Online, UserStatus.Online)]
    [InlineData(DiscordPresenceStatus.Idle, UserStatus.Idle)]
    [InlineData(DiscordPresenceStatus.DoNotDisturb, UserStatus.DoNotDisturb)]
    [InlineData(DiscordPresenceStatus.Invisible, UserStatus.Invisible)]
    public void FixedIndicatorAndHiddenActivityAreIndependent(DiscordPresenceStatus configured, UserStatus expected)
    {
        var result = DiscordPresence.Build(new() { Status = configured, ShowActivity = false,
            ActivityType = DiscordPresenceActivity.Playing }, Servers);
        Assert.Equal(expected, result.Status);
        Assert.Null(result.Activity);
        Assert.Equal(ActivityType.Playing, result.ActivityType);
    }

    [Fact]
    public void UnavailableTelemetryIsNotReportedAsZeroPlayers()
    {
        var result = DiscordPresence.Build(new(), [new("cluster:x", "X", false, null, "Unavailable", Warning: true)]);
        Assert.Equal("0/1 servers online, player count unavailable, 1 warnings", result.Activity);
    }

    [Fact]
    public void StandaloneTotalsExcludeClusterAgentsAndUnselectedOrStaleAgents()
    {
        var server = new DedicatedServerRuntimeSnapshot { UniqueName = "alpha", State = DedicatedServerProcessState.Running };
        var agent = new AgentRuntimeState { IsConnected = true, LastSnapshotReceivedUtc = DateTimeOffset.UtcNow,
            Snapshot = new AgentSnapshot { UniqueName = "alpha", Metrics = new() { PlayersOnline = 3 } } };
        var cluster = new AgentRuntimeState { IsConnected = true, LastSnapshotReceivedUtc = DateTimeOffset.UtcNow,
            Snapshot = new AgentSnapshot { UniqueName = "alpha", ClusterMode = true, Metrics = new() { PlayersOnline = 99 } } };
        Assert.Equal(3, DiscordPresence.Standalone([server], [agent, cluster]).Single().Players);
        agent.LastSnapshotReceivedUtc = DateTimeOffset.UtcNow.AddMinutes(-1);
        Assert.Null(DiscordPresence.Standalone([server], [agent, cluster]).Single().Players);
    }

    [Fact]
    public void OptionsRoundTripAndCloneKeepIndependentSelectionsAndDefaults()
    {
        var old = DiscordOptions.Normalize(JsonSerializer.Deserialize<DiscordOptions>("{}"));
        Assert.True(old.Presence.AllServers);
        Assert.True(old.Presence.ShowActivity);
        var options = new DiscordOptions { Presence = new() { AllServers = false, SelectedTargets = ["alpha", "cluster:beta"],
            Status = DiscordPresenceStatus.Idle, ShowPlayerCount = false } };
        var copy = DiscordOptions.Normalize(JsonSerializer.Deserialize<DiscordOptions>(JsonSerializer.Serialize(options)));
        copy.Presence.SelectedTargets.Clear();
        Assert.Equal(2, options.Presence.SelectedTargets.Count);
        var clone = options.Clone();
        clone.Presence.SelectedTargets.Clear();
        Assert.Equal(2, options.Presence.SelectedTargets.Count);
        Assert.Equal(DiscordPresenceStatus.Idle, copy.Presence.Status);
        Assert.False(copy.Presence.ShowPlayerCount);
    }

    [Fact]
    public void LongNamesRespectDiscordLimitWithoutSplittingUnicode()
    {
        var result = DiscordPresence.Build(new() { ShowServerCount = false, ShowPlayerCount = false, ShowHealth = false,
            ShowServerNames = true }, [new("a", new string('x', 126) + "🚀long", true, 0, "Running")]);
        Assert.True(result.Activity!.Length <= 128);
        Assert.Equal(new string('x', 126) + "…", result.Activity);
    }
}
