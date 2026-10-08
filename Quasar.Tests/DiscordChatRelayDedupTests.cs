using Magnetar.Protocol.Model;
using Microsoft.Extensions.Logging.Abstractions;
using Quasar.Services.Discord;
using Xunit;

namespace Quasar.Tests;

public sealed class DiscordChatRelayDedupTests
{
    private readonly DiscordChatRelayService _relay = new(null!, null!, NullLogger<DiscordChatRelayService>.Instance);
    private readonly DiscordServerOptions _options = new() { UniqueName = "survival", ChatRelayChannelId = 10 };
    private readonly long _timestamp = DateTimeOffset.UtcNow.AddSeconds(1).UtcTicks;

    [Fact]
    public void RepeatedSnapshotsRelayEachGameMessageOnce()
    {
        Observe();
        var message = Message("Testing");
        Assert.Equal("**Player**: Testing", Assert.Single(Observe(message)).Content);
        for (var i = 0; i < 100; i++)
            Assert.Empty(Observe(message));

        // Repeating the same text is still a new player message when its timestamp changes.
        var next = Message("Testing", 1);
        Assert.Single(Observe(message, next));
        Assert.Empty(Observe(message, next));
    }

    [Fact]
    public void ConcurrentSnapshotObserversDoNotDuplicateMessages()
    {
        Observe();
        var message = Message("Testing");
        var counts = new int[50];
        Parallel.For(0, counts.Length, i => counts[i] = Observe(message).Count);
        Assert.Equal(1, counts.Sum());
    }

    [Fact]
    public void StartupAndResetBaselineExistingChat()
    {
        var message = Message("Old chat");
        Assert.Empty(Observe(message));
        Assert.Single(Observe(message, Message("New chat", 1)));
        _relay.Reset();
        Assert.Empty(Observe(message, Message("New chat", 1)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EveryCaptureOfForwardedDiscordChatIsSuppressed(bool quasarBroadcast)
    {
        Observe();
        const string forwarded = "[Discord] Player: Le teste";
        _relay.TrackDiscordToGameMessage("SURVIVAL", forwarded);

        var firstEcho = Echo(0);
        Assert.Empty(Observe(firstEcho));
        // A second game callback has a new timestamp, even though it echoes the same send.
        var secondEcho = Echo(1);
        Assert.Empty(Observe(firstEcho, secondEcho));
        Assert.Empty(Observe(firstEcho, secondEcho, Echo(2)));
        Assert.Single(Observe(Message("Regular player chat", 3)));

        ChatMessageSnapshot Echo(long offset)
        {
            var echo = Message(forwarded, offset);
            echo.IsQuasarBroadcast = quasarBroadcast;
            echo.IsServerMessage = quasarBroadcast;
            if (quasarBroadcast) { echo.SteamId = 0; echo.AuthorName = "Server"; }
            return echo;
        }
    }

    [Theory]
    [InlineData(ChatMessageChannel.Whisper)]
    [InlineData(ChatMessageChannel.Faction)]
    [InlineData(ChatMessageChannel.GlobalScripted)]
    [InlineData(ChatMessageChannel.Unknown)]
    public void BroadcastFlagDoesNotPermitPrivateOrUnsupportedServerChat(ChatMessageChannel channel)
    {
        Observe();
        _options.AdminChannelId = 20;
        _options.FactionChannels.Add(new() { FactionTag = "SPRT", ChannelId = 30 });
        var message = Message("Server message");
        message.Channel = channel;
        message.FactionTag = "SPRT";
        message.IsServerMessage = true;
        message.IsQuasarBroadcast = true;
        Assert.Empty(Observe(message));
    }

    [Fact]
    public void EchoSuppressionIsScopedToItsServer()
    {
        Observe();
        _relay.TrackDiscordToGameMessage("other-server", "[Discord] Player: hello");
        Assert.Single(Observe(Message("[Discord] Player: hello")));
    }

    [Fact]
    public void ServerAuthoredMessagesAreNeverRelayed()
    {
        Observe();
        var server = Message("Server message");
        server.IsServerMessage = true;
        Assert.Empty(Observe(server));
        var noPlayer = Message("Server message", 1);
        noPlayer.SteamId = 0;
        Assert.Empty(Observe(noPlayer));
    }

    private IReadOnlyList<DiscordChatRelayService.RelayMessage> Observe(params ChatMessageSnapshot[] messages) =>
        _relay.CollectFreshMessages(_options, messages);

    private ChatMessageSnapshot Message(string content, long offset = 0) => new()
    {
        SteamId = 123,
        AuthorName = "Player",
        Content = content,
        Channel = ChatMessageChannel.Global,
        TimestampTicksUtc = _timestamp + offset,
    };
}
