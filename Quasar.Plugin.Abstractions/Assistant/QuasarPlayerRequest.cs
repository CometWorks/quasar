using System.Security.Claims;

namespace Quasar.Plugin.Abstractions.Assistant;

/// <summary>
/// Answers players' questions: <c>!ask</c> in game and <c>/ask</c> on Discord. Register an
/// implementation in DI; core calls every registered handler. With none, core tells the player
/// that the assistant is not enabled.
/// </summary>
public interface IQuasarPlayerRequestHandler
{
    Task HandleAsync(QuasarPlayerRequest request, CancellationToken token);
}

/// <param name="Player">The player-agent principal, built by core from the authenticated game session or the
/// stored Discord link. Null for a Discord user without a link: answer general questions only, with no tools.</param>
/// <param name="ConversationKey"><c>steam:&lt;id&gt;</c>, or <c>discord:&lt;id&gt;</c> without a link. A game
/// conversation follows the player across cluster nodes.</param>
/// <param name="ReplyAsync">Whispers to the player in game (split to the chat limit) or replies on Discord
/// (split to the message limit).</param>
public sealed record QuasarPlayerRequest(
    ClaimsPrincipal? Player,
    string ConversationKey,
    string Text,
    QuasarPlayerRequestSource Source,
    Func<string, CancellationToken, Task> ReplyAsync
);

public enum QuasarRequestChannel
{
    Game,
    Discord,
}

/// <summary>Where a request came from.</summary>
/// <param name="Server">The standalone server's unique name or the cluster's unique name; null on Discord.</param>
/// <param name="Node">The cluster node that carried the request; null on a standalone server and on Discord.</param>
public sealed record QuasarPlayerRequestSource(
    QuasarRequestChannel Channel,
    string? Server = null,
    string? Node = null,
    string? DiscordUserId = null,
    string? DiscordChannelId = null
);

/// <summary>
/// Answers <c>/assistant</c> on Discord, for Discord users mapped to a Quasar subject. Whether the channel
/// is the admin channel is the handler's check. Register an implementation in DI.
/// </summary>
public interface IQuasarAdminRequestHandler
{
    Task HandleAsync(QuasarAdminRequest request, CancellationToken token);
}

/// <param name="Asker">The mapped subject's principal with its current roles: a person, never an agent.</param>
public sealed record QuasarAdminRequest(
    ClaimsPrincipal Asker,
    string ChannelId,
    string Text,
    Func<string, CancellationToken, Task> ReplyAsync
);
