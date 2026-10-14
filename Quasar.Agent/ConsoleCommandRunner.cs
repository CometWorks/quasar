using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using PluginSdk.Commands;
using Sandbox.Game;
using VRage.Game;
using VRage.Game.ModAPI;

namespace Quasar.Agent
{
    /// <summary>
    /// Runs a chat command typed into Quasar's Chat page through the server's
    /// registered commands, as the console. Replies go back to Quasar as Console
    /// chat lines; only broadcast replies (announcements) reach the players.
    /// </summary>
    internal static class ConsoleCommandRunner
    {
        private static readonly CommandCaller Console =
            new CommandCaller(0UL, 0L, "Quasar", MyPromoteLevel.Owner, isConsole: true);

        /// <summary>Must run on the game thread. Returns the replies sent
        /// before the command returned; later ones arrive through
        /// <paramref name="record"/> only.</summary>
        public static bool Run(string commandLine, Action<string, string> record, out List<string> replies, out string error)
        {
            var responder = new Responder(record);
            error = null;
            replies = responder.Sent;
            try
            {
                if (Execute(commandLine, responder))
                    return true;
                error = "Unknown command. Type !help for the list.";
                return false;
            }
            catch (Exception exception) when (exception is MissingMethodException || exception is InvalidOperationException)
            {
                // Magnetar before ServerCommands.Execute (2.4.3.4 and older)
                error = "This Magnetar version can't run commands from Quasar. Update Magnetar.";
                return false;
            }
            finally
            {
                responder.Returned = true;
            }
        }

        // Kept apart so a host without ServerCommands.Execute fails here, inside Run's try
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool Execute(string commandLine, ICommandResponder responder)
            => ServerCommands.Execute(commandLine, Console, responder);

        private sealed class Responder : ICommandResponder
        {
            private readonly Action<string, string> record;
            public readonly List<string> Sent = new List<string>();
            public bool Returned;

            public Responder(Action<string, string> record)
            {
                this.record = record;
            }

            public void Send(in CommandReply reply, in CommandCaller caller)
            {
                if (!reply.HasContent)
                    return;

                string author = string.IsNullOrEmpty(reply.Author) ? "Server" : reply.Author;
                if (reply.Broadcast)
                {
                    string font = string.IsNullOrEmpty(reply.Font) ? MyFontEnum.White : reply.Font;
                    if (reply.Color.HasValue)
                        MyVisualScriptLogicProvider.SendChatMessageColored(reply.Text, reply.Color.Value, author, 0L, font);
                    else
                        MyVisualScriptLogicProvider.SendChatMessage(reply.Text, author, 0L, font);
                }

                if (!Returned)
                    Sent.Add(reply.Text);
                record(author, reply.Text);
            }
        }
    }
}
