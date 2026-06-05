using Discord.WebSocket;

namespace Quasar.Services.Discord;

public sealed class DiscordCommandRouter
{
    private readonly DiscordOptionsCatalog _optionsCatalog;
    private readonly DiscordCommandDispatcher _dispatcher;
    private readonly ILogger<DiscordCommandRouter> _logger;

    public DiscordCommandRouter(
        DiscordOptionsCatalog optionsCatalog,
        DiscordCommandDispatcher dispatcher,
        ILogger<DiscordCommandRouter> logger)
    {
        _optionsCatalog = optionsCatalog;
        _dispatcher = dispatcher;
        _logger = logger;
    }

    public async Task HandleAsync(SocketMessage message)
    {
        try
        {
            if (message.Author.IsBot || string.IsNullOrWhiteSpace(message.Content))
                return;

            if (message.Channel is not SocketGuildChannel guildChannel)
                return;

            var options = _optionsCatalog.GetOptions();
            if (!options.Enabled || options.GuildId == 0 || guildChannel.Guild.Id != options.GuildId)
                return;

            foreach (var serverOptions in options.Servers)
            {
                if (serverOptions.CommandChannelId != guildChannel.Id || string.IsNullOrWhiteSpace(serverOptions.CommandPrefix))
                    continue;

                if (!message.Content.StartsWith(serverOptions.CommandPrefix, StringComparison.OrdinalIgnoreCase))
                    continue;

                var remainder = message.Content[serverOptions.CommandPrefix.Length..].Trim();
                if (string.IsNullOrWhiteSpace(remainder))
                {
                    await _dispatcher.DispatchAsync(serverOptions, "help", string.Empty, message);
                    return;
                }

                var tokens = remainder.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var verb = tokens[0].ToLowerInvariant();
                var args = tokens.Length > 1 ? tokens[1] : string.Empty;
                await _dispatcher.DispatchAsync(serverOptions, verb, args, message);
                return;
            }

            foreach (var serverOptions in options.Servers)
            {
                if (!serverOptions.EnableChatRelay ||
                    !serverOptions.RelayNonCommandMessages ||
                    serverOptions.ChatRelayChannelId != guildChannel.Id)
                {
                    continue;
                }

                await _dispatcher.RelayChatAsync(serverOptions, message.Content.Trim(), message);
                return;
            }
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Discord command routing failed for message {MessageId}", message.Id);
        }
    }
}
