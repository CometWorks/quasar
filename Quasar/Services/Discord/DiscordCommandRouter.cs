using Discord;
using Discord.WebSocket;
using Quasar.Services.Assistant;

namespace Quasar.Services.Discord;

public sealed class DiscordCommandRouter
{
    private const string WhisperCommandName = "whisper";
    private const string FactionChannelCommandName = "faction-channel";
    private const string LinkCommandName = "link";
    private const string AskCommandName = "ask";
    private const string AssistantCommandName = "assistant";
    private readonly DiscordOptionsCatalog _optionsCatalog;
    private readonly DiscordCommandDispatcher _dispatcher;
    private readonly QuasarPlayerRequests _requests;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<DiscordCommandRouter> _logger;
    private readonly object _messageSync = new();
    private Task _messageTask = Task.CompletedTask;

    public DiscordCommandRouter(
        DiscordOptionsCatalog optionsCatalog,
        DiscordCommandDispatcher dispatcher,
        QuasarPlayerRequests requests,
        IHostApplicationLifetime lifetime,
        ILogger<DiscordCommandRouter> logger)
    {
        _optionsCatalog = optionsCatalog;
        _dispatcher = dispatcher;
        _requests = requests;
        _lifetime = lifetime;
        _logger = logger;
    }

    public Task HandleAsync(SocketMessage message)
    {
        // Keep arrival order (especially start/stop) without making the gateway
        // wait for lifecycle operations or agent replies. The handler logs failures.
        lock (_messageSync)
        {
            var previous = _messageTask;
            _messageTask = Task.Run(async () =>
            {
                await previous;
                await HandleMessageAsync(message);
            });
        }
        return Task.CompletedTask;
    }

    private async Task HandleMessageAsync(SocketMessage message)
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
                if (verb is not ("chat" or "status" or "help") &&
                    (message.Author is not SocketGuildUser administrator || !administrator.GuildPermissions.Administrator))
                {
                    await message.Channel.SendMessageAsync("Discord Administrator permission required.", allowedMentions: AllowedMentions.None);
                    return;
                }
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
            .AddOption("server", ApplicationCommandOptionType.String, "Server name, or cluster:<name>", isRequired: true)
            .AddOption("user", ApplicationCommandOptionType.String, "Online player name or Steam ID", isRequired: true)
            .AddOption("message", ApplicationCommandOptionType.String, "Whisper text", isRequired: true);

        var factionChannel = new SlashCommandBuilder()
            .WithName(FactionChannelCommandName)
            .WithDescription("Create an admin-only Discord channel for a game faction.")
            .WithContextTypes(InteractionContextType.Guild)
            .WithDefaultMemberPermissions(GuildPermission.Administrator)
            .AddOption("server", ApplicationCommandOptionType.String, "Server name, or cluster:<name>", isRequired: true)
            .AddOption("faction", ApplicationCommandOptionType.String, "Space Engineers faction tag", isRequired: true);

        var link = new SlashCommandBuilder()
            .WithName(LinkCommandName)
            .WithDescription("Link your Discord account to your Space Engineers player. Type !link in game for a code.")
            .WithContextTypes(InteractionContextType.Guild)
            .AddOption("code", ApplicationCommandOptionType.String, "The code !link whispered to you in game", isRequired: true);

        List<ApplicationCommandProperties> commands = [whisper.Build(), factionChannel.Build(), link.Build()];
        // The assistant commands exist only while a UI plugin answers them.
        if (_requests.HasPlayerHandlers)
            commands.Add(new SlashCommandBuilder()
                .WithName(AskCommandName)
                .WithDescription("Ask the server's assistant.")
                .WithContextTypes(InteractionContextType.Guild)
                .AddOption("question", ApplicationCommandOptionType.String, "Your question", isRequired: true)
                .Build());
        if (_requests.HasAdminHandlers)
            commands.Add(new SlashCommandBuilder()
                .WithName(AssistantCommandName)
                .WithDescription("Ask the admin assistant (mapped Quasar admins only).")
                .WithContextTypes(InteractionContextType.Guild)
                .AddOption("question", ApplicationCommandOptionType.String, "Your question", isRequired: true)
                .Build());

        await guild.BulkOverwriteApplicationCommandAsync(commands.ToArray());
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
                case LinkCommandName:
                    await HandleLinkAsync(command);
                    break;
                case AskCommandName:
                    await HandleAssistantAsync(command, ephemeral: true, (user, channel, text, reply, token) =>
                        _requests.AskFromDiscordAsync(user, channel, text, reply, token));
                    break;
                case AssistantCommandName:
                    await HandleAssistantAsync(command, ephemeral: false, (user, channel, text, reply, token) =>
                        _requests.AskAdminFromDiscordAsync(user, channel, text, reply, token));
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

    private async Task HandleLinkAsync(SocketSlashCommand command)
    {
        await command.DeferAsync(ephemeral: true);
        var link = await _requests.LinkDiscordAsync(command.User.Id.ToString(), GetOption(command, "code"), _lifetime.ApplicationStopping);
        await command.ModifyOriginalResponseAsync(properties => properties.Content = link is null
            ? "That code is unknown or expired. Type !link in game for a new one."
            : "Linked. The assistant now knows your Space Engineers player.");
    }

    // Players' answers stay private (ephemeral); /assistant answers in the admin channel. The answer can take
    // minutes, so it runs off the gateway's event handler.
    private async Task HandleAssistantAsync(SocketSlashCommand command, bool ephemeral,
        Func<string, string, string, Func<string, CancellationToken, Task>, CancellationToken, Task> ask)
    {
        await command.DeferAsync(ephemeral: ephemeral);
        var question = GetOption(command, "question");
        var responded = 0;
        async Task ReplyAsync(string text, CancellationToken token)
        {
            foreach (var part in QuasarTextSplitter.Split(text, QuasarTextSplitter.DiscordLength, QuasarTextSplitter.MaxDiscordMessages))
            {
                if (Interlocked.Exchange(ref responded, 1) == 0)
                    await command.ModifyOriginalResponseAsync(properties => properties.Content = part);
                else
                    await command.FollowupAsync(part, ephemeral: ephemeral, allowedMentions: AllowedMentions.None);
            }
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await ask(command.User.Id.ToString(), command.ChannelId?.ToString() ?? string.Empty, question, ReplyAsync,
                    _lifetime.ApplicationStopping);
                if (Volatile.Read(ref responded) == 0)
                    await command.DeleteOriginalResponseAsync();
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Discord /{CommandName} failed", command.Data.Name);
                try { await ReplyAsync("The assistant could not answer: " + exception.Message, CancellationToken.None); }
                catch (Exception) { }
            }
        });
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
                   string.Equals(server.TargetKey, uniqueName.Trim(), StringComparison.OrdinalIgnoreCase))
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
