using Magnetar.Protocol.Model;
using Quasar.Services.Discord;
using Xunit;

namespace Quasar.Tests;

public sealed class DiscordChatRelayRoutingTests
{
    private static readonly DiscordServerOptions ServerOptions = new()
    {
        UniqueName = "survival",
        ChatRelayChannelId = 10,
        AdminChannelId = 20,
        FactionChannels =
        [
            new DiscordFactionChannelOptions { FactionTag = "SPRT", ChannelId = 30 },
        ],
    };

    [Fact]
    public void GlobalChatRoutesOnlyToGlobalRelay()
    {
        var message = new ChatMessageSnapshot { Channel = ChatMessageChannel.Global };

        Assert.Equal(10UL, DiscordChatRelayService.ResolveRelayChannelId(ServerOptions, message));
        Assert.False(DiscordChatRelayService.RequiresAdminOnlyChannel(message.Channel));
    }

    [Fact]
    public void WhisperRoutesOnlyToExplicitAdminChannel()
    {
        var message = new ChatMessageSnapshot { Channel = ChatMessageChannel.Whisper };

        Assert.Equal(20UL, DiscordChatRelayService.ResolveRelayChannelId(ServerOptions, message));
        Assert.True(DiscordChatRelayService.RequiresAdminOnlyChannel(message.Channel));

        var withoutAdminChannel = ServerOptions.Clone();
        withoutAdminChannel.AdminChannelId = null;
        Assert.Null(DiscordChatRelayService.ResolveRelayChannelId(withoutAdminChannel, message));
    }

    [Fact]
    public void FactionChatRequiresMatchingConfiguredFaction()
    {
        var configured = new ChatMessageSnapshot
        {
            Channel = ChatMessageChannel.Faction,
            FactionTag = "sprt",
        };
        var unconfigured = new ChatMessageSnapshot
        {
            Channel = ChatMessageChannel.Faction,
            FactionTag = "RED",
        };

        Assert.Equal(30UL, DiscordChatRelayService.ResolveRelayChannelId(ServerOptions, configured));
        Assert.Null(DiscordChatRelayService.ResolveRelayChannelId(ServerOptions, unconfigured));
        Assert.True(DiscordChatRelayService.RequiresAdminOnlyChannel(configured.Channel));
    }

    [Fact]
    public void DiscordFactionChannelResolvesItsBinding()
    {
        var binding = DiscordCommandRouter.ResolveFactionBinding(ServerOptions, 30);

        Assert.NotNull(binding);
        Assert.Equal("SPRT", binding.FactionTag);
        Assert.Null(DiscordCommandRouter.ResolveFactionBinding(ServerOptions, 31));
    }

    [Theory]
    [InlineData(ChatMessageChannel.Unknown)]
    [InlineData(ChatMessageChannel.GlobalScripted)]
    [InlineData(ChatMessageChannel.ChatBot)]
    [InlineData(ChatMessageChannel.BroadcastController)]
    public void NonPublicChannelsNeverFallThroughToGlobal(ChatMessageChannel channel)
    {
        var message = new ChatMessageSnapshot { Channel = channel };

        Assert.Null(DiscordChatRelayService.ResolveRelayChannelId(ServerOptions, message));
    }

    [Fact]
    public void NormalizeDeduplicatesAndCanonicalizesFactionBindings()
    {
        var normalized = DiscordServerOptions.Normalize(new DiscordServerOptions
        {
            AdminChannelId = 0,
            FactionChannels =
            [
                new DiscordFactionChannelOptions { FactionTag = " sprt ", ChannelId = 11 },
                new DiscordFactionChannelOptions { FactionTag = "SPRT", ChannelId = 12 },
                new DiscordFactionChannelOptions { FactionTag = "", ChannelId = 13 },
            ],
        });

        Assert.Null(normalized.AdminChannelId);
        var faction = Assert.Single(normalized.FactionChannels);
        Assert.Equal("SPRT", faction.FactionTag);
        Assert.Equal(12UL, faction.ChannelId);
    }

    [Theory]
    [InlineData("Server One", "[SPRT]", "faction-server-one-sprt")]
    [InlineData("!", "?", "faction")]
    public void FactionChannelNameIsDiscordSafe(string server, string faction, string expected)
    {
        Assert.Equal(expected, DiscordCommandRouter.BuildFactionChannelName(server, faction));
    }
}
