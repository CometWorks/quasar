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

    private sealed class Registrar : ICommandRegistrar
    {
        internal readonly List<Type> Types = [];

        public void Register(Assembly assembly) =>
            throw new InvalidOperationException("Commands must be named.");

        public void Register(Assembly assembly, params Type[] moduleTypes) =>
            Types.AddRange(moduleTypes);
    }

    private sealed class Responder : ICommandResponder
    {
        internal readonly List<CommandReply> Replies = [];

        public void Send(in CommandReply reply, in CommandCaller caller) => Replies.Add(reply);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AskAndLinkAreRegisteredForEveryPlayerInBothModes(bool clusterMode)
    {
        var previous = ServerCommands.Registrar;
        try
        {
            var registrar = new Registrar();
            ServerCommands.Registrar = registrar;
            AdminPlugin.RegisterCommands(clusterMode);
            Assert.Contains(typeof(AskCommand), registrar.Types);
            Assert.Contains(typeof(LinkCommand), registrar.Types);
            Assert.Equal(!clusterMode, registrar.Types.Contains(typeof(StopCommand)));
            Assert.True(PlayerCommands.Registered);
        }
        finally
        {
            ServerCommands.Registrar = previous;
        }

        foreach (var type in new[] { typeof(AskCommand), typeof(LinkCommand) })
        foreach (var method in type.GetMethods().Where(m => m.IsDefined(typeof(CommandAttribute))))
            Assert.Equal(
                MyPromoteLevel.None,
                method.GetCustomAttribute<PermissionAttribute>()!.Level
            );
    }

    [Fact]
    public void PlayerCommandsSendTheCallersIdentityAndRefuseTheConsole()
    {
        var registry = new CommandRegistry();
        registry.RegisterModule(typeof(AskCommand), "quasar-agent");
        registry.RegisterModule(typeof(LinkCommand), "quasar-agent");
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
            var ask = Assert.Single(sent);
            Assert.Equal(PlayerRequest.AskKind, ask.Kind);
            Assert.Equal((long)Pilot, ask.SteamId);
            Assert.Equal(34, ask.IdentityId);
            Assert.Equal("Pilot", ask.PlayerName);
            Assert.Equal("where is my ship?", ask.Text);
            Assert.Equal(32, ask.RequestId.Length);
            Assert.Equal("Asking the assistant...", Assert.Single(responder.Replies).Text);

            Assert.True(
                dispatcher.Handle(
                    "!link",
                    new CommandCaller(Pilot, 34, "Pilot", MyPromoteLevel.None),
                    new Responder()
                )
            );
            Assert.Equal(PlayerRequest.LinkKind, sent[^1].Kind);
            Assert.Equal(string.Empty, sent[^1].Text);

            sent.Clear();
            var refused = new Responder();
            dispatcher.Handle(
                "!ask hello",
                new CommandCaller(0, 0, "Quasar", MyPromoteLevel.Owner, isConsole: true),
                refused
            );
            dispatcher.Handle(
                "!link",
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
                    Kind = PlayerRequest.AskKind,
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
    }
}
