using System.Diagnostics;
using Magnetar.Protocol.Transport;
using Quasar.Agent;
using Quasar.Services;
using Xunit;

namespace Quasar.Tests;

public sealed class AgentGameThreadCommandQueueTests
{
    private readonly List<string> _ran = [];
    private readonly List<string> _logs = [];
    private long _now = 1_000_000;

    [Fact]
    public async Task ManyCommandsRunInOneTickInArrivalOrderWithLifecycleFirst()
    {
        var queue = Queue(TimeSpan.FromSeconds(1));
        var chat = queue.Enqueue(Command(ServerCommandType.SendChat, "first"), CancellationToken.None);
        var kick = queue.Enqueue(Command(ServerCommandType.KickPlayer, "second"), CancellationToken.None);
        var save = queue.Enqueue(Command(ServerCommandType.SaveWorld, "save"), CancellationToken.None);
        var stop = queue.Enqueue(Command(ServerCommandType.StopServer, "stop"), CancellationToken.None);

        queue.Drain();

        Assert.Equal(["save", "stop", "first", "second"], _ran);
        Assert.All(await Task.WhenAll(chat, kick, save, stop), result => Assert.True(result.Success));
        Assert.Equal(0, queue.Pending);
    }

    [Fact]
    public async Task BudgetDefersTheRestToTheNextTickButAlwaysRunsOne()
    {
        // Each command "takes" 5 ms against a 2 ms budget.
        var queue = Queue(TimeSpan.FromMilliseconds(2), cost: TimeSpan.FromMilliseconds(5));
        var first = queue.Enqueue(Command(ServerCommandType.SendChat, "a"), CancellationToken.None);
        var second = queue.Enqueue(Command(ServerCommandType.SendChat, "b"), CancellationToken.None);

        queue.Drain();
        Assert.Equal(["a"], _ran);
        Assert.True((await first).Success);
        Assert.False(second.IsCompleted);

        queue.Drain();
        Assert.Equal(["a", "b"], _ran);
        Assert.True((await second).Success);
    }

    [Fact]
    public async Task CommandsQuasarStoppedWaitingForAreDroppedButSaveAndStopNeverExpire()
    {
        var queue = Queue(TimeSpan.FromSeconds(1));
        var poll = queue.Enqueue(Command(ServerCommandType.SendChat, "poll", timeoutMs: 10_000), CancellationToken.None);
        var unbounded = queue.Enqueue(Command(ServerCommandType.SendChat, "unbounded"), CancellationToken.None);
        var save = queue.Enqueue(Command(ServerCommandType.SaveWorld, "save", timeoutMs: 15_000), CancellationToken.None);

        Advance(TimeSpan.FromSeconds(40)); // a long stall
        queue.Drain();

        Assert.Equal(["save", "unbounded"], _ran);
        var expired = await poll;
        Assert.False(expired.Success);
        Assert.Contains("Quasar had stopped waiting", expired.Message);
        Assert.True((await save).Success);
        Assert.True((await unbounded).Success);
        Assert.Contains(_logs, line => line.StartsWith("The game thread was busy:"));
    }

    [Fact]
    public async Task ACancelledConnectionDropsItsQueuedCommands()
    {
        var queue = Queue(TimeSpan.FromSeconds(1));
        using var connection = new CancellationTokenSource();
        var pending = queue.Enqueue(Command(ServerCommandType.StopServer, "stop"), connection.Token);

        connection.Cancel();
        queue.Drain();

        Assert.Empty(_ran);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Fact]
    public async Task AFailingCommandAnswersWithItsErrorAndTheRestStillRun()
    {
        var queue = new GameThreadCommandQueue(
            command => command.Text == "boom" ? throw new InvalidOperationException("Kaboom") : Run(command),
            Result, _logs.Add, TimeSpan.FromSeconds(1), () => _now);
        var failing = queue.Enqueue(Command(ServerCommandType.SendChat, "boom"), CancellationToken.None);
        var next = queue.Enqueue(Command(ServerCommandType.SendChat, "after"), CancellationToken.None);

        queue.Drain();

        Assert.Equal("Kaboom", (await failing).Message);
        Assert.True((await next).Success);
    }

    private GameThreadCommandQueue Queue(TimeSpan budget, TimeSpan? cost = null) => new(
        command =>
        {
            if (cost is { } spent) Advance(spent);
            return Run(command);
        },
        Result, _logs.Add, budget, () => _now);

    private ServerCommandResult Run(ServerCommandEnvelope command)
    {
        _ran.Add(command.Text);
        return Result(command, true, "ok");
    }

    private static ServerCommandResult Result(ServerCommandEnvelope command, bool success, string message) =>
        new() { CommandId = command.CommandId, Success = success, Message = message };

    private void Advance(TimeSpan span) => _now += (long)(span.TotalSeconds * Stopwatch.Frequency);

    private static ServerCommandEnvelope Command(ServerCommandType type, string text, int timeoutMs = 0) =>
        new() { CommandType = type, Text = text, TimeoutMilliseconds = timeoutMs };
}

public sealed class AgentCommandTimeoutStampTests
{
    // The agent drops a command that waits past this; Quasar must send how long it will wait.
    [Fact]
    public async Task QuasarSendsItsWaitWithTheCommand()
    {
        var registry = new AgentRegistry(null!,
            new Quasar.Services.Analytics.MetricsStoreService(null!, new Quasar.Services.Analytics.AnalyticsStoreOptions(),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<Quasar.Services.Analytics.MetricsStoreService>.Instance),
            new Quasar.Services.Analytics.ProfilerStoreService(), new Quasar.Services.Analytics.PluginStatsStoreService());
        ServerCommandEnvelope? sent = null;
        registry.UpsertHello(new Magnetar.Protocol.Model.AgentHello { AgentId = "agent", UniqueName = "alpha", HostId = "host" },
            "connection", (message, _) => { sent = message.Command; return Task.CompletedTask; });

        var command = new ServerCommandEnvelope { AgentId = "agent", CommandType = ServerCommandType.SendChat };
        await Assert.ThrowsAsync<TimeoutException>(() => registry.SendCommandAndWaitAsync(command, TimeSpan.FromMilliseconds(150)));

        Assert.Equal(150, sent!.TimeoutMilliseconds);
    }
}
