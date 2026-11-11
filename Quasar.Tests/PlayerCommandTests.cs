using System.Reflection;
using System.Text;
using System.Text.Json;
using Magnetar.Protocol.Model;
using Magnetar.Protocol.Transport;
using PluginSdk.Commands;
using Quasar.Agent;
using VRage.Game.ModAPI;
using Xunit;

namespace Quasar.Tests;

[Collection("ServerCommands")]
public sealed class PlayerCommandTests
{
    private const ulong Pilot = 76561198000000001;

    private sealed class Registrar(CommandRegistry? registry = null) : ICommandRegistrar
    {
        internal readonly List<Type> Types = [];

        public void Register(Assembly assembly) =>
            throw new InvalidOperationException("Commands must be named.");

        public void Register(Assembly assembly, params Type[] moduleTypes)
        {
            Types.AddRange(moduleTypes);
            foreach (var type in moduleTypes)
                registry?.RegisterModule(type, assembly.GetName().Name!);
        }
    }

    private sealed class Responder : ICommandResponder
    {
        internal readonly List<CommandReply> Replies = [];

        public void Send(in CommandReply reply, in CommandCaller caller) => Replies.Add(reply);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AuthorizationIsRegisteredForEveryPlayerInBothModes(bool clusterMode)
    {
        var previous = ServerCommands.Registrar;
        try
        {
            var registrar = new Registrar();
            ServerCommands.Registrar = registrar;
            AdminPlugin.RegisterCommands(clusterMode);
            Assert.Contains(typeof(AuthorizeCommand), registrar.Types);
            Assert.Equal(!clusterMode, registrar.Types.Contains(typeof(StopCommand)));
            Assert.True(PlayerCommands.Registered);
        }
        finally
        {
            ServerCommands.Registrar = previous;
        }

        foreach (
            var method in typeof(AuthorizeCommand)
                .GetMethods()
                .Where(m => m.IsDefined(typeof(CommandAttribute)))
        )
            Assert.Equal(
                MyPromoteLevel.None,
                method.GetCustomAttribute<PermissionAttribute>()!.Level
            );
    }

    [Fact]
    public void PlayerCommandsSendTheCallersIdentityAndRefuseTheConsole()
    {
        var registry = new CommandRegistry();
        registry.RegisterModule(typeof(AuthorizeCommand), "quasar-agent");
        var previousRegistrar = ServerCommands.Registrar;
        var registrar = new Registrar(registry);
        ServerCommands.Registrar = registrar;
        try
        {
            // Quasar sends the plugins' commands on connecting; the game thread registers them.
            PlayerCommands.Offer([
                new ChatCommandInfo
                {
                    Name = "ask",
                    Description = "Ask the server's assistant",
                    Argument = "question",
                    Acknowledgement = "Asking the assistant...",
                },
                new ChatCommandInfo { Name = "Bad Name" },
            ]);
            PlayerCommands.RegisterPending();
        }
        finally
        {
            ServerCommands.Registrar = previousRegistrar;
        }
        var ask = Assert.Single(registrar.Types);
        Assert.True(typeof(PluginChatCommand).IsAssignableFrom(ask));
        Assert.Equal("ask", ask.GetCustomAttribute<CommandRootAttribute>()!.Prefix);
        var use = ask.GetMethods().Single(m => m.IsDefined(typeof(CommandAttribute)));
        Assert.Equal(MyPromoteLevel.None, use.GetCustomAttribute<PermissionAttribute>()!.Level);
        Assert.Equal("question", use.GetParameters().Single().Name);
        var dispatcher = new CommandDispatcher(registry);
        var sent = new List<PlayerRequest>();
        var previous = PlayerCommands.Send;
        PlayerCommands.Send = request =>
        {
            sent.Add(request);
            return true;
        };
        try
        {
            var responder = new Responder();
            Assert.True(
                dispatcher.Handle(
                    "!ask where is my ship?",
                    new CommandCaller(Pilot, 34, "Pilot", MyPromoteLevel.None),
                    responder
                )
            );
            var asked = Assert.Single(sent);
            Assert.Equal("ask", asked.Kind);
            Assert.Equal((long)Pilot, asked.SteamId);
            Assert.Equal(34, asked.IdentityId);
            Assert.Equal("Pilot", asked.PlayerName);
            Assert.Equal("where is my ship?", asked.Text);
            Assert.Equal(32, asked.RequestId.Length);
            Assert.Equal("Asking the assistant...", Assert.Single(responder.Replies).Text);

            Assert.True(
                dispatcher.Handle(
                    "!authorize",
                    new CommandCaller(Pilot, 34, "Pilot", MyPromoteLevel.None),
                    new Responder()
                )
            );
            Assert.Equal(PlayerRequest.AuthorizeKind, sent[^1].Kind);
            Assert.Equal(string.Empty, sent[^1].Text);

            sent.Clear();
            var refused = new Responder();
            dispatcher.Handle(
                "!ask hello",
                new CommandCaller(0, 0, "Quasar", MyPromoteLevel.Owner, isConsole: true),
                refused
            );
            dispatcher.Handle(
                "!authorize",
                new CommandCaller(0, 0, "Ghost", MyPromoteLevel.None),
                refused
            );
            dispatcher.Handle(
                "!ask",
                new CommandCaller(Pilot, 34, "Pilot", MyPromoteLevel.None),
                refused
            );
            Assert.Empty(sent);
            Assert.Equal(3, refused.Replies.Count);

            PlayerCommands.Send = _ => false;
            var offline = new Responder();
            dispatcher.Handle(
                "!ask hello",
                new CommandCaller(Pilot, 34, "Pilot", MyPromoteLevel.None),
                offline
            );
            Assert.Contains("not reachable", Assert.Single(offline.Replies).Text);
        }
        finally
        {
            PlayerCommands.Send = previous;
        }
    }

    [Fact]
    public void PlayerRequestTravelsAsItsOwnWireKind()
    {
        var bytes = AgentConnection.SerializeMessage(
            new AgentWireMessage
            {
                Kind = WireMessageKind.PlayerRequest,
                PlayerRequest = new PlayerRequest
                {
                    RequestId = "r",
                    Kind = "ask",
                    SteamId = (long)Pilot,
                    Text = "hi",
                },
            }
        );
        var json = JsonDocument.Parse(Encoding.UTF8.GetString(bytes)).RootElement;
        Assert.Equal("player-request", json.GetProperty("kind").GetString());
        Assert.Equal(
            (long)Pilot,
            json.GetProperty("playerRequest").GetProperty("steamId").GetInt64()
        );
        var read = JsonSerializer.Deserialize<AgentWireMessage>(
            json,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)
        )!;
        Assert.Equal("hi", read.PlayerRequest!.Text);

        var commands = JsonSerializer.Deserialize<AgentWireMessage>(
            """{"kind":"chat-commands","chatCommands":[{"name":"ask","description":"d","argument":"question","acknowledgement":"a"}]}""",
            new JsonSerializerOptions(JsonSerializerDefaults.Web)
        )!;
        Assert.Equal(WireMessageKind.ChatCommands, commands.Kind);
        Assert.Equal("question", Assert.Single(commands.ChatCommands!).Argument);
    }
}
