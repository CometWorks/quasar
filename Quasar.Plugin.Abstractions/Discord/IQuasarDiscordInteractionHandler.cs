using Discord;

namespace Quasar.Plugin.Abstractions.Discord;

/// <summary>Handles buttons, select menus, and modal submissions in Quasar's configured Discord guild.
/// Register implementations as singletons in the UI plugin's <see cref="IQuasarPlugin.ConfigureServices"/>.</summary>
public interface IQuasarDiscordInteractionHandler
{
    /// <summary>A unique, case-sensitive prefix without colons or whitespace. Use custom IDs of the form
    /// <c>prefix:action</c>, for example <c>my-plugin:refresh</c>.</summary>
    string CustomIdPrefix { get; }

    /// <summary>Checks feature permissions and acknowledges an <see cref="IComponentInteraction"/> or
    /// <see cref="IModalInteraction"/>. Calls can run concurrently.</summary>
    Task HandleAsync(IDiscordInteraction interaction, CancellationToken cancellationToken);
}
