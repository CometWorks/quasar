using System;
using Magnetar.Protocol.Model;
using PluginSdk.Commands;
using VRage.Game.ModAPI;

namespace Quasar.Agent
{
    /// <summary>
    /// Hands players' <c>!ask</c> and <c>!link</c> lines to Quasar as player requests.
    /// The answer comes back later as whispers, so the commands only acknowledge.
    /// </summary>
    internal static class PlayerCommands
    {
        /// <summary>Sends a request to Quasar; false when Quasar is not connected. Set by <see cref="AdminPlugin"/>.</summary>
        internal static Func<PlayerRequest, bool> Send { get; set; }

        /// <summary>True once the commands are registered with the host.</summary>
        internal static bool Registered { get; set; }

        internal static CommandReply Forward(CommandContext context, string kind, string text)
        {
            var caller = context.Caller;
            if (caller.IsConsole || caller.SteamId == 0)
                return CommandReply.Error("Only players can use this command.");

            var request = new PlayerRequest
            {
                RequestId = Guid.NewGuid().ToString("N"),
                Kind = kind,
                SteamId = (long)caller.SteamId,
                IdentityId = caller.IdentityId,
                PlayerName = caller.Name ?? string.Empty,
                Text = text,
            };

            var send = Send;
            if (send == null || !send(request))
                return CommandReply.Error("Quasar is not reachable right now. Try again later.");

            return CommandReply.Ok(
                kind == PlayerRequest.LinkKind
                    ? "Creating your link code..."
                    : "Asking the assistant..."
            );
        }
    }

    [CommandRoot("ask", "Quasar", "Ask the server's assistant")]
    public sealed class AskCommand : CommandModule
    {
        [Command(
            "",
            "Ask the server's assistant",
            "Usage: !ask <question>. The answer comes as a private message."
        )]
        [Permission(MyPromoteLevel.None)]
        public CommandReply Ask(params string[] words)
        {
            var text = string.Join(" ", words ?? Array.Empty<string>()).Trim();
            if (text.Length == 0)
                return CommandReply.Error("Usage: !ask <question>");

            return PlayerCommands.Forward(Context, PlayerRequest.AskKind, text);
        }
    }

    [CommandRoot("link", "Quasar", "Link your Discord account")]
    public sealed class LinkCommand : CommandModule
    {
        [Command(
            "",
            "Get a code to link your Discord account",
            "Usage: !link. Then type /link CODE in the server's Discord."
        )]
        [Permission(MyPromoteLevel.None)]
        public CommandReply Link() =>
            PlayerCommands.Forward(Context, PlayerRequest.LinkKind, string.Empty);
    }
}
