using Magnetar.Protocol.Model;
using Microsoft.Extensions.Logging.Abstractions;
using Quasar.Models;
using Quasar.Services;
using Quasar.Services.Discord;
using Xunit;

namespace Quasar.Tests;

public sealed class DiscordStatusRelayTests
{
    private readonly DiscordStatusRelayService _relay = new(null!, null!, null!, NullLogger<DiscordStatusRelayService>.Instance);
    private readonly DiscordServerOptions _options = new() { UniqueName = "survival" };
    private readonly DedicatedServerRuntimeSnapshot _server = new() { UniqueName = "survival" };

    [Fact]
    public void LifecycleNotifiesOncePerTransitionAndRestartRequest()
    {
        Assert.Empty(Observe());
        _server.State = DedicatedServerProcessState.Starting;
        Assert.Empty(Observe());
        _server.State = DedicatedServerProcessState.Running;
        Assert.Equal("[survival] ✅ Server Started!", Assert.Single(Observe()));
        Assert.Empty(Observe());

        // The supervisor records intent before changing the process state.
        _server.LastRestart = new() { RequestedAtUtc = DateTimeOffset.UtcNow };
        Assert.Equal("[survival] 🔄 Server is going to Restart!", Assert.Single(Observe()));
        _server.State = DedicatedServerProcessState.Restarting;
        Assert.Empty(Observe());
        _server.State = DedicatedServerProcessState.Stopped;
        Assert.Equal("[survival] ❌ Server Closed!", Assert.Single(Observe()));
        Assert.Empty(Observe());
        _server.State = DedicatedServerProcessState.Starting;
        Assert.Empty(Observe());
        _server.State = DedicatedServerProcessState.Running;
        _server.LastRestart.Outcome = DedicatedServerRestartOutcome.Recovered;
        Assert.Equal("[survival] ✅ Server Started!", Assert.Single(Observe()));
    }

    [Theory]
    [InlineData(DedicatedServerProcessState.Crashed)]
    [InlineData(DedicatedServerProcessState.Faulted)]
    [InlineData(DedicatedServerProcessState.Stopped)]
    public void ClosureDoesNotRepeatAcrossTerminalStates(DedicatedServerProcessState state)
    {
        _server.State = DedicatedServerProcessState.Running;
        Assert.Empty(Observe());
        _server.State = state;
        Assert.Equal("[survival] ❌ Server Closed!", Assert.Single(Observe()));
        _server.State = DedicatedServerProcessState.Stopped;
        Assert.Empty(Observe());
    }

    [Fact]
    public void PlayersAreComparedByIdAndLeaveUsesLastKnownName()
    {
        _server.State = DedicatedServerProcessState.Running;
        Assert.Empty(Observe(Agent()));
        Assert.Equal("[survival] 🚀 **artur** connected to the server!", Assert.Single(Observe(Agent("artur"))));
        Assert.Empty(Observe(Agent("renamed")));
        Assert.Equal("[survival] ☄️ **renamed** disconnected from server!", Assert.Single(Observe(Agent())));
        Assert.Empty(Observe(Agent()));
    }

    [Fact]
    public void BotStartupAndResetBaselineExistingPlayersAndServer()
    {
        _server.State = DedicatedServerProcessState.Running;
        Assert.Empty(Observe(Agent("artur")));
        _relay.Reset();
        Assert.Empty(Observe(Agent("artur")));
    }

    [Fact]
    public void AgentDisconnectAndReconnectDoNotImplyPlayerOrServerEvents()
    {
        _server.State = DedicatedServerProcessState.Running;
        Observe(Agent("artur"));
        Assert.Empty(Observe());
        Assert.Empty(Observe(Agent("someone else")));
        var reconnected = Agent();
        reconnected.ConnectionId = "new-connection";
        Assert.Empty(Observe(reconnected));
    }

    [Fact]
    public void ShutdownDoesNotReportEveryoneAsDisconnected()
    {
        _server.State = DedicatedServerProcessState.Running;
        Observe(Agent("artur"));
        _server.State = DedicatedServerProcessState.Stopping;
        Assert.Empty(Observe(Agent()));
        _server.State = DedicatedServerProcessState.Stopped;
        Assert.Equal("[survival] ❌ Server Closed!", Assert.Single(Observe()));
    }

    [Fact]
    public void NotificationTogglesAreIndependentAndDoNotReplayDisabledChanges()
    {
        _options.EnableServerNotifications = false;
        Observe();
        _server.State = DedicatedServerProcessState.Running;
        Assert.Empty(Observe(Agent()));
        Assert.Single(Observe(Agent("artur")));
        _options.EnablePlayerNotifications = false;
        Assert.Empty(Observe(Agent()));
        _options.EnableServerNotifications = true;
        _options.EnablePlayerNotifications = true;
        Assert.Empty(Observe(Agent()));
        _server.State = DedicatedServerProcessState.Stopped;
        Assert.Single(Observe());
    }

    [Fact]
    public void PlayerNamesCannotBreakBoldFormatting()
    {
        _server.State = DedicatedServerProcessState.Running;
        Observe(Agent());
        var message = Assert.Single(Observe(Agent("art**ur\n")));
        Assert.Contains("art\\*\\*ur", message);
        Assert.DoesNotContain("\n", message);
    }

    [Fact]
    public void StatusOptionsSurviveCloneNormalizationAndLegacyJson()
    {
        var legacy = System.Text.Json.JsonSerializer.Deserialize<DiscordServerOptions>("{}")!;
        Assert.True(legacy.EnablePlayerNotifications);
        Assert.True(legacy.EnableServerNotifications);
        Assert.Null(legacy.StatusChannelId);

        _options.StatusChannelId = 123;
        _options.EnablePlayerNotifications = false;
        _options.EnableServerNotifications = false;
        var copy = DiscordServerOptions.Normalize(_options.Clone());
        Assert.Equal(123UL, copy.StatusChannelId);
        Assert.False(copy.EnablePlayerNotifications);
        Assert.False(copy.EnableServerNotifications);
        copy.StatusChannelId = 0;
        Assert.Null(DiscordServerOptions.Normalize(copy).StatusChannelId);
    }

    private IReadOnlyList<string> Observe(AgentRuntimeState? agent = null) => _relay.Observe(_options, _server, agent);

    private static AgentRuntimeState Agent(params string[] names) => new()
    {
        IsConnected = true,
        ConnectionId = "connection",
        Snapshot = new AgentSnapshot
        {
            UniqueName = "survival",
            IsRunning = true,
            Players = names.Select((name, index) => new PlayerSnapshot { SteamId = index + 1, DisplayName = name }).ToList(),
        },
    };
}
