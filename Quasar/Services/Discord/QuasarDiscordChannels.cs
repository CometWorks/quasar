using Discord;
using Discord.Net;
using Quasar.Plugin.Abstractions.Discord;
using Quasar.Services.Players;

namespace Quasar.Services.Discord;

/// <summary>Lets plugins post to the configured guild's channels through the bot.</summary>
public sealed class QuasarDiscordChannels(
    IServiceProvider services,
    DiscordOptionsCatalog options,
    DiscordRateLimiter limiter,
    ILogger<QuasarDiscordChannels> logger
) : IQuasarDiscordChannels
{
    public async Task<bool> PostAsync(string channelId, string text, CancellationToken token)
    {
        // Resolved per call: the bot depends on the command router, which depends on the plugins using this.
        var client = services.GetService<DiscordBotService>()?.CurrentClient;
        if (
            client is not { ConnectionState: ConnectionState.Connected }
            || !ulong.TryParse(channelId, out var id)
        )
            return false;
        try
        {
            return await PostAsync(
                client.GetChannel(id),
                options.GetOptions().GuildId,
                text,
                limiter,
                token
            );
        }
        catch (HttpException error)
        {
            logger.LogWarning(
                "Posting to Discord channel {ChannelId} failed: {Reason}",
                id,
                error.Reason ?? error.HttpCode.ToString()
            );
            return false;
        }
    }

    internal static async Task<bool> PostAsync(
        IChannel? channel,
        ulong guildId,
        string text,
        DiscordRateLimiter limiter,
        CancellationToken token
    )
    {
        if (
            guildId == 0
            || channel is not (IGuildChannel guildChannel and IMessageChannel messages)
            || guildChannel.GuildId != guildId
        )
            return false;
        var parts = QuasarTextSplitter.Split(
            text,
            QuasarTextSplitter.DiscordLength,
            QuasarTextSplitter.MaxDiscordMessages
        );
        if (parts.Count == 0)
            return false;
        foreach (var part in parts)
            await limiter.RunAsync(
                channel.Id,
                () => messages.SendMessageAsync(text: part, allowedMentions: AllowedMentions.None),
                token
            );
        return true;
    }
}
