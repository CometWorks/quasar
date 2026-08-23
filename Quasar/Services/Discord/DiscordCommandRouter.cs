using Discord;
using Discord.WebSocket;

namespace Quasar.Services.Discord;

public sealed class DiscordCommandRouter
{
    private const string WhisperCommandName = "whisper";
    private const string FactionChannelCommandName = "faction-channel";
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
            if (message.Source != MessageSource.User ||
                message.Author.IsBot ||
                string.IsNullOrWhiteSpace(message.Content))
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
                var factionChannel = ResolveFactionBinding(serverOptions, guildChannel.Id);
                if (!serverOptions.EnableChatRelay || factionChannel is null)
                    continue;

                if (message.Author is not SocketGuildUser user || !user.GuildPermissions.Administrator)
                {
                    _logger.LogWarning(
                        "Ignored Discord faction chat from non-administrator {UserId} in channel {ChannelId}",
                        message.Author.Id,
                        guildChannel.Id);
                    return;
                }

                await _dispatcher.RelayFactionChatAsync(
                    serverOptions,
                    factionChannel.FactionTag,
                    message.Content,
                    message);
                return;
            }

            foreach (var serverOptions in options.Servers)
            {
                if (!serverOptions.EnableChatRelay ||
                    serverOptions.ChatRelayChannelId != guildChannel.Id)
                {
                    continue;
                }

                await _dispatcher.RelayChatAsync(serverOptions, message.Content, message);
                return;
            }

        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Discord command routing failed for message {MessageId}", message.Id);
        }
    }

    public async Task RegisterSlashCommandsAsync(DiscordSocketClient client)
    {
        var options = _optionsCatalog.GetOptions();
        var guild = client.GetGuild(options.GuildId)
            ?? throw new InvalidOperationException($"Discord guild {options.GuildId} is unavailable.");

        var whisper = new SlashCommandBuilder()
            .WithName(WhisperCommandName)
            .WithDescription("Whisper to an online Space Engineers player.")
            .WithContextTypes(InteractionContextType.Guild)
            .AddOption("server", ApplicationCommandOptionType.String, "Quasar server unique name", isRequired: true)
            .AddOption("user", ApplicationCommandOptionType.String, "Online player name or Steam ID", isRequired: true)
            .AddOption("message", ApplicationCommandOptionType.String, "Whisper text", isRequired: true);

        var factionChannel = new SlashCommandBuilder()
            .WithName(FactionChannelCommandName)
            .WithDescription("Create an admin-only Discord channel for a game faction.")
            .WithContextTypes(InteractionContextType.Guild)
            .WithDefaultMemberPermissions(GuildPermission.Administrator)
            .AddOption("server", ApplicationCommandOptionType.String, "Quasar server unique name", isRequired: true)
            .AddOption("faction", ApplicationCommandOptionType.String, "Space Engineers faction tag", isRequired: true);

        await guild.BulkOverwriteApplicationCommandAsync([whisper.Build(), factionChannel.Build()]);
    }

    public async Task HandleSlashCommandAsync(SocketSlashCommand command)
    {
        try
        {
            var options = _optionsCatalog.GetOptions();
            if (!options.Enabled || command.GuildId != options.GuildId)
                return;

            switch (command.Data.Name)
            {
                case WhisperCommandName:
                    await HandleWhisperAsync(command, options);
                    break;
                case FactionChannelCommandName:
                    await HandleFactionChannelAsync(command, options);
                    break;
            }
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Discord slash command {CommandName} failed", command.Data.Name);
            if (command.HasResponded)
            {
                await command.ModifyOriginalResponseAsync(properties => properties.Content = $"Error: {exception.Message}");
            }
            else
            {
                await command.RespondAsync($"Error: {exception.Message}", ephemeral: true);
            }
        }
    }

    private async Task HandleWhisperAsync(SocketSlashCommand command, DiscordOptions options)
    {
        await command.DeferAsync(ephemeral: true);

        var server = ResolveServer(options, GetOption(command, "server"));
        if (!IsBridgeChannel(server, command.ChannelId))
            throw new InvalidOperationException("Use /whisper in a channel configured for that server's Discord bridge.");

        var recipient = GetOption(command, "user");
        var message = GetOption(command, "message");
        var author = command.User is SocketGuildUser guildUser
            ? guildUser.DisplayName
            : command.User.GlobalName ?? command.User.Username;

        await _dispatcher.SendWhisperAsync(server, recipient, message, author);
        await command.ModifyOriginalResponseAsync(properties => properties.Content = $"Whisper sent to {recipient}.");
    }

    private async Task HandleFactionChannelAsync(SocketSlashCommand command, DiscordOptions options)
    {
        if (command.User is not SocketGuildUser user || !user.GuildPermissions.Administrator)
            throw new InvalidOperationException("Discord Administrator permission required.");

        await command.DeferAsync(ephemeral: true);

        var server = ResolveServer(options, GetOption(command, "server"));
        var factionTag = GetOption(command, "faction").Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(factionTag))
            throw new InvalidOperationException("Faction tag is required.");

        var guild = user.Guild;
        var permissionOverwrites = BuildAdminOnlyPermissionOverwrites(guild);
        var existing = server.FactionChannels.FirstOrDefault(channel =>
            string.Equals(channel.FactionTag, factionTag, StringComparison.OrdinalIgnoreCase));
        if (existing is not null && guild.GetTextChannel(existing.ChannelId) is { } existingChannel)
        {
            await existingChannel.ModifyAsync(properties =>
            {
                properties.Topic = $"Admin-only Space Engineers faction chat relay for {server.UniqueName} / {factionTag}.";
                properties.PermissionOverwrites = permissionOverwrites;
            });
            await command.ModifyOriginalResponseAsync(properties =>
                properties.Content = $"Secured {MentionUtils.MentionChannel(existingChannel.Id)} for faction {factionTag}. Only Discord administrators and the bot can view it.");
            return;
        }

        var channelName = BuildFactionChannelName(server.UniqueName, factionTag);
        var categoryId = (command.Channel as SocketTextChannel)?.CategoryId;

        var channel = await guild.CreateTextChannelAsync(channelName, properties =>
        {
            properties.CategoryId = categoryId;
            properties.Topic = $"Admin-only Space Engineers faction chat relay for {server.UniqueName} / {factionTag}.";
            properties.PermissionOverwrites = permissionOverwrites;
        });

        server.FactionChannels.RemoveAll(item =>
            string.Equals(item.FactionTag, factionTag, StringComparison.OrdinalIgnoreCase));
        server.FactionChannels.Add(new DiscordFactionChannelOptions
        {
            FactionTag = factionTag,
            ChannelId = channel.Id,
        });

        await command.ModifyOriginalResponseAsync(properties =>
            properties.Content = $"Created {MentionUtils.MentionChannel(channel.Id)} for faction {factionTag}. Only Discord administrators and the bot can view it.");
        await _optionsCatalog.SaveAsync(options);
    }

    private static Overwrite[] BuildAdminOnlyPermissionOverwrites(SocketGuild guild)
    {
        var everyonePermissions = OverwritePermissions.InheritAll.Modify(viewChannel: PermValue.Deny);
        var botPermissions = OverwritePermissions.InheritAll.Modify(
            viewChannel: PermValue.Allow,
            sendMessages: PermValue.Allow,
            readMessageHistory: PermValue.Allow,
            embedLinks: PermValue.Allow,
            attachFiles: PermValue.Allow);

        return
        [
            new Overwrite(guild.EveryoneRole.Id, PermissionTarget.Role, everyonePermissions),
            new Overwrite(guild.CurrentUser.Id, PermissionTarget.User, botPermissions),
        ];
    }

    private static DiscordServerOptions ResolveServer(DiscordOptions options, string uniqueName)
    {
        return options.Servers.FirstOrDefault(server =>
                   string.Equals(server.UniqueName, uniqueName.Trim(), StringComparison.OrdinalIgnoreCase))
               ?? throw new InvalidOperationException($"Discord bridge server '{uniqueName}' was not found.");
    }

    private static bool IsBridgeChannel(DiscordServerOptions server, ulong? channelId)
    {
        return channelId.HasValue &&
               (server.CommandChannelId == channelId ||
                server.ChatRelayChannelId == channelId ||
                server.AdminChannelId == channelId ||
                server.FactionChannels.Any(channel => channel.ChannelId == channelId));
    }

    internal static DiscordFactionChannelOptions? ResolveFactionBinding(
        DiscordServerOptions server,
        ulong channelId)
    {
        return server.FactionChannels.FirstOrDefault(channel => channel.ChannelId == channelId);
    }

    private static string GetOption(SocketSlashCommand command, string name)
    {
        return command.Data.Options.FirstOrDefault(option =>
                   string.Equals(option.Name, name, StringComparison.OrdinalIgnoreCase))
               ?.Value?.ToString()?.Trim() ?? string.Empty;
    }

    internal static string BuildFactionChannelName(string uniqueName, string factionTag)
    {
        var value = $"faction-{uniqueName}-{factionTag}".ToLowerInvariant();
        var normalized = new string(value.Select(character =>
            character is >= 'a' and <= 'z' or >= '0' and <= '9' ? character : '-').ToArray());
        while (normalized.Contains("--", StringComparison.Ordinal))
            normalized = normalized.Replace("--", "-", StringComparison.Ordinal);

        normalized = normalized.Trim('-');
        if (normalized.Length > 100)
            normalized = normalized[..100].TrimEnd('-');

        return normalized.Length >= 2 ? normalized : "faction-chat";
    }
}
