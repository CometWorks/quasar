namespace Quasar.Plugin.Abstractions.Discord;

/// <summary>
/// Posts to a channel of the Discord server Quasar's bot is set up for. Implemented by core; resolve it from DI.
/// </summary>
public interface IQuasarDiscordChannels
{
    /// <summary>
    /// Posts <paramref name="text"/> to the channel, split into messages of at most 2000 characters (5 messages at
    /// most, the last one then says the rest was cut). Mentions are never resolved, so a written <c>@everyone</c>,
    /// role or user mention pings nobody. False when the bot isn't connected, the text is empty, or the channel
    /// isn't a text channel of the configured guild.
    /// </summary>
    Task<bool> PostAsync(string channelId, string text, CancellationToken token);
}
