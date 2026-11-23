using Discord;

namespace Quasar.Plugin.Abstractions.Discord;

/// <summary>A slash, user menu, or message menu command for Quasar's configured Discord guild.
/// Register implementations as singletons in the UI plugin's <see cref="IQuasarPlugin.ConfigureServices"/>.</summary>
public interface IQuasarDiscordCommand
{
    /// <summary>Builds the command with Discord.Net. The definition is reused when the bot reconnects.</summary>
    ApplicationCommandProperties BuildCommand();

    /// <summary>Checks feature permissions and acknowledges the interaction. Calls can run concurrently.</summary>
    Task HandleAsync(IApplicationCommandInteraction interaction, CancellationToken cancellationToken);

    /// <summary>Returns choices for a slash command option configured to use autocomplete.</summary>
    Task HandleAutocompleteAsync(IAutocompleteInteraction interaction, CancellationToken cancellationToken) =>
        interaction.RespondAsync([]);
}
