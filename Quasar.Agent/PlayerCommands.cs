using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Text.RegularExpressions;
using Magnetar.Protocol.Model;
using PluginSdk.Commands;
using VRage.Game.ModAPI;

namespace Quasar.Agent
{
    /// <summary>
    /// Hands players' authorization and UI plugins' chat commands to Quasar as player requests.
    /// The answer comes back later as whispers, so the commands only acknowledge.
    /// </summary>
    internal static class PlayerCommands
    {
        private static readonly Regex NamePattern = new Regex(@"\A[a-z0-9-]{1,32}\z");

        // The commands Quasar sent, waiting for the game thread, and the ones registered by name.
        private static readonly ConcurrentQueue<ChatCommandInfo> Pending =
            new ConcurrentQueue<ChatCommandInfo>();
        private static readonly ConcurrentDictionary<string, ChatCommandInfo> Known =
            new ConcurrentDictionary<string, ChatCommandInfo>(StringComparer.OrdinalIgnoreCase);
        private static ModuleBuilder _module;

        /// <summary>Sends a request to Quasar; false when Quasar is not connected. Set by <see cref="AdminPlugin"/>.</summary>
        internal static Func<PlayerRequest, bool> Send { get; set; }

        /// <summary>True once the commands are registered with the host.</summary>
        internal static bool Registered { get; set; }

        /// <summary>Takes the plugin commands Quasar sent on connecting. Any thread.</summary>
        internal static void Offer(IEnumerable<ChatCommandInfo> commands)
        {
            foreach (var command in commands ?? Array.Empty<ChatCommandInfo>())
                if (command != null && NamePattern.IsMatch(command.Name ?? string.Empty))
                    Pending.Enqueue(command);
        }

        /// <summary>Registers the offered commands that are new, on the game thread. A command that is already
        /// registered only takes the new texts: the host can't drop a command, so a removed one stays and
        /// Quasar answers that it's not available.</summary>
        internal static void RegisterPending()
        {
            if (Pending.IsEmpty)
                return;
            var types = new List<Type>();
            while (Pending.TryDequeue(out var command))
            {
                if (!Known.ContainsKey(command.Name))
                    types.Add(Build(command));
                Known[command.Name] = command;
            }
            if (types.Count > 0)
                ServerCommands.Register(typeof(PlayerCommands).Assembly, types.ToArray());
        }

        internal static bool TryGet(string name, out ChatCommandInfo command) =>
            Known.TryGetValue(name ?? string.Empty, out command);

        internal static CommandReply Forward(
            CommandContext context,
            string kind,
            string text,
            string acknowledgement
        )
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
                string.IsNullOrWhiteSpace(acknowledgement) ? "Sent." : acknowledgement
            );
        }

        // The host registers commands by type and reads their attributes, so each plugin command is a subclass of
        // PluginChatCommand built here with the command's own texts.
        private static Type Build(ChatCommandInfo command)
        {
            if (_module == null)
                _module = AssemblyBuilder
                    .DefineDynamicAssembly(
                        new AssemblyName("Quasar.Agent.PluginCommands"),
                        AssemblyBuilderAccess.Run
                    )
                    .DefineDynamicModule("Quasar.Agent.PluginCommands");

            var argument = NamePattern.IsMatch(command.Argument ?? string.Empty)
                ? command.Argument
                : "text";
            var type = _module.DefineType(
                "Quasar.Agent.PluginCommands.Command_" + command.Name.Replace('-', '_'),
                TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.Class,
                typeof(PluginChatCommand)
            );
            type.SetCustomAttribute(
                Attribute<CommandRootAttribute>(
                    new[] { typeof(string), typeof(string), typeof(string) },
                    command.Name,
                    "Quasar",
                    command.Description ?? string.Empty
                )
            );
            type.DefineDefaultConstructor(MethodAttributes.Public);

            var use = type.DefineMethod(
                "Use",
                MethodAttributes.Public | MethodAttributes.HideBySig,
                typeof(CommandReply),
                new[] { typeof(string[]) }
            );
            use.SetCustomAttribute(
                Attribute<CommandAttribute>(
                    new[] { typeof(string), typeof(string), typeof(string) },
                    string.Empty,
                    command.Description ?? string.Empty,
                    $"Usage: !{command.Name} <{argument}>. The answer comes as a private message."
                )
            );
            use.SetCustomAttribute(
                Attribute<PermissionAttribute>(
                    new[] { typeof(MyPromoteLevel) },
                    MyPromoteLevel.None
                )
            );
            use.DefineParameter(1, ParameterAttributes.None, argument)
                .SetCustomAttribute(Attribute<ParamArrayAttribute>(Type.EmptyTypes));
            var il = use.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(
                OpCodes.Call,
                typeof(PluginChatCommand).GetMethod(
                    nameof(PluginChatCommand.Forward),
                    BindingFlags.Instance | BindingFlags.NonPublic
                )
            );
            il.Emit(OpCodes.Ret);
            return type.CreateTypeInfo();
        }

        private static CustomAttributeBuilder Attribute<T>(
            Type[] parameters,
            params object[] values
        ) => new CustomAttributeBuilder(typeof(T).GetConstructor(parameters), values);
    }

    /// <summary>The base of the command types <see cref="PlayerCommands"/> builds for UI plugins' commands.</summary>
    public abstract class PluginChatCommand : CommandModule
    {
        protected internal CommandReply Forward(string[] words)
        {
            if (!PlayerCommands.TryGet(Context.Prefix, out var command))
                return CommandReply.Error("This command is not available.");
            var text = string.Join(" ", words ?? Array.Empty<string>()).Trim();
            if (text.Length == 0)
                return CommandReply.Error($"Usage: !{command.Name} <{command.Argument}>");
            return PlayerCommands.Forward(Context, command.Name, text, command.Acknowledgement);
        }
    }

    [CommandRoot("authorize", "Quasar", "Authenticate Steam and Discord")]
    public sealed class AuthorizeCommand : CommandModule
    {
        [Command(
            "",
            "Open browser authentication",
            "Usage: !authorize. Authenticate Steam and Discord on the website and authorize this server."
        )]
        [Permission(MyPromoteLevel.None)]
        public CommandReply Authorize() =>
            PlayerCommands.Forward(
                Context,
                PlayerRequest.AuthorizeKind,
                string.Empty,
                "Opening player authentication..."
            );
    }
}
