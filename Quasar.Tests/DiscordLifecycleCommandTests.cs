using System.Reflection;
using Discord;
using Magnetar.Protocol.Runtime;
using Microsoft.Extensions.Logging.Abstractions;
using Quasar.Models;
using Quasar.Services;
using Quasar.Services.Backup;
using Quasar.Services.Discord;
using Xunit;

namespace Quasar.Tests;

[CollectionDefinition("Discord lifecycle", DisableParallelization = true)]
public sealed class DiscordLifecycleCollection;

[Collection("Discord lifecycle")]
public sealed class DiscordLifecycleCommandTests
{
    [Theory]
    [InlineData("start", DedicatedServerGoalState.Off, DedicatedServerGoalState.On)]
    [InlineData("stop", DedicatedServerGoalState.On, DedicatedServerGoalState.Off)]
    public async Task LifecycleCommandPersistsGoalBeforeReconciliation(
        string verb,
        DedicatedServerGoalState initialGoal,
        DedicatedServerGoalState expectedGoal)
    {
        var root = Path.Combine(Path.GetTempPath(), $"quasar-discord-lifecycle-{Guid.NewGuid():N}");
        var pathCache = typeof(MagnetarPaths).GetField("_cachedQuasarDirectory", BindingFlags.NonPublic | BindingFlags.Static)!;
        var previousRoot = pathCache.GetValue(null);
        pathCache.SetValue(null, root);
        try
        {
            using var catalog = new DedicatedServerCatalog(NullLogger<DedicatedServerCatalog>.Instance);
            await catalog.UpsertAsync(new DedicatedServerDefinition
            {
                UniqueName = "test",
                WorldSaveName = "World",
                GoalState = initialGoal,
            });

            var registry = new AgentRegistry(null!, null!, null!, null!);
            var restores = new ServerRestoreCoordinator();
            // Exercise real lifecycle methods without launching a game process.
            Assert.True(restores.TryBeginRestore("test", out var restore));
            using var restoreScope = restore;
            using var supervisor = new DedicatedServerSupervisor(
                catalog, registry, null!, null!, null!,
                new WebServiceOptions(), restores, NullLogger<DedicatedServerSupervisor>.Instance);
            var syncDefinitions = typeof(DedicatedServerSupervisor)
                .GetMethod("SyncDefinitions", BindingFlags.NonPublic | BindingFlags.Instance)!
                .CreateDelegate<Action>(supervisor);
            syncDefinitions();
            catalog.Changed += syncDefinitions;

            var replies = new List<string>();
            var channel = Stub<IMessageChannel>((method, args) =>
            {
                Assert.Equal("SendMessageAsync", method.Name);
                replies.Add((string)args![0]!);
                return Task.FromResult<IUserMessage>(null!);
            });
            var message = Stub<IMessage>((method, _) => method.Name == "get_Channel" ? channel : 1UL);
            var dispatcher = new DiscordCommandDispatcher(
                registry, supervisor, catalog, null!, NullLogger<DiscordCommandDispatcher>.Instance);

            for (var attempt = 0; attempt < 2; attempt++)
            {
                await dispatcher.DispatchAsync(new DiscordServerOptions { UniqueName = "test" }, verb, "", message);

                Assert.Equal($"{char.ToUpperInvariant(verb[0])}{verb[1..]} requested.", replies[^1]);
                Assert.Equal(expectedGoal, catalog.GetServer("test")!.GoalState);
                Assert.Equal(expectedGoal == DedicatedServerGoalState.On, catalog.GetServer("test")!.AutoStart);
                using var reloaded = new DedicatedServerCatalog(NullLogger<DedicatedServerCatalog>.Instance);
                Assert.Equal(expectedGoal, reloaded.GetServer("test")!.GoalState);

                await (Task)typeof(DedicatedServerSupervisor)
                    .GetMethod("ReconcileAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .Invoke(supervisor, [CancellationToken.None])!;
                var snapshot = Assert.Single(supervisor.GetSnapshots());
                Assert.Equal(expectedGoal, snapshot.GoalState);
                Assert.Equal(DedicatedServerProcessState.Stopped, snapshot.State);
                Assert.Null(snapshot.LastRestart);
                if (verb == "start")
                    Assert.Contains("restore is in progress", snapshot.LastMessage);
            }
        }
        finally
        {
            pathCache.SetValue(null, previousRoot);
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static T Stub<T>(Func<MethodInfo, object?[]?, object?> invoke) where T : class
    {
        var stub = DispatchProxy.Create<T, DiscordProxy>();
        ((DiscordProxy)(object)stub).Handler = invoke;
        return stub;
    }

    public class DiscordProxy : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?> Handler { get; set; } = null!;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Handler(targetMethod!, args);
    }
}
