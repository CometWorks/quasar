using System.Security.Claims;

namespace Quasar.Plugin.Abstractions.Commands;

/// <summary>
/// A chat command a UI plugin adds. Register implementations in DI. A player command is <c>!name &lt;text&gt;</c>
/// in game on every server and <c>/name</c> on Discord; an admin command is <c>/name</c> on Discord only.
/// Core authenticates the caller, carries the text and delivers the replies; the plugin only answers.
/// </summary>
public interface IQuasarChatCommand
{
    QuasarChatCommandInfo Info { get; }

    /// <summary>Called off the chat and Discord event loops. A thrown exception is logged, never shown.</summary>
    Task HandleAsync(QuasarChatCommandRequest request, CancellationToken token);
}

public enum QuasarChatCommandScope
{
    /// <summary>Every player, in game and on Discord. Answers on Discord are private to the caller.</summary>
    Player,

    /// <summary>Discord users mapped to a Quasar subject on the Security page. Answers are posted in the channel.</summary>
    Admin,
}

/// <param name="Name">The command word: 1 to 32 lowercase letters, digits or hyphens. Core's own commands
/// (<c>link</c>, <c>whisper</c>, <c>faction-channel</c>, <c>help</c>, <c>stop</c>, <c>restart</c>, <c>quit</c>)
/// can't be taken; a command whose name is invalid or already taken is left out and logged.</param>
/// <param name="Description">Shown in <c>!help</c> and the Discord command list. At most 100 characters.</param>
/// <param name="ArgumentName">The text's name, shown as <c>!name &lt;argument&gt;</c> and as the Discord option.
/// Lowercase like <see cref="Name"/>.</param>
/// <param name="ArgumentDescription">The Discord option's description. At most 100 characters.</param>
/// <param name="Acknowledgement">What the player sees in game right after sending, before the answer comes.</param>
public sealed record QuasarChatCommandInfo(
    string Name,
    string Description,
    string ArgumentName,
    string ArgumentDescription,
    QuasarChatCommandScope Scope = QuasarChatCommandScope.Player,
    string Acknowledgement = "Sent."
);

/// <param name="Caller">For a player command, the player-agent principal core built from the authenticated game
/// session or the stored Discord link; null for a Discord user without a link. For an admin command, the
/// mapped subject's principal with its current roles: a person, never an agent.</param>
/// <param name="ConversationKey"><c>steam:&lt;id&gt;</c> for a player, which follows them across cluster nodes and
/// Discord once linked; <c>discord:&lt;id&gt;</c> for an unlinked Discord user or an admin.</param>
/// <param name="Text">The caller's text, trimmed and at most 2000 characters; never empty.</param>
/// <param name="ReplyAsync">Whispers to the player in game (split to the chat limit) or replies on Discord
/// (split to the message limit).</param>
public sealed record QuasarChatCommandRequest(
    string Command,
    ClaimsPrincipal? Caller,
    string ConversationKey,
    string Text,
    QuasarChatCommandSource Source,
    Func<string, CancellationToken, Task> ReplyAsync
);

public enum QuasarChatChannel
{
    Game,
    Discord,
}

/// <summary>Where a command came from.</summary>
/// <param name="Server">The standalone server's unique name or the cluster's unique name; null on Discord.</param>
/// <param name="Node">The cluster node that carried the command; null on a standalone server and on Discord.</param>
public sealed record QuasarChatCommandSource(
    QuasarChatChannel Channel,
    string? Server = null,
    string? Node = null,
    string? DiscordUserId = null,
    string? DiscordChannelId = null
);
