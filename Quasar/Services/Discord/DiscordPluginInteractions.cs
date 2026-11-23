using Discord;
using Quasar.Plugin.Abstractions.Commands;
using Quasar.Plugin.Abstractions.Discord;
using Quasar.Services.Players;

namespace Quasar.Services.Discord;

internal sealed class DiscordPluginInteractions
{
    private readonly Dictionary<(ApplicationCommandType Type, string Name), IQuasarDiscordCommand> _commands = [];
    private readonly Dictionary<string, IQuasarDiscordInteractionHandler> _handlers = new(StringComparer.Ordinal);
    private readonly List<ApplicationCommandProperties> _definitions = [];
    private readonly ILogger _logger;

    public DiscordPluginInteractions(
        IEnumerable<QuasarChatCommandInfo> chatCommands,
        IEnumerable<IQuasarDiscordCommand> commands,
        IEnumerable<IQuasarDiscordInteractionHandler> handlers,
        ILogger logger)
    {
        _logger = logger;
        var reservedSlashCommands = new HashSet<string>(QuasarChatCommands.Reserved, StringComparer.OrdinalIgnoreCase);
        reservedSlashCommands.UnionWith(chatCommands.Select(command => command.Name));

        foreach (var command in commands)
        {
            try
            {
                var definition = command.BuildCommand();
                var name = definition.Name.GetValueOrDefault();
                var type = definition switch
                {
                    SlashCommandProperties => ApplicationCommandType.Slash,
                    UserCommandProperties => ApplicationCommandType.User,
                    MessageCommandProperties => ApplicationCommandType.Message,
                    _ => throw new InvalidOperationException("Unsupported Discord command type."),
                };
                if (string.IsNullOrWhiteSpace(name) || name.Length > 32)
                    throw new InvalidOperationException("Discord command names must have 1 to 32 characters.");
                if (definition is SlashCommandProperties slash)
                {
                    var description = slash.Description.GetValueOrDefault();
                    if (string.IsNullOrWhiteSpace(description) || description.Length > 100)
                        throw new InvalidOperationException("Discord slash command descriptions must have 1 to 100 characters.");
                }

                if ((type == ApplicationCommandType.Slash && reservedSlashCommands.Contains(name)) ||
                    !_commands.TryAdd((type, name), command))
                {
                    logger.LogWarning("Skipped Discord {Type} command {Name} from {Plugin}: the name is already in use.",
                        type, name, command.GetType().FullName);
                    continue;
                }

                _definitions.Add(definition);
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Failed building a Discord command from {Plugin}.", command.GetType().FullName);
            }
        }

        foreach (var handler in handlers)
        {
            try
            {
                var prefix = handler.CustomIdPrefix;
                if (string.IsNullOrEmpty(prefix) || prefix.Length > 98 || prefix.Contains(':') || prefix.Any(char.IsWhiteSpace))
                    throw new InvalidOperationException("A Discord custom ID prefix must have 1 to 98 characters without colons or whitespace.");

                if (!_handlers.TryAdd(prefix, handler))
                    logger.LogWarning("Skipped Discord interaction handler from {Plugin}: prefix {Prefix} is already in use.",
                        handler.GetType().FullName, prefix);
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Failed registering a Discord interaction handler from {Plugin}.", handler.GetType().FullName);
            }
        }
    }

    public IReadOnlyList<ApplicationCommandProperties> Definitions => _definitions;

    public Task DispatchAsync(IDiscordInteraction interaction, CancellationToken cancellationToken)
    {
        var invoke = FindHandler(interaction);
        if (invoke is null)
            return Task.CompletedTask;

        // Discord waits for gateway callbacks before processing the next event.
        return Task.Run(async () =>
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                await invoke(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Plugin handling Discord interaction {InteractionId} failed.", interaction.Id);
                try
                {
                    if (interaction is IAutocompleteInteraction autocomplete)
                    {
                        if (!interaction.HasResponded)
                            await autocomplete.RespondAsync([]);
                    }
                    else if (interaction.HasResponded)
                        await interaction.FollowupAsync("The interaction failed.", ephemeral: true, allowedMentions: AllowedMentions.None);
                    else
                        await interaction.RespondAsync("The interaction failed.", ephemeral: true, allowedMentions: AllowedMentions.None);
                }
                catch (Exception responseError)
                {
                    _logger.LogDebug(responseError, "Could not respond to Discord interaction {InteractionId}.", interaction.Id);
                }
            }
        });
    }

    private Func<CancellationToken, Task>? FindHandler(IDiscordInteraction interaction)
    {
        switch (interaction)
        {
            case IAutocompleteInteraction autocomplete:
                return _commands.TryGetValue((ApplicationCommandType.Slash, autocomplete.Data.CommandName), out var command)
                    ? token => command.HandleAutocompleteAsync(autocomplete, token)
                    : null;
            case IApplicationCommandInteraction application:
                var type = application switch
                {
                    ISlashCommandInteraction => ApplicationCommandType.Slash,
                    IUserCommandInteraction => ApplicationCommandType.User,
                    IMessageCommandInteraction => ApplicationCommandType.Message,
                    _ => (ApplicationCommandType)0,
                };
                return _commands.TryGetValue((type, application.Data.Name), out var applicationCommand)
                    ? token => applicationCommand.HandleAsync(application, token)
                    : null;
        }

        var customId = interaction switch
        {
            IComponentInteraction component => component.Data.CustomId,
            IModalInteraction modal => modal.Data.CustomId,
            _ => null,
        };
        if (customId is null)
            return null;

        var separator = customId.IndexOf(':');
        return separator > 0 && separator < customId.Length - 1 && _handlers.TryGetValue(customId[..separator], out var handler)
            ? token => handler.HandleAsync(interaction, token)
            : null;
    }
}
