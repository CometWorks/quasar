using System;
using System.Collections.Generic;
using System.Reflection;
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
                if (ExecuteMethod == null)
                {
                    error = "This Magnetar version can't run commands from Quasar. Update Magnetar.";
                    return false;
                }

                if ((bool)ExecuteMethod.Invoke(null, new object[] { commandLine, Console, responder }))
                    return true;
                error = "Unknown command. Type !help for the list.";
                return false;
            }
            catch (TargetInvocationException exception) when (exception.InnerException is InvalidOperationException)
            {
                // A host with the method but without a command executor
                error = "This Magnetar version can't run commands from Quasar. Update Magnetar.";
                return false;
            }
            finally
            {
                responder.Returned = true;
            }
        }

        // ServerCommands.Execute(string, in CommandCaller, ICommandResponder), new after Magnetar
        // 2.4.3.4. Looked up at run time, so the agent builds against and runs on Magnetar
        // releases without it.
        private static readonly MethodInfo ExecuteMethod = typeof(ServerCommands).GetMethod(
            "Execute",
            BindingFlags.Public | BindingFlags.Static,
            null,
            new[] { typeof(string), typeof(CommandCaller).MakeByRefType(), typeof(ICommandResponder) },
            null);

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
