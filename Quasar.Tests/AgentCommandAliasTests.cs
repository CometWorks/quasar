using System.Reflection;
using PluginSdk.Commands;
using PluginSdk.Config;
using Quasar.Agent;
using Quasar.Models;
using Quasar.Services;
using VRage.Game.ModAPI;
using Xunit;

namespace Quasar.Tests;

[Collection("ServerCommands")]
public sealed class AgentCommandAliasTests
{
    [CommandRoot("ess")]
    public sealed class Commands : CommandModule
    {
        [Command("grids list")]
        public string List(string name) => $"{Context.Caller.SteamId}/{Context.Caller.IdentityId}/{Context.Caller.IsConsole}/{name}";
    }

    private sealed class Host : ICommandRegistrar, ICommandAliasConfigurator
    {
        private readonly CommandRegistry registry = new();
        internal readonly CommandDispatcher Dispatcher;
        internal Host() { registry.RegisterModule(typeof(Commands), "ess"); Dispatcher = new(registry); }
        public void Register(Assembly assembly) => registry.RegisterAssembly(assembly);
        public void Register(Assembly assembly, params Type[] moduleTypes) { foreach (var type in moduleTypes) registry.RegisterModule(type, "test"); }
        public void SetAliases(IEnumerable<KeyValuePair<string, string>> aliases) => Dispatcher.SetAliases(aliases);
    }

    private sealed class Responder : ICommandResponder
    {
        internal readonly List<CommandReply> Replies = [];
        public void Send(in CommandReply reply, in CommandCaller caller) => Replies.Add(reply);
    }

    [Fact]
    public void ProfileThroughRealSdkConfigAndAgentFacadeKeepsCallerAndTargetPermissions()
    {
        var previous = ServerCommands.Registrar;
        try
        {
            var host = new Host();
            ServerCommands.Registrar = host;
            var profile = new QuasarConfigProfile { CommandAliases = [new() { Source = "!grids list", Target = "!ess grids list" }] };
            var config = ConfigStorage.LoadJson<CommandAliasConfiguration>(QuasarCommandAliases.Serialize(profile));
            CommandAliasConfiguration.Apply(config.Aliases);
            var responder = new Responder();
            Assert.True(host.Dispatcher.Handle("!grids list \"Big Ship\"", new CommandCaller(12, 34, "Player", MyPromoteLevel.None), responder));
            Assert.Contains("permission", Assert.Single(responder.Replies).Text);
            responder.Replies.Clear();
            host.Dispatcher.Handle("!grids list \"Big Ship\"", new CommandCaller(12, 34, "Admin", MyPromoteLevel.Admin), responder);
            Assert.Equal("12/34/False/Big Ship", Assert.Single(responder.Replies).Text);
            CommandAliasConfiguration.Apply(new Dictionary<string, string>());
            Assert.False(host.Dispatcher.Handle("!grids list", new CommandCaller(12, 34, "Admin", MyPromoteLevel.Admin), new Responder()));
        }
        finally { ServerCommands.Registrar = previous; }
    }
}
