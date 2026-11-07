using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using Magnetar.Protocol.Model;
using Magnetar.Protocol.Runtime;
using Magnetar.Protocol.Transport;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Quasar.Models;
using Quasar.Plugin.Abstractions.Operations;
using Quasar.Plugin.Abstractions.Security;
using Quasar.Services;
using Quasar.Services.Audit;
using Quasar.Services.Auth;
using Quasar.Services.Backup;
using Quasar.Services.Plugins;
using Quasar.Services.PluginSdk;
using Xunit;
using Admin = CometWorks.ClusterGateway.AdminContract.V1;
using QuasarPolicyNames = Quasar.Services.Auth.QuasarPolicyNames;

namespace Quasar.Tests;

[Collection("Exact server creation")]
public sealed class QuasarCoreOperationTests : IDisposable
{
    private const string Core = QuasarCoreOperations.PluginId;
    private const string SteamId = "76561198000000042";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "quasar-core-ops-" + Guid.NewGuid().ToString("N")
    );
    private readonly FieldInfo _cache = typeof(MagnetarPaths).GetField(
        "_cachedQuasarDirectory",
        BindingFlags.NonPublic | BindingFlags.Static
    )!;
    private readonly object? _previous;
    private readonly RbacConfigCatalog _rbac;
    private readonly DedicatedServerCatalog _servers;
    private readonly QuasarConfigProfileCatalog _profiles;
    private readonly KnownPlayerCatalog _known;
    private readonly AgentRegistry _registry;
    private readonly ServerRestoreCoordinator _restores = new();
    private readonly DedicatedServerSupervisor _supervisor;
    private readonly ServiceProvider _services;
    private readonly ClusterCatalog _clusters;
    private readonly ClusterPlayerTests.Gateway _gateway = new();
    private readonly ClusterPlayerService _clusterPlayers;
    private readonly PluginLogStream _logs = new();
    private readonly List<ServerCommandEnvelope> _commands = [];
    private readonly List<EntitySummary> _alphaEntities = [];
    private ClusterEntityTests.Fixture? _entityFixture;
    private bool _commandsSucceed = true;

    public QuasarCoreOperationTests()
    {
        _previous = _cache.GetValue(null);
        _cache.SetValue(null, _root);
        Directory.CreateDirectory(_root);
        _rbac = new(NullLogger<RbacConfigCatalog>.Instance);
        _servers = new(NullLogger<DedicatedServerCatalog>.Instance);
        _profiles = new(NullLogger<QuasarConfigProfileCatalog>.Instance);
        _known = new(NullLogger<KnownPlayerCatalog>.Instance);
        _registry = new(_known, null!, null!, null!);
        var options = new WebServiceOptions { BackupDirectory = Path.Combine(_root, "backups") };
        _supervisor = new(
            _servers,
            _registry,
            null!,
            null!,
            null!,
            options,
            _restores,
            NullLogger<DedicatedServerSupervisor>.Instance
        );
        _servers.Changed += typeof(DedicatedServerSupervisor)
            .GetMethod("SyncDefinitions", BindingFlags.NonPublic | BindingFlags.Instance)!
            .CreateDelegate<Action>(_supervisor);
        var backups = new QuasarBackupService(
            NullLogger<QuasarBackupService>.Instance,
            options,
            _known,
            null!,
            _servers,
            _supervisor,
            _restores
        );

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(_rbac);
        services.AddSingleton(new QuasarAuthOptions());
        services.AddSingleton<QuasarRoleMapper>();
        services.AddAuthorization(Program.ConfigureAuthorizationPolicies);
        services.AddSingleton<IAuthorizationPolicyProvider, QuasarAuthorizationPolicyProvider>();
        services.AddScoped<QuasarOperationService>();
        services.AddScoped<IQuasarOperationDispatcher>(provider =>
            provider.GetRequiredService<QuasarOperationService>()
        );
        services.AddSingleton(new QuasarAuditOptions { Directory = Path.Combine(_root, "audit") });
        services.AddSingleton<QuasarAuditLog>();
        services.AddSingleton<QuasarAgentPrincipals>();
        services.AddSingleton(_servers);
        services.AddSingleton(_profiles);
        services.AddSingleton(_known);
        services.AddSingleton(_registry);
        services.AddSingleton(_supervisor);
        services.AddSingleton(backups);
        var backupSettings = new QuasarBackupSettingsService(
            NullLogger<QuasarBackupSettingsService>.Instance,
            options
        );
        services.AddSingleton(backupSettings);
        services.AddSingleton(
            new AutomaticBackupService(
                backups,
                backupSettings,
                _servers,
                NullLogger<AutomaticBackupService>.Instance
            )
        );
        // After the Quasar directory points at _root, or the catalog writes next to the test binaries.
        _clusters = new(NullLogger<ClusterCatalog>.Instance, new ConfigurationBuilder().Build());
        services.AddSingleton(_clusters);
        _clusterPlayers = new(
            _clusters,
            new(new HttpClient(_gateway)),
            _known,
            NullLogger<ClusterPlayerService>.Instance
        );
        services.AddSingleton(_clusterPlayers);
        // Created on first use, so only entity tests get its cluster agents.
        services.AddSingleton(_ => Entities.Service);
        var gateway = new ClusterGatewayClient(new HttpClient(_gateway));
        var clusterOperations = new ClusterOperationStore(
            Path.Combine(_root, "cluster-operations")
        );
        services.AddSingleton(gateway);
        services.AddSingleton(clusterOperations);
        services.AddSingleton(
            new ClusterCommandService(
                _clusters,
                clusterOperations,
                gateway,
                new ClusterHostClient(new HttpClient(new NoHost()))
            )
        );
        services.AddSingleton(_logs);
        services.AddSingleton(new ClusterFleetService(gateway, _registry, _logs));
        services.AddQuasarCoreOperations();
        _services = services.BuildServiceProvider();
    }

    public void Dispose()
    {
        _services.Dispose();
        _servers.Dispose();
        _profiles.Dispose();
        _rbac.Dispose();
        _supervisor.Dispose();
        _clusterPlayers.Dispose();
        _entityFixture?.Dispose();
        _clusters.Dispose();
        _gateway.Dispose();
        _cache.SetValue(null, _previous);
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException) { }
    }

    [Fact]
    public void EveryCoreOperationDeclaresItsPolicyEffectAndDescription()
    {
        var expected = new Dictionary<
            string,
            (string Policy, QuasarOperationEffect Effect, QuasarReversal Reversal)
        >
        {
            ["servers.list"] = (
                QuasarPolicyNames.CanView,
                QuasarOperationEffect.Read,
                QuasarReversal.NotReversible
            ),
            ["servers.status"] = (
                QuasarPolicyNames.CanView,
                QuasarOperationEffect.Read,
                QuasarReversal.NotReversible
            ),
            ["servers.start"] = (
                QuasarPolicyNames.CanControlServers,
                QuasarOperationEffect.Change,
                QuasarReversal.Undo
            ),
            ["servers.stop"] = (
                QuasarPolicyNames.CanControlServers,
                QuasarOperationEffect.Change,
                QuasarReversal.Undo
            ),
            ["servers.restart"] = (
                QuasarPolicyNames.CanControlServers,
                QuasarOperationEffect.Change,
                QuasarReversal.NothingToReverse
            ),
            ["servers.save"] = (
                QuasarPolicyNames.CanControlServers,
                QuasarOperationEffect.Change,
                QuasarReversal.NothingToReverse
            ),
            ["logs.server.tail"] = (
                QuasarPolicyNames.CanView,
                QuasarOperationEffect.Read,
                QuasarReversal.NotReversible
            ),
            ["logs.magnetar.tail"] = (
                QuasarPolicyNames.CanView,
                QuasarOperationEffect.Read,
                QuasarReversal.NotReversible
            ),
            ["servers.definition.get"] = (
                QuasarPolicyNames.CanView,
                QuasarOperationEffect.Read,
                QuasarReversal.NotReversible
            ),
            ["servers.definition.set"] = (
                QuasarPolicyNames.CanEditServers,
                QuasarOperationEffect.Change,
                QuasarReversal.Undo
            ),
            ["servers.definition.save"] = (
                QuasarPolicyNames.CanEditServers,
                QuasarOperationEffect.Change,
                QuasarReversal.Undo
            ),
            ["config.profiles.list"] = (
                QuasarPolicyNames.CanView,
                QuasarOperationEffect.Read,
                QuasarReversal.NotReversible
            ),
            ["config.settings.get"] = (
                QuasarPolicyNames.CanView,
                QuasarOperationEffect.Read,
                QuasarReversal.NotReversible
            ),
            ["config.settings.set"] = (
                QuasarPolicyNames.CanEditConfigs,
                QuasarOperationEffect.Change,
                QuasarReversal.Undo
            ),
            ["config.profile.save"] = (
                QuasarPolicyNames.CanEditConfigs,
                QuasarOperationEffect.Change,
                QuasarReversal.Undo
            ),
            ["config.plugins.list"] = (
                QuasarPolicyNames.CanView,
                QuasarOperationEffect.Read,
                QuasarReversal.NotReversible
            ),
            ["config.mods.list"] = (
                QuasarPolicyNames.CanView,
                QuasarOperationEffect.Read,
                QuasarReversal.NotReversible
            ),
            ["config.plugins.set"] = (
                QuasarPolicyNames.CanEditConfigs,
                QuasarOperationEffect.Change,
                QuasarReversal.Undo
            ),
            ["config.mods.add"] = (
                QuasarPolicyNames.CanEditConfigs,
                QuasarOperationEffect.Change,
                QuasarReversal.Undo
            ),
            ["config.mods.remove"] = (
                QuasarPolicyNames.CanEditConfigs,
                QuasarOperationEffect.Change,
                QuasarReversal.Undo
            ),
            ["players.list"] = (
                QuasarPolicyNames.CanView,
                QuasarOperationEffect.Read,
                QuasarReversal.NotReversible
            ),
            ["players.kick"] = (
                QuasarPolicyNames.CanControlServers,
                QuasarOperationEffect.Change,
                QuasarReversal.NothingToReverse
            ),
            ["players.ban"] = (
                QuasarPolicyNames.CanControlServers,
                QuasarOperationEffect.Change,
                QuasarReversal.Undo
            ),
            ["players.unban"] = (
                QuasarPolicyNames.CanControlServers,
                QuasarOperationEffect.Change,
                QuasarReversal.Undo
            ),
            ["players.promote"] = (
                QuasarPolicyNames.CanControlServers,
                QuasarOperationEffect.Change,
                QuasarReversal.Undo
            ),
            ["players.demote"] = (
                QuasarPolicyNames.CanControlServers,
                QuasarOperationEffect.Change,
                QuasarReversal.Undo
            ),
            ["players.promotion.set"] = (
                QuasarPolicyNames.CanControlServers,
                QuasarOperationEffect.Change,
                QuasarReversal.Undo
            ),
            ["players.kick-cooldown.clear"] = (
                QuasarPolicyNames.CanControlServers,
                QuasarOperationEffect.Change,
                QuasarReversal.NothingToReverse
            ),
            ["backups.list"] = (
                QuasarPolicyNames.CanControlServers,
                QuasarOperationEffect.Read,
                QuasarReversal.NotReversible
            ),
            ["backups.world.create"] = (
                QuasarPolicyNames.CanControlServers,
                QuasarOperationEffect.Change,
                QuasarReversal.NothingToReverse
            ),
            ["backups.server.create"] = (
                QuasarPolicyNames.CanControlServers,
                QuasarOperationEffect.Change,
                QuasarReversal.NothingToReverse
            ),
            ["backups.settings.read"] = (
                QuasarPolicyNames.CanManageSecurity,
                QuasarOperationEffect.Read,
                QuasarReversal.NotReversible
            ),
            ["backups.restore"] = (
                QuasarPolicyNames.CanManageSecurity,
                QuasarOperationEffect.Change,
                QuasarReversal.NotReversible
            ),
            ["cluster.list"] = (
                QuasarPolicyNames.ClusterQuery,
                QuasarOperationEffect.Read,
                QuasarReversal.NotReversible
            ),
            ["cluster.status"] = (
                QuasarPolicyNames.ClusterQuery,
                QuasarOperationEffect.Read,
                QuasarReversal.NotReversible
            ),
            ["entities.list"] = (
                QuasarPolicyNames.CanView,
                QuasarOperationEffect.Read,
                QuasarReversal.NotReversible
            ),
            ["entities.delete"] = (
                QuasarPolicyNames.CanControlServers,
                QuasarOperationEffect.Change,
                QuasarReversal.NotReversible
            ),
            ["chat.messages"] = (
                QuasarPolicyNames.CanView,
                QuasarOperationEffect.Read,
                QuasarReversal.NotReversible
            ),
            ["chat.commands"] = (
                QuasarPolicyNames.CanView,
                QuasarOperationEffect.Read,
                QuasarReversal.NotReversible
            ),
            ["chat.send"] = (
                QuasarPolicyNames.CanControlServers,
                QuasarOperationEffect.Change,
                QuasarReversal.NotReversible
            ),
            ["chat.command.run"] = (
                QuasarPolicyNames.CanControlServers,
                QuasarOperationEffect.Change,
                QuasarReversal.NotReversible
            ),
            ["activity.recent"] = (
                QuasarPolicyNames.CanView,
                QuasarOperationEffect.Read,
                QuasarReversal.NotReversible
            ),
            ["audit.list"] = (
                QuasarPolicyNames.CanManageSecurity,
                QuasarOperationEffect.Read,
                QuasarReversal.NotReversible
            ),
            ["cluster.operation"] = (
                QuasarPolicyNames.ClusterQuery,
                QuasarOperationEffect.Read,
                QuasarReversal.NotReversible
            ),
            ["cluster.logs.tail"] = (
                QuasarPolicyNames.ClusterQuery,
                QuasarOperationEffect.Read,
                QuasarReversal.NotReversible
            ),
            ["cluster.events.tail"] = (
                QuasarPolicyNames.ClusterQuery,
                QuasarOperationEffect.Read,
                QuasarReversal.NotReversible
            ),
            ["cluster.start"] = (
                QuasarPolicyNames.ClusterManage,
                QuasarOperationEffect.Change,
                QuasarReversal.Undo
            ),
            ["cluster.stop"] = (
                QuasarPolicyNames.ClusterManage,
                QuasarOperationEffect.Change,
                QuasarReversal.Undo
            ),
            ["cluster.restart"] = (
                QuasarPolicyNames.ClusterManage,
                QuasarOperationEffect.Change,
                QuasarReversal.NothingToReverse
            ),
            ["cluster.save"] = (
                QuasarPolicyNames.ClusterManage,
                QuasarOperationEffect.Change,
                QuasarReversal.NothingToReverse
            ),
            ["cluster.gateway.restart"] = (
                QuasarPolicyNames.ClusterManage,
                QuasarOperationEffect.Change,
                QuasarReversal.NothingToReverse
            ),
            ["cluster.services.recover"] = (
                QuasarPolicyNames.ClusterManage,
                QuasarOperationEffect.Change,
                QuasarReversal.NotReversible
            ),
            ["cluster.recover"] = (
                QuasarPolicyNames.ClusterManage,
                QuasarOperationEffect.Change,
                QuasarReversal.NotReversible
            ),
            ["cluster.players.kick"] = (
                QuasarPolicyNames.ClusterManage,
                QuasarOperationEffect.Change,
                QuasarReversal.NothingToReverse
            ),
            ["cluster.players.ban"] = (
                QuasarPolicyNames.ClusterManage,
                QuasarOperationEffect.Change,
                QuasarReversal.Undo
            ),
            ["cluster.players.unban"] = (
                QuasarPolicyNames.ClusterManage,
                QuasarOperationEffect.Change,
                QuasarReversal.Undo
            ),
            ["cluster.players.kick-cooldown.clear"] = (
                QuasarPolicyNames.ClusterManage,
                QuasarOperationEffect.Change,
                QuasarReversal.NothingToReverse
            ),
            ["cluster.players.promotion.set"] = (
                QuasarPolicyNames.ClusterManage,
                QuasarOperationEffect.Change,
                QuasarReversal.Undo
            ),
        };
        var operations = _services
            .GetServices<QuasarOperation>()
            .ToDictionary(operation => operation.OperationId);
        foreach (var (id, (policy, effect, reversal)) in expected)
        {
            var operation = operations[id];
            Assert.Equal(new[] { policy }, operation.DefaultPolicies);
            Assert.Equal(effect, operation.Metadata.Effect);
            Assert.Equal(reversal, operation.Metadata.Reversal);
        }
        Assert.All(
            operations.Values,
            operation =>
            {
                Assert.False(
                    string.IsNullOrWhiteSpace(operation.Metadata.Description),
                    operation.OperationId
                );
                Assert.NotNull(operation.RequestType);
            }
        );
        foreach (
            var denied in new[]
            {
                "servers.definition.save",
                "config.profile.save",
                "backup.read",
                "logs.discord.read",
            }
        )
            Assert.Equal(QuasarAgentAccess.Denied, operations[denied].Metadata.AgentAccess);
        Assert.Equal(TimeSpan.FromSeconds(120), operations["servers.start"].Metadata.Deadline);
        Assert.Equal(TimeSpan.FromSeconds(300), operations["servers.restart"].Metadata.Deadline);
        Assert.Equal(TimeSpan.FromSeconds(300), operations["servers.save"].Metadata.Deadline);
        foreach (var read in new[] { "cluster.players", "cluster.entities.list" })
        {
            Assert.Equal(
                [QuasarPolicyNames.CanView, QuasarPolicyNames.ClusterQuery],
                operations[read].DefaultPolicies
            );
            Assert.Equal(QuasarOperationEffect.Read, operations[read].Metadata.Effect);
        }
        var clusterDelete = operations["cluster.entities.delete"];
        Assert.Equal(
            [QuasarPolicyNames.CanControlServers, QuasarPolicyNames.ClusterManage],
            clusterDelete.DefaultPolicies
        );
        Assert.Equal(
            (QuasarOperationEffect.Change, QuasarReversal.NotReversible),
            (clusterDelete.Metadata.Effect, clusterDelete.Metadata.Reversal)
        );
        // Destructive operations are a deliberate choice, each one listed here.
        Assert.Equal(
            ["backups.restore", "cluster.entities.delete", "entities.delete"],
            operations
                .Keys.Where(id =>
                    id.Contains("delete")
                    || id.Contains("kill")
                    || id.Contains("restore")
                    || id.Contains("clone")
                )
                .Order()
        );
    }

    [Fact]
    public async Task StartAndStopAreAuditedAndUndoneWhileTheGoalIsUnchanged()
    {
        await CreateServerAsync("alpha", world: true);
        // A restore in progress defers the launch, so no process starts.
        Assert.True(_restores.TryBeginRestore("alpha", out var restore));
        using var _ = restore;

        Assert.Equal(
            403,
            (
                await CallAsync(
                    Person(QuasarRoles.Viewer),
                    "servers.start",
                    new { uniqueName = "alpha" }
                )
            ).StatusCode
        );
        Assert.Equal(
            200,
            (
                await CallAsync(
                    Person(QuasarRoles.Viewer),
                    "servers.status",
                    new { uniqueName = "alpha" }
                )
            ).StatusCode
        );
        Assert.Equal(
            404,
            (await CallAsync(Editor(), "servers.start", new { uniqueName = "missing" })).StatusCode
        );
        Assert.Equal(
            200,
            (await CallAsync(Editor(), "servers.start", new { uniqueName = "alpha" })).StatusCode
        );
        Assert.Equal(DedicatedServerGoalState.On, _servers.GetServer("alpha")!.GoalState);
        var start = await LatestAsync("servers.start");
        Assert.Equal("alpha", start.Server);
        Assert.Equal(QuasarReversal.Undo, start.Reversal);
        Assert.Equal(QuasarUndoStatus.Done, (await UndoAsync(start.Id)).Status);
        Assert.Equal(DedicatedServerGoalState.Off, _servers.GetServer("alpha")!.GoalState);

        // Stopping a stopped server changes nothing, so there is nothing to undo.
        Assert.Equal(
            200,
            (await CallAsync(Editor(), "servers.stop", new { uniqueName = "alpha" })).StatusCode
        );
        Assert.Equal(QuasarReversal.NotReversible, (await LatestAsync("servers.stop")).Reversal);

        Assert.Equal(
            200,
            (await CallAsync(Editor(), "servers.start", new { uniqueName = "alpha" })).StatusCode
        );
        await _supervisor.SetGoalStateAsync("alpha", DedicatedServerGoalState.Off);
        var conflict = await UndoAsync((await LatestAsync("servers.start")).Id);
        Assert.Equal(QuasarUndoStatus.Conflict, conflict.Status);
        Assert.Contains("Off", conflict.Detail);
    }

    [Fact]
    public async Task StartRefusesAServerWithoutAWorld()
    {
        await CreateServerAsync("alpha", world: false);
        var refused = await CallAsync(Editor(), "servers.start", new { uniqueName = "alpha" });
        Assert.Equal(409, refused.StatusCode);
        Assert.Contains("World save folder not found", refused.Error);
    }

    [Fact]
    public async Task LogSlicesTakeTailHeadFilterAndByteLimits()
    {
        var path = Path.Combine(_root, "test.log");
        await File.WriteAllTextAsync(
            path,
            string.Concat(Enumerable.Range(1, 10).Select(i => $"line {i}\r\n"))
        );
        var tail = await Slice(path, new() { Lines = 3 });
        Assert.Equal(new[] { "line 8", "line 9", "line 10" }, tail.Lines);
        Assert.True(tail.Truncated);
        Assert.Null(tail.TotalLines);
        var head = await Slice(path, new() { Lines = 2, From = "head" });
        Assert.Equal(new[] { "line 1", "line 2" }, head.Lines);
        var filtered = await Slice(path, new() { Filter = "LINE 1" });
        Assert.Equal(new[] { "line 1", "line 10" }, filtered.Lines);
        Assert.False(filtered.Truncated);
        Assert.Equal(2, filtered.TotalLines);
        var limited = await Slice(path, new() { MaxBytes = 14 });
        Assert.Equal(new[] { "line ", "line 10" }, limited.Lines);
        Assert.True(limited.Truncated);
        Assert.Equal(
            400,
            (
                await Assert.ThrowsAsync<QuasarOperationException>(() =>
                    Slice(path, new() { From = "middle" })
                )
            ).StatusCode
        );

        // Lines that cross the 64 KiB read chunks come back whole.
        var big = Path.Combine(_root, "big.log");
        await File.WriteAllTextAsync(
            big,
            string.Concat(Enumerable.Range(1, 20000).Select(i => $"entry {i} Δ\n"))
                + "last without newline"
        );
        var end = await Slice(big, new() { Lines = 3 });
        Assert.Equal(new[] { "entry 19999 Δ", "entry 20000 Δ", "last without newline" }, end.Lines);
        var all = await Slice(
            big,
            new()
            {
                Lines = 2000,
                MaxBytes = 65536,
                Filter = "entry 1999",
            }
        );
        Assert.Equal(11, all.TotalLines);
        Assert.Equal("entry 1999 Δ", all.Lines[0]);
        Assert.Equal("entry 19999 Δ", all.Lines[^1]);
    }

    [Fact]
    public async Task LogTailResolvesTheServerLogThroughTheDispatcher()
    {
        await CreateServerAsync("alpha", world: false);
        var directory = DedicatedServerPathResolver
            .Resolve(_servers.GetServer("alpha")!)
            .DedicatedServerAppDataPath;
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(
            Path.Combine(directory, "SpaceEngineersDedicated-1.log"),
            "a\nb\n"
        );
        var result = await CallAsync(
            Person(QuasarRoles.Viewer),
            "logs.server.tail",
            new { uniqueName = "alpha", lines = 1 }
        );
        var slice = Assert.IsType<QuasarServerOperations.LogTailResponse>(result.Value);
        Assert.Equal(new[] { "b" }, slice.Lines);
        Assert.Equal("SpaceEngineersDedicated-1.log", slice.File);
        Assert.Equal(
            404,
            (
                await CallAsync(
                    Person(QuasarRoles.Viewer),
                    "logs.magnetar.tail",
                    new { uniqueName = "alpha" }
                )
            ).StatusCode
        );
    }

    [Fact]
    public async Task DefinitionFieldsAreSetOneAtATimeFromTheAllowList()
    {
        await CreateServerAsync("alpha", world: false);
        foreach (
            var field in new[]
            {
                "ExecutablePath",
                "WorkingDirectory",
                "LaunchArguments",
                "WorldSaveName",
                "ConfigProfileId",
                "UniqueName",
                "ManagedRuntime",
                "WorldPath",
            }
        )
            Assert.Equal(400, (await SetField(field, "x")).StatusCode);
        Assert.Equal(400, (await SetField("ServerPort", "abc")).StatusCode);
        Assert.Equal(400, (await SetField("ServerPort", 70000)).StatusCode);
        Assert.Equal(400, (await SetField("MaximumUptime", "soon")).StatusCode);
        Assert.Equal(400, (await SetField("ServerIP", "not an address")).StatusCode);

        var set = await SetField("serverPort", 27020);
        Assert.Equal(200, set.StatusCode);
        var change = Assert.IsType<QuasarConfigOperations.FieldChangeResult>(set.Value);
        Assert.Equal(27016, change.Previous.GetInt32());
        Assert.Equal("next start", change.AppliesAt);
        Assert.Equal(27020, _servers.GetServer("alpha")!.ServerPort);
        Assert.Equal(200, (await SetField("ReadyProcessPriority", "High")).StatusCode);
        Assert.Equal(
            DedicatedServerProcessPriority.High,
            _servers.GetServer("alpha")!.ReadyProcessPriority
        );

        Assert.Equal(
            QuasarUndoStatus.Done,
            (await UndoAsync((await LatestAsync("servers.definition.set", skip: 1)).Id)).Status
        );
        Assert.Equal(27016, _servers.GetServer("alpha")!.ServerPort);

        Assert.Equal(200, (await SetField("ServerPort", 27021)).StatusCode);
        var server = _servers.GetServer("alpha")!;
        server.ServerPort = 27022;
        await _servers.UpsertAsync(server);
        Assert.Equal(
            QuasarUndoStatus.Conflict,
            (await UndoAsync((await LatestAsync("servers.definition.set")).Id)).Status
        );

        var definition = Assert.IsType<Dictionary<string, object?>>(
            (
                await CallAsync(
                    Person(QuasarRoles.Viewer),
                    "servers.definition.get",
                    new { uniqueName = "alpha" }
                )
            ).Value
        );
        Assert.Equal(27022, definition["serverPort"]);
        Assert.DoesNotContain(
            definition.Keys,
            key =>
                key.Contains("path", StringComparison.OrdinalIgnoreCase)
                || key.Contains("executable", StringComparison.OrdinalIgnoreCase)
        );
    }

    [Fact]
    public async Task SettingsActOnTheSharedProfileAndRefuseBadValues()
    {
        await CreateProfileAsync();
        await CreateServerAsync("alpha", world: false, profile: "shared");
        await CreateServerAsync("beta", world: false, profile: "shared");

        var all = Assert.IsType<QuasarConfigOperations.ProfileSettings>(
            (
                await CallAsync(
                    Person(QuasarRoles.Viewer),
                    "config.settings.get",
                    new { uniqueName = "alpha" }
                )
            ).Value
        );
        Assert.Equal("shared", all.ProfileId);
        var password = all.Settings.Single(setting => setting.Option == "ServerPassword");
        Assert.Null(password.Value);
        Assert.True(password.IsSet);
        Assert.Contains(all.Settings, setting => setting.Option == "MaxPlayers");
        Assert.DoesNotContain(all.Settings, setting => setting.Option == "AutodetectDependencies");

        Assert.Equal(400, (await SetSetting("ServerPassword", "x")).StatusCode);
        Assert.Equal(404, (await SetSetting("NoSuchOption", 1)).StatusCode);
        Assert.Equal(404, (await SetSetting("AutodetectDependencies", true)).StatusCode);
        Assert.Equal(400, (await SetSetting("MaxPlayers", 0)).StatusCode);
        Assert.Equal(400, (await SetSetting("MaxPlayers", "many")).StatusCode);
        Assert.Equal(400, (await SetSetting("GameMode", "Peaceful")).StatusCode);
        Assert.Equal(400, (await SetSetting("EnableJetpack", "yes")).StatusCode);

        var set = await SetSetting("MaxPlayers", 16);
        Assert.Equal(200, set.StatusCode);
        var change = Assert.IsType<QuasarConfigOperations.FieldChangeResult>(set.Value);
        Assert.Equal(new[] { "alpha", "beta" }, change.Servers.Order());
        Assert.Equal(16, _profiles.GetProfile("shared")!.SessionSettings.MaxPlayers);
        Assert.Equal(200, (await SetSetting("GameMode", "Creative")).StatusCode);
        Assert.Equal(0, _profiles.GetProfile("shared")!.SessionSettings.GameMode);
        Assert.Equal(200, (await SetSetting("NetworkType", "eos")).StatusCode);
        Assert.Equal(
            QuasarNetworkType.EOS,
            _profiles.GetProfile("shared")!.RootSettings.NetworkType
        );

        Assert.Equal(
            QuasarUndoStatus.Done,
            (await UndoAsync((await LatestAsync("config.settings.set", skip: 2)).Id)).Status
        );
        Assert.Equal(
            new QuasarSessionSettings().MaxPlayers,
            _profiles.GetProfile("shared")!.SessionSettings.MaxPlayers
        );
        var profile = _profiles.GetProfile("shared")!;
        profile.RootSettings.NetworkType = QuasarNetworkType.Steam;
        await _profiles.UpsertAsync(profile);
        Assert.Equal(
            QuasarUndoStatus.Conflict,
            (await UndoAsync((await LatestAsync("config.settings.set")).Id)).Status
        );
    }

    [Fact]
    public async Task ModsAndPluginsChangeTheProfileWithUndo()
    {
        await CreateProfileAsync();
        Assert.Equal(
            200,
            (
                await CallAsync(
                    Editor(),
                    "config.mods.add",
                    new { profileId = "shared", workshopId = 20 }
                )
            ).StatusCode
        );
        Assert.Equal(
            200,
            (
                await CallAsync(
                    Editor(),
                    "config.mods.add",
                    new
                    {
                        profileId = "shared",
                        workshopId = 10,
                        index = 0,
                    }
                )
            ).StatusCode
        );
        Assert.Equal(
            409,
            (
                await CallAsync(
                    Editor(),
                    "config.mods.add",
                    new { profileId = "shared", workshopId = 10 }
                )
            ).StatusCode
        );
        var mods = Assert.IsType<QuasarConfigOperations.ProfileMods>(
            (await CallAsync(Editor(), "config.mods.list", new { profileId = "shared" })).Value
        );
        Assert.Equal(new[] { 10L, 20L }, mods.Mods.Select(mod => mod.WorkshopId));
        Assert.Equal(
            200,
            (
                await CallAsync(
                    Editor(),
                    "config.mods.remove",
                    new { profileId = "shared", workshopId = 20 }
                )
            ).StatusCode
        );
        Assert.Equal(
            404,
            (
                await CallAsync(
                    Editor(),
                    "config.mods.remove",
                    new { profileId = "shared", workshopId = 20 }
                )
            ).StatusCode
        );
        var undone = await UndoAsync((await LatestAsync("config.mods.remove")).Id);
        Assert.True(undone.Status == QuasarUndoStatus.Done, undone.Detail);
        Assert.Equal(
            new[] { 10L, 20L },
            _profiles.GetProfile("shared")!.Mods.Select(mod => mod.WorkshopId)
        );
        Assert.Equal(
            QuasarUndoStatus.Conflict,
            (await UndoAsync((await LatestAsync("config.mods.add", skip: 1)).Id)).Status
        );

        var profile = _profiles.GetProfile("shared")!;
        profile.Plugins.Add(
            new QuasarPluginSelection { PluginId = "listed", DisplayName = "Listed" }
        );
        await _profiles.UpsertAsync(profile);
        Assert.Equal(
            404,
            (
                await CallAsync(
                    Editor(),
                    "config.plugins.set",
                    new
                    {
                        profileId = "shared",
                        pluginId = "other",
                        selected = false,
                    }
                )
            ).StatusCode
        );
        Assert.Equal(
            200,
            (
                await CallAsync(
                    Editor(),
                    "config.plugins.set",
                    new
                    {
                        profileId = "shared",
                        pluginId = "LISTED",
                        selected = false,
                    }
                )
            ).StatusCode
        );
        Assert.Empty(_profiles.GetProfile("shared")!.Plugins);
        Assert.Equal(
            QuasarUndoStatus.Done,
            (await UndoAsync((await LatestAsync("config.plugins.set")).Id)).Status
        );
        Assert.Equal("listed", Assert.Single(_profiles.GetProfile("shared")!.Plugins).PluginId);
    }

    [Fact]
    public async Task PagesSaveProfilesAndDefinitionsThroughTheDispatcher()
    {
        await CreateProfileAsync();
        await CreateServerAsync("alpha", world: false, profile: "shared");
        var caller = new QuasarCoreOperationCaller(
            _services.GetRequiredService<IQuasarOperationDispatcher>(),
            new PersonState(Editor()),
            _services
        );
        var profile = _profiles.GetProfile("shared")!;
        profile.Name = "Renamed";
        await caller.CallAsync<QuasarConfigOperations.SaveResult>(
            "config.profile.save",
            new { profile }
        );
        Assert.Equal("Renamed", _profiles.GetProfile("shared")!.Name);
        var saved = await LatestAsync("config.profile.save");
        Assert.Equal(
            "<redacted>",
            saved
                .Arguments!.Value.GetProperty("profile")
                .GetProperty("rootSettings")
                .GetProperty("serverPassword")
                .GetString()
        );
        Assert.Equal(QuasarUndoStatus.Done, (await UndoAsync(saved.Id)).Status);
        Assert.Equal("Shared", _profiles.GetProfile("shared")!.Name);
        Assert.Equal("secret", _profiles.GetProfile("shared")!.RootSettings.ServerPassword);

        var definition = _servers.GetServer("alpha")!;
        definition.DisplayName = "Alpha Prime";
        await caller.CallAsync<QuasarConfigOperations.SaveResult>(
            "servers.definition.save",
            new { definition }
        );
        Assert.Equal("Alpha Prime", _servers.GetServer("alpha")!.DisplayName);
        Assert.Equal(
            QuasarUndoStatus.Done,
            (await UndoAsync((await LatestAsync("servers.definition.save")).Id)).Status
        );
        Assert.Equal("alpha", _servers.GetServer("alpha")!.DisplayName);

        var viewer = new QuasarCoreOperationCaller(
            _services.GetRequiredService<IQuasarOperationDispatcher>(),
            new PersonState(Person(QuasarRoles.Viewer)),
            _services
        );
        var refused = await Assert.ThrowsAsync<QuasarOperationException>(() =>
            viewer.CallAsync<QuasarConfigOperations.SaveResult>(
                "config.profile.save",
                new { profile }
            )
        );
        Assert.Equal(403, refused.StatusCode);

        var assistant = await _services
            .GetRequiredService<QuasarAgentPrincipals>()
            .CreateAssistantAsync(Person(QuasarRoles.Admin), "conversation", "save");
        Assert.Equal(
            403,
            (await CallAsync(assistant, "config.profile.save", new { profile })).StatusCode
        );
        Assert.Equal(
            403,
            (await CallAsync(assistant, "servers.definition.save", new { definition })).StatusCode
        );
    }

    [Fact]
    public async Task PlayerActionsWaitForTheAgentAndUndoTheirInverse()
    {
        await CreateServerAsync("alpha", world: false);
        Assert.Equal(409, (await Player("players.kick")).StatusCode);
        ConnectAgent("alpha");
        Assert.Equal(
            400,
            (
                await CallAsync(
                    Editor(),
                    "players.kick",
                    new { uniqueName = "alpha", steamId = "pilot" }
                )
            ).StatusCode
        );
        Assert.Equal(403, (await Player("players.ban", Person(QuasarRoles.Viewer))).StatusCode);

        Assert.Equal(200, (await Player("players.kick")).StatusCode);
        Assert.Equal(QuasarReversal.NothingToReverse, (await LatestAsync("players.kick")).Reversal);
        Assert.Equal(ServerCommandType.KickPlayer, _commands[^1].CommandType);

        Assert.Equal(200, (await Player("players.ban")).StatusCode);
        Assert.True(Known()!.IsBanned);
        Assert.Equal(
            QuasarUndoStatus.Done,
            (await UndoAsync((await LatestAsync("players.ban")).Id)).Status
        );
        Assert.Equal(ServerCommandType.UnbanPlayer, _commands[^1].CommandType);
        Assert.False(Known()!.IsBanned);

        Assert.Equal(200, (await Player("players.ban")).StatusCode);
        Assert.Equal(200, (await Player("players.unban")).StatusCode);
        Assert.Equal(
            QuasarUndoStatus.Conflict,
            (await UndoAsync((await LatestAsync("players.ban")).Id)).Status
        );

        Assert.Equal(
            400,
            (
                await CallAsync(
                    Editor(),
                    "players.promotion.set",
                    new
                    {
                        uniqueName = "alpha",
                        steamId = SteamId,
                        level = "Emperor",
                    }
                )
            ).StatusCode
        );
        Assert.Equal(
            200,
            (
                await CallAsync(
                    Editor(),
                    "players.promotion.set",
                    new
                    {
                        uniqueName = "alpha",
                        steamId = SteamId,
                        level = "moderator",
                    }
                )
            ).StatusCode
        );
        Assert.Equal("Moderator", _commands[^1].Text);
        Assert.Equal(200, (await Player("players.promote")).StatusCode);
        Assert.Equal("SpaceMaster", Known()!.PromoteLevel);
        Assert.Equal(
            QuasarUndoStatus.Done,
            (await UndoAsync((await LatestAsync("players.promote")).Id)).Status
        );
        Assert.Equal(ServerCommandType.SetPlayerPromoteLevel, _commands[^1].CommandType);
        Assert.Equal("Moderator", Known()!.PromoteLevel);

        _commandsSucceed = false;
        var failed = await Player("players.demote");
        Assert.Equal(409, failed.StatusCode);
        Assert.Equal("refused", failed.Error);
    }

    [Fact]
    public async Task BackupsQueueWorldAndServerBackupsOnly()
    {
        await CreateServerAsync("alpha", world: true);
        Assert.Equal(
            400,
            (
                await CallAsync(
                    Editor(),
                    "backups.world.create",
                    new { uniqueName = "alpha", action = "stop" }
                )
            ).StatusCode
        );
        Assert.Equal(
            404,
            (
                await CallAsync(Editor(), "backups.world.create", new { uniqueName = "missing" })
            ).StatusCode
        );
        Assert.Equal(
            409,
            (
                await CallAsync(
                    Editor(),
                    "backups.world.create",
                    new { uniqueName = "alpha", action = "save" }
                )
            ).StatusCode
        );
        Assert.Equal(
            403,
            (
                await CallAsync(
                    Person(QuasarRoles.Viewer),
                    "backups.world.create",
                    new { uniqueName = "alpha" }
                )
            ).StatusCode
        );
        var queued = await CallAsync(
            Editor(),
            "backups.world.create",
            new { uniqueName = "alpha" }
        );
        Assert.Equal(200, queued.StatusCode);
        Assert.NotEqual(
            Guid.Empty,
            Assert.IsType<QuasarBackupOperations.BackupQueued>(queued.Value).JobId
        );
        Assert.Equal("alpha", (await LatestAsync("backups.world.create")).Server);
        Assert.Equal(
            200,
            (
                await CallAsync(Editor(), "backups.server.create", new { uniqueName = "alpha" })
            ).StatusCode
        );

        // A configuration backup holds secrets and is never listed.
        Directory.CreateDirectory(Path.Combine(_root, "backups"));
        await File.WriteAllBytesAsync(Path.Combine(_root, "backups", "config.zip"), []);
        Assert.Empty(
            Assert.IsAssignableFrom<IReadOnlyList<QuasarBackupOperations.BackupItem>>(
                (await CallAsync(Editor(), "backups.list", new { })).Value
            )
        );
    }

    [Fact]
    public async Task BackupRestoreTakesStoredWorldBackupsOnly()
    {
        await CreateServerAsync("alpha", world: true);
        var name = Path.GetFileName(
            await _services
                .GetRequiredService<QuasarBackupService>()
                .WriteWorldBackupFileAsync("alpha", DateTimeOffset.UtcNow, automatic: false)
        );
        var save = Path.Combine(_servers.GetServer("alpha")!.GetWorldSavePath(), "Sandbox.sbc");
        await File.WriteAllTextAsync(save, "changed");
        Directory.CreateDirectory(Path.Combine(_root, "backups"));
        await File.WriteAllBytesAsync(Path.Combine(_root, "backups", "config.zip"), []);

        Assert.Equal(403, (await CallAsync(Editor(), "backups.restore", new { name })).StatusCode);
        var admin = Person(QuasarRoles.Admin);
        Assert.Equal(
            404,
            (await CallAsync(admin, "backups.restore", new { name = "config.zip" })).StatusCode
        );
        var restored = await CallAsync(admin, "backups.restore", new { name });
        Assert.Equal(200, restored.StatusCode);
        var result = Assert.IsType<QuasarBackupOperations.BackupRestored>(restored.Value);
        Assert.Equal("World", result.Kind);
        Assert.Equal("alpha", result.Server);
        Assert.Equal("save", await File.ReadAllTextAsync(save));
        Assert.Equal("alpha", (await LatestAsync("backups.restore")).Server);

        Assert.Equal(403, (await CallAsync(Editor(), "backups.settings.read", new { })).StatusCode);
        var settings = Assert.IsType<QuasarBackupOperations.BackupSettings>(
            (await CallAsync(admin, "backups.settings.read", new { })).Value
        );
        Assert.Equal("Daily", settings.World.Frequency);
        Assert.Equal("03:00", settings.World.TimeOfDay);
    }

    // Every operation this change added, and what a Viewer may call.
    private static readonly string[] AddedOperations =
    [
        "cluster.players",
        "chat.messages",
        "chat.commands",
        "chat.send",
        "chat.command.run",
        "entities.list",
        "entities.delete",
        "cluster.entities.list",
        "cluster.entities.delete",
        "cluster.start",
        "cluster.stop",
        "cluster.restart",
        "cluster.save",
        "cluster.gateway.restart",
        "cluster.services.recover",
        "cluster.recover",
        "cluster.operation",
        "cluster.players.kick",
        "cluster.players.ban",
        "cluster.players.unban",
        "cluster.players.kick-cooldown.clear",
        "cluster.players.promotion.set",
        "cluster.logs.tail",
        "cluster.events.tail",
        "activity.recent",
        "audit.list",
        "backups.settings.read",
        "backups.restore",
    ];

    private static readonly string[] ViewerReads =
    [
        "cluster.players",
        "chat.messages",
        "chat.commands",
        "entities.list",
        "cluster.entities.list",
        "cluster.operation",
        "cluster.logs.tail",
        "cluster.events.tail",
        "activity.recent",
    ];

    private static readonly string[] SecurityLevel =
    [
        "audit.list",
        "backups.settings.read",
        "backups.restore",
    ];

    [Fact]
    public async Task AddedOperationsRefuseCallersTheWebUiRefuses()
    {
        Assert.Equal(
            AddedOperations.Order(),
            _services
                .GetServices<QuasarOperation>()
                .Select(operation => operation.OperationId)
                .Intersect(AddedOperations)
                .Order()
        );
        var principals = _services.GetRequiredService<QuasarAgentPrincipals>();
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity());
        var service = new ClaimsPrincipal(
            new ClaimsIdentity(
                [
                    new Claim(QuasarClaimTypes.Provider, QuasarAuthSchemes.ServicePrincipal),
                    new Claim(ClaimTypes.Role, QuasarRoles.Admin),
                    new Claim(QuasarClaimTypes.Scope, QuasarScopes.ClusterManage),
                ],
                QuasarAuthSchemes.ServicePrincipal
            )
        );
        var player = principals.CreatePlayer("76561198000000001", "Pilot", null, "c", "!ask");
        var assistant = await principals.CreateAssistantAsync(Person(QuasarRoles.Admin), "c", "r");
        var monitor = principals.CreateMonitor("c", "r");
        var viewer = Person(QuasarRoles.Viewer);
        foreach (var operation in AddedOperations)
        {
            Assert.Equal(401, (await CallAsync(anonymous, operation, new { })).StatusCode);
            Assert.Equal(403, (await CallAsync(service, operation, new { })).StatusCode);
            // The player agent holds no role: it reaches only the my.* operations.
            Assert.Equal(403, (await CallAsync(player, operation, new { })).StatusCode);
            var asViewer = (await CallAsync(viewer, operation, new { })).StatusCode;
            if (ViewerReads.Contains(operation))
                Assert.NotEqual(403, asViewer);
            else
                Assert.Equal(403, asViewer);
            // Security-level operations are on the hard deny list for every agent, whoever asks.
            if (SecurityLevel.Contains(operation))
            {
                Assert.Equal(403, (await CallAsync(assistant, operation, new { })).StatusCode);
                Assert.Equal(403, (await CallAsync(monitor, operation, new { })).StatusCode);
            }
        }

        // An assistant does no more than its asker could, and a proposal no more than its approver could.
        await using (var scope = _services.CreateAsyncScope())
            await scope
                .ServiceProvider.GetRequiredService<QuasarOperationService>()
                .SavePolicyRolesAsync(
                    Person(QuasarRoles.Admin),
                    QuasarPolicyNames.CanUseAssistant,
                    [QuasarRoles.Viewer, QuasarRoles.Editor, QuasarRoles.Admin]
                );
        var viewerAssistant = await principals.CreateAssistantAsync(viewer, "c", "r");
        var askerRefused = await CallAsync(viewerAssistant, "chat.send", new { });
        Assert.Equal(403, askerRefused.StatusCode);
        Assert.Equal(
            "The person asking the assistant cannot access this SDK operation.",
            askerRefused.Error
        );
        foreach (var change in AddedOperations.Except(ViewerReads).Except(SecurityLevel))
        {
            await using var scope = _services.CreateAsyncScope();
            var refused = await scope
                .ServiceProvider.GetRequiredService<QuasarOperationService>()
                .DispatchAsync(
                    new(assistant, scope.ServiceProvider) { ApprovedBy = viewer },
                    Core,
                    change,
                    JsonSerializer.SerializeToElement(new { }, QuasarCoreOperations.Json),
                    default
                );
            Assert.Equal(403, refused.StatusCode);
            Assert.Equal("The approver cannot access this SDK operation.", refused.Error);
        }

        // A role override that narrows CanView also hides the cluster reads of the CanView pages.
        await using (var scope = _services.CreateAsyncScope())
            await scope
                .ServiceProvider.GetRequiredService<QuasarOperationService>()
                .SavePolicyRolesAsync(
                    Person(QuasarRoles.Admin),
                    QuasarPolicyNames.CanView,
                    [QuasarRoles.Editor, QuasarRoles.Admin]
                );
        foreach (var read in new[] { "cluster.players", "cluster.entities.list", "chat.messages" })
            Assert.Equal(403, (await CallAsync(viewer, read, new { })).StatusCode);
    }

    [Fact]
    public async Task ClusterReadsRefuseUnknownClusters()
    {
        Assert.Empty(
            Assert.IsAssignableFrom<IReadOnlyList<QuasarClusterOperations.ClusterItem>>(
                (await CallAsync(Editor(), "cluster.list", new { })).Value
            )
        );
        Assert.Equal(
            404,
            (await CallAsync(Editor(), "cluster.status", new { uniqueName = "nope" })).StatusCode
        );
    }

    [Fact]
    public async Task ClusterPlayersListTheGatewaysClientsAndServerReadsPointThere()
    {
        Assert.Equal(
            404,
            (await CallAsync(Editor(), "cluster.players", new { uniqueName = "nope" })).StatusCode
        );
        var cluster = await _clusters.CreateAsync(
            new("test", "Unified cluster", "http://gateway.test", ""),
            default
        );
        var early = Assert.IsType<QuasarClusterOperations.ClusterPlayersResult>(
            (await CallAsync(Editor(), "cluster.players", new { uniqueName = "test" })).Value
        );
        Assert.Contains("not collected yet", early.Error);
        Assert.Empty(early.Online);

        await _clusterPlayers.RefreshAsync(cluster, default);
        var players = Assert.IsType<QuasarClusterOperations.ClusterPlayersResult>(
            (await CallAsync(Editor(), "cluster.players", new { uniqueName = "test" })).Value
        );
        Assert.Null(players.Error);
        var player = Assert.Single(players.Online);
        Assert.Equal(("7", "Cluster player", "node-a"), (player.SteamId, player.Name, player.Node));

        var list = await CallAsync(Editor(), "players.list", new { uniqueName = "test" });
        Assert.Equal(404, list.StatusCode);
        Assert.Contains("cluster.players", list.Error);
        var tail = await CallAsync(Editor(), "logs.magnetar.tail", new { uniqueName = "test" });
        Assert.Equal(404, tail.StatusCode);
        Assert.Contains("is a cluster", tail.Error);
        Assert.Equal(
            "Server 'missing' not found.",
            (await CallAsync(Editor(), "players.list", new { uniqueName = "missing" })).Error
        );
    }

    [Fact]
    public async Task EntitiesListFiltersSortsAndCleansAStandaloneServersEntities()
    {
        Assert.Equal(
            "Server 'alpha' not found.",
            (await CallAsync(Editor(), "entities.list", new { uniqueName = "alpha" })).Error
        );
        await CreateServerAsync("alpha", world: false);
        Assert.Equal(
            409,
            (await CallAsync(Editor(), "entities.list", new { uniqueName = "alpha" })).StatusCode
        );
        ConnectAgent("alpha");
        _alphaEntities.AddRange([
            new()
            {
                EntityId = 1,
                DisplayName = "\uE030Big station",
                TypeTag = "Grid",
                SubType = "LargeStatic",
                BlockCount = 900,
                Pcu = 100,
                SizeMeters = 300,
                OwnerSteamId = 42,
                OwnerName = "Owner",
                PositionX = 1.4,
                PositionY = -2,
                PositionZ = 3,
            },
            new()
            {
                EntityId = 2,
                DisplayName = "Busy ship",
                TypeTag = "Grid",
                SubType = "SmallShip",
                BlockCount = 50,
                Pcu = 5000,
                SizeMeters = 20,
                BigOwnerSteamIds = [42, 43],
            },
            new()
            {
                EntityId = 3,
                DisplayName = "Plates",
                TypeTag = "Float",
                ItemAmount = 2.5m,
                SizeMeters = 1,
            },
        ]);

        var all = Assert.IsType<QuasarEntityOperations.EntityList>(
            (
                await CallAsync(
                    Person(QuasarRoles.Viewer),
                    "entities.list",
                    new { uniqueName = "alpha" }
                )
            ).Value
        );
        Assert.Equal((3, 3), (all.Matching, all.LiveTotal!.Value));
        Assert.Equal(["1", "2", "3"], all.Entities.Select(entity => entity.EntityId));
        var station = all.Entities[0];
        Assert.Equal(
            ("Grid", "Large-grid station", "Big station", "Owner", "42", "1, -2, 3"),
            (
                station.Type,
                station.Details,
                station.Name,
                station.OwnerName,
                station.OwnerSteamId,
                station.Position
            )
        );
        Assert.Null(station.Nodes);
        Assert.Equal(2.5m, all.Entities[2].Quantity);

        var busy = Assert.IsType<QuasarEntityOperations.EntityList>(
            (
                await CallAsync(
                    Editor(),
                    "entities.list",
                    new
                    {
                        uniqueName = "alpha",
                        type = "grid",
                        search = "s",
                        ownerSteamId = "42",
                        sort = "pcu",
                        limit = 1,
                    }
                )
            ).Value
        );
        Assert.Equal("2", Assert.Single(busy.Entities).EntityId);
        var filter = JsonSerializer.Deserialize<EntityListFilter>(
            _commands[^1].Payload!,
            QuasarCoreOperations.Json
        )!;
        Assert.Equal(("Grid", "s"), (filter.TypeTag, filter.Search));
        Assert.Equal(
            400,
            (
                await CallAsync(
                    Editor(),
                    "entities.list",
                    new { uniqueName = "alpha", type = "Ships" }
                )
            ).StatusCode
        );
    }

    [Fact]
    public async Task EntitiesDeleteLooksTheEntityUpAgainAndIsAuditedWithoutUndo()
    {
        await CreateServerAsync("alpha", world: false);
        ConnectAgent("alpha");
        _alphaEntities.Add(
            new()
            {
                EntityId = 7,
                DisplayName = "Wreck",
                TypeTag = "Grid",
            }
        );
        Assert.Equal(
            403,
            (
                await CallAsync(
                    Person(QuasarRoles.Viewer),
                    "entities.delete",
                    new { uniqueName = "alpha", entityId = "7" }
                )
            ).StatusCode
        );
        Assert.Equal(
            400,
            (
                await CallAsync(
                    Editor(),
                    "entities.delete",
                    new { uniqueName = "alpha", entityId = "wreck" }
                )
            ).StatusCode
        );
        Assert.Equal(
            404,
            (
                await CallAsync(
                    Editor(),
                    "entities.delete",
                    new { uniqueName = "alpha", entityId = "8" }
                )
            ).StatusCode
        );
        Assert.DoesNotContain(
            _commands,
            command => command.CommandType == ServerCommandType.DeleteEntity
        );

        var deleted = await CallAsync(
            Editor(),
            "entities.delete",
            new { uniqueName = "alpha", entityId = "7" }
        );
        Assert.Equal(200, deleted.StatusCode);
        Assert.Equal(
            "Wreck",
            Assert.IsType<QuasarEntityOperations.EntityDeleteResult>(deleted.Value).Entity.Name
        );
        var command = _commands[^1];
        Assert.Equal(ServerCommandType.DeleteEntity, command.CommandType);
        Assert.Equal(
            7,
            JsonSerializer
                .Deserialize<EntityDeleteRequest>(command.Payload!, QuasarCoreOperations.Json)!
                .EntityId
        );
        var entry = await LatestAsync("entities.delete");
        Assert.Equal(QuasarReversal.NotReversible, entry.Reversal);
        Assert.Equal("alpha", entry.Server);
    }

    [Fact]
    public async Task ClusterEntitiesListNodesAndDeleteOnlyThroughTheOwningNode()
    {
        Assert.Equal(
            404,
            (
                await CallAsync(Editor(), "cluster.entities.list", new { uniqueName = "cluster" })
            ).StatusCode
        );
        await _clusters.CreateAsync(
            new("cluster", "Unified cluster", "http://gateway.test", ""),
            default
        );
        Entities.Data["a"] =
        [
            ClusterEntityTests.Entity(1, "Local"),
            ClusterEntityTests.Entity(3, "Local"),
        ];
        Entities.Data["b"] =
        [
            ClusterEntityTests.Entity(1, "Unowned"),
            ClusterEntityTests.Entity(3, "Local"),
        ];

        var list = Assert.IsType<QuasarEntityOperations.EntityList>(
            (
                await CallAsync(
                    Person(QuasarRoles.Viewer),
                    "cluster.entities.list",
                    new { uniqueName = "cluster" }
                )
            ).Value
        );
        Assert.Contains("conflicting node ownership", Assert.Single(list.Warnings));
        Assert.Null(list.LiveTotal);
        var shared = Assert.Single(list.Entities, entity => entity.EntityId == "1");
        Assert.Equal(("a, b", null), (shared.Nodes, shared.DeleteBlocked));
        Assert.NotNull(
            Assert.Single(list.Entities, entity => entity.EntityId == "3").DeleteBlocked
        );
        var serverList = await CallAsync(Editor(), "entities.list", new { uniqueName = "cluster" });
        Assert.Equal(404, serverList.StatusCode);
        Assert.Contains("cluster.entities.list", serverList.Error);

        Assert.Equal(
            403,
            (
                await CallAsync(
                    Person(QuasarRoles.Viewer),
                    "cluster.entities.delete",
                    new { uniqueName = "cluster", entityId = "1" }
                )
            ).StatusCode
        );
        Assert.Equal(
            409,
            (
                await CallAsync(
                    Editor(),
                    "cluster.entities.delete",
                    new { uniqueName = "cluster", entityId = "3" }
                )
            ).StatusCode
        );
        Assert.Equal(
            404,
            (
                await CallAsync(
                    Editor(),
                    "cluster.entities.delete",
                    new { uniqueName = "cluster", entityId = "9" }
                )
            ).StatusCode
        );
        Assert.DoesNotContain(
            Entities.Commands,
            command => command.CommandType == ServerCommandType.DeleteEntity
        );
        Assert.Equal(
            200,
            (
                await CallAsync(
                    Editor(),
                    "cluster.entities.delete",
                    new { uniqueName = "cluster", entityId = "1" }
                )
            ).StatusCode
        );
        var deletion = Assert.Single(
            Entities.Commands,
            command => command.CommandType == ServerCommandType.DeleteEntity
        );
        Assert.Equal("a", deletion.AgentId);
    }

    private ClusterEntityTests.Fixture Entities => _entityFixture ??= new(_registry);

    [Fact]
    public async Task ChatReadsTheAgentsChatAndSendsMessagesAndCommands()
    {
        Assert.Equal(
            404,
            (await CallAsync(Editor(), "chat.messages", new { uniqueName = "missing" })).StatusCode
        );
        await CreateServerAsync("alpha", world: false);
        var offline = Assert.IsType<QuasarChatOperations.ChatResult>(
            (await CallAsync(Editor(), "chat.messages", new { uniqueName = "alpha" })).Value
        );
        Assert.Contains("not connected", offline.Error);
        Assert.Equal(409, (await Chat("chat.send", "hello")).StatusCode);

        ConnectAgent("alpha");
        var tick = new DateTimeOffset(2026, 11, 7, 12, 0, 0, TimeSpan.Zero).UtcTicks;
        _registry.UpdateSnapshot(
            new AgentSnapshot
            {
                AgentId = "agent",
                UniqueName = "alpha",
                ServerId = "alpha",
                TelemetryPaused = true,
                RecentChat =
                [
                    new()
                    {
                        SteamId = 42,
                        AuthorName = "\uE030Pilot",
                        Content = "one\r\ntwo",
                        TimestampTicksUtc = tick + 1,
                        Channel = ChatMessageChannel.Faction,
                        FactionTag = "ABC",
                    },
                    new()
                    {
                        AuthorName = "Good.bot",
                        Content = "older",
                        TimestampTicksUtc = tick,
                        Channel = ChatMessageChannel.Global,
                    },
                ],
                ChatCommands =
                [
                    new()
                    {
                        Text = "!ess help",
                        Syntax = "!ess help",
                        Description = "Help",
                    },
                ],
            },
            "connection"
        );
        var chat = Assert.IsType<QuasarChatOperations.ChatResult>(
            (
                await CallAsync(
                    Person(QuasarRoles.Viewer),
                    "chat.messages",
                    new { uniqueName = "alpha" }
                )
            ).Value
        );
        Assert.Null(chat.Error);
        Assert.Equal(
            new[]
            {
                ("Server", (string?)null, "Global", "older"),
                ("Pilot", "42", "Faction ABC", "one\ntwo"),
            },
            chat.Messages.Select(line => (line.Author, line.SteamId, line.Channel, line.Text))
        );
        Assert.Equal(new DateTimeOffset(tick, TimeSpan.Zero), chat.Messages[0].Time);
        var last = Assert.IsType<QuasarChatOperations.ChatResult>(
            (
                await CallAsync(Editor(), "chat.messages", new { uniqueName = "alpha", limit = 1 })
            ).Value
        );
        Assert.Equal("Pilot", Assert.Single(last.Messages).Author);
        var commands = Assert.IsType<QuasarChatOperations.ChatCommandList>(
            (await CallAsync(Editor(), "chat.commands", new { uniqueName = "alpha" })).Value
        );
        Assert.Equal("!ess help", Assert.Single(commands.Commands).Command);

        Assert.Equal(
            403,
            (await Chat("chat.send", "hello", Person(QuasarRoles.Viewer))).StatusCode
        );
        Assert.Equal(400, (await Chat("chat.send", "!ess help")).StatusCode);
        Assert.Equal(400, (await Chat("chat.command.run", "ess help")).StatusCode);
        Assert.Equal(200, (await Chat("chat.send", " hello ")).StatusCode);
        Assert.Equal(200, (await Chat("chat.command.run", "!ess help")).StatusCode);
        Assert.Equal(
            new[]
            {
                (ServerCommandType.SendChat, "hello"),
                (ServerCommandType.RunChatCommand, "!ess help"),
            },
            _commands.Select(command => (command.CommandType, command.Text))
        );
        Assert.Equal("alpha", (await LatestAsync("chat.send")).Server);
        Assert.Equal(
            QuasarReversal.NotReversible,
            (await LatestAsync("chat.command.run")).Reversal
        );
        _commandsSucceed = false;
        var refused = await Chat("chat.command.run", "!nope");
        Assert.Equal(409, refused.StatusCode);
        Assert.Equal("refused", refused.Error);
    }

    [Fact]
    public async Task ClusterChatComesFromTheGatewayAndCommandsStayStandalone()
    {
        var cluster = await _clusters.CreateAsync(
            new("test", "Unified cluster", "http://gateway.test", ""),
            default
        );
        await _clusterPlayers.RefreshAsync(cluster, default);
        var at = DateTimeOffset.UtcNow;
        _gateway.Chat =
        [
            new(1, at, 7, 0, "first"),
            new(2, at, 7, 0, "second"),
            new(3, at, 99, 0, "third"),
        ];
        var chat = Assert.IsType<QuasarChatOperations.ChatResult>(
            (
                await CallAsync(Editor(), "chat.messages", new { uniqueName = "test", limit = 2 })
            ).Value
        );
        Assert.True(chat.IsCluster);
        Assert.Equal(
            new[] { ("Cluster player", "second"), ("99", "third") },
            chat.Messages.Select(line => (line.Author, line.Text))
        );
        var commands = await CallAsync(Editor(), "chat.commands", new { uniqueName = "test" });
        Assert.Equal(404, commands.StatusCode);
        Assert.Contains("chat.send", commands.Error);
        Assert.Equal(404, (await Chat("chat.command.run", "!help", uniqueName: "test")).StatusCode);
        // No Discord cluster bridge in this host.
        Assert.Equal(409, (await Chat("chat.send", "hi", uniqueName: "test")).StatusCode);
        _gateway.Offline = true;
        Assert.NotNull(
            Assert
                .IsType<QuasarChatOperations.ChatResult>(
                    (await CallAsync(Editor(), "chat.messages", new { uniqueName = "test" })).Value
                )
                .Error
        );
    }

    [Fact]
    public async Task ActivityListsCommandResultsAndFiltersByServer()
    {
        await CreateServerAsync("alpha", world: false);
        await CreateServerAsync("beta", world: false);
        ConnectAgent("alpha");
        Assert.Equal(200, (await Chat("chat.send", "hello")).StatusCode);
        var all = Assert.IsAssignableFrom<IReadOnlyList<QuasarActivityOperations.ActivityItem>>(
            (await CallAsync(Person(QuasarRoles.Viewer), "activity.recent", new { })).Value
        );
        var item = Assert.Single(all);
        Assert.Equal(("alpha", "done", "info"), (item.UniqueName, item.Message, item.Level));
        Assert.Empty(
            Assert.IsAssignableFrom<IReadOnlyList<QuasarActivityOperations.ActivityItem>>(
                (await CallAsync(Editor(), "activity.recent", new { uniqueName = "beta" })).Value
            )
        );
        Assert.Equal(
            404,
            (
                await CallAsync(Editor(), "activity.recent", new { uniqueName = "missing" })
            ).StatusCode
        );
    }

    [Fact]
    public async Task AuditListNeedsSecurityAndPagesFilteredEntries()
    {
        await CreateServerAsync("alpha", world: false);
        ConnectAgent("alpha");
        Assert.Equal(200, (await Chat("chat.send", "one")).StatusCode);
        Assert.Equal(200, (await Chat("chat.send", "two")).StatusCode);
        Assert.Equal(403, (await CallAsync(Editor(), "audit.list", new { })).StatusCode);
        Assert.Equal(
            400,
            (
                await CallAsync(Person(QuasarRoles.Admin), "audit.list", new { result = "maybe" })
            ).StatusCode
        );
        var page = Assert.IsType<QuasarActivityOperations.AuditPage>(
            (
                await CallAsync(
                    Person(QuasarRoles.Admin),
                    "audit.list",
                    new
                    {
                        operation = "chat.send",
                        server = "alpha",
                        result = "ok",
                        take = 1,
                    }
                )
            ).Value
        );
        Assert.True(page.More);
        var entry = Assert.Single(page.Entries);
        Assert.Equal(
            (Core + "/chat.send", "alpha", "Ok"),
            (entry.Operation, entry.Server, entry.Result)
        );
        Assert.Contains("two", entry.Arguments);
        Assert.StartsWith("Person: ", entry.Principal);
        var next = Assert.IsType<QuasarActivityOperations.AuditPage>(
            (
                await CallAsync(
                    Person(QuasarRoles.Admin),
                    "audit.list",
                    new { operation = "chat.send", skip = 1 }
                )
            ).Value
        );
        Assert.False(next.More);
        Assert.Contains("one", Assert.Single(next.Entries).Arguments);
    }

    private Task<QuasarOperationResult> Chat(
        string operation,
        string text,
        ClaimsPrincipal? user = null,
        string uniqueName = "alpha"
    ) => CallAsync(user ?? Editor(), operation, new { uniqueName, text });

    [Fact]
    public async Task ClusterLifecycleRunsThroughTheGatewayOrTheGoal()
    {
        Assert.Equal(
            404,
            (await CallAsync(Editor(), "cluster.stop", new { uniqueName = "nope" })).StatusCode
        );
        await _clusters.CreateAsync(new("test", "Cluster", "http://gateway.test", ""), default);
        Assert.Equal(
            403,
            (
                await CallAsync(
                    Person(QuasarRoles.Viewer),
                    "cluster.save",
                    new { uniqueName = "test" }
                )
            ).StatusCode
        );
        // An observed cluster has no goal to set; it stops through a Gateway shutdown.
        var start = await CallAsync(Editor(), "cluster.start", new { uniqueName = "test" });
        Assert.Equal(400, start.StatusCode);
        Assert.Contains("observed only", start.Error);
        Assert.Equal(
            409,
            (await CallAsync(Editor(), "cluster.save", new { uniqueName = "test" })).StatusCode
        );
        await _clusters.SetGoalStateAsync("test", DedicatedServerGoalState.On);

        var save = await CallAsync(Editor(), "cluster.save", new { uniqueName = "test" });
        var saved = Assert.IsType<QuasarClusterControlOperations.ClusterActionResult>(save.Value);
        Assert.Equal("Succeeded", saved.State);
        Assert.Equal("POST /admin/v1/save-all", _gateway.Mutations[^1]);
        var followed = Assert.IsType<QuasarClusterControlOperations.ClusterOperationStatus>(
            (
                await CallAsync(
                    Person(QuasarRoles.Viewer),
                    "cluster.operation",
                    new { uniqueName = "test", operationId = saved.OperationId }
                )
            ).Value
        );
        Assert.Equal(("cluster.save-all", "Succeeded"), (followed.Kind, followed.State));
        Assert.Equal("test", (await LatestAsync("cluster.save")).Server);

        Assert.Equal(
            200,
            (await CallAsync(Editor(), "cluster.stop", new { uniqueName = "test" })).StatusCode
        );
        Assert.Equal("POST /admin/v1/shutdown", _gateway.Mutations[^1]);
        Assert.Equal(QuasarReversal.NotReversible, (await LatestAsync("cluster.stop")).Reversal);

        _gateway.CurrentCapabilities = ClusterPlayerTests.Gateway.Capabilities with
        {
            Operations = [],
        };
        var refused = await CallAsync(Editor(), "cluster.save", new { uniqueName = "test" });
        Assert.Equal(409, refused.StatusCode);
        Assert.Contains("does not allow a world save", refused.Error);
        Assert.Equal(
            409,
            (await CallAsync(Editor(), "cluster.recover", new { uniqueName = "test" })).StatusCode
        );
    }

    [Fact]
    public async Task ManagedClusterStopSetsTheGoalAndUndoesWhileItIsUnchanged()
    {
        await _clusters.CreateAsync(new("test", "Cluster", "http://gateway.test", ""), default);
        await _clusters.SetGoalStateAsync("test", DedicatedServerGoalState.On);
        var stored = (
            (List<ClusterDefinition>)
                typeof(ClusterCatalog)
                    .GetField("_clusters", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .GetValue(_clusters)!
        ).Single();
        stored.HostCommandUrl = "http://host.test";
        stored.HostCommandTokenEnvironmentVariable = "HOST_TOKEN";
        stored.Gateway = new(
            "test",
            Quasar.Host.Contract.V1.GatewayGoal.Off,
            "/bundle/manifest.json",
            new string('a', 64),
            "r1",
            [28000],
            "/runs/test"
        );

        Assert.Equal(
            200,
            (await CallAsync(Editor(), "cluster.stop", new { uniqueName = "test" })).StatusCode
        );
        Assert.Equal(DedicatedServerGoalState.Off, _clusters.GetCluster("test")!.GoalState);
        Assert.Empty(_gateway.Mutations);
        var stop = await LatestAsync("cluster.stop");
        Assert.Equal(QuasarReversal.Undo, stop.Reversal);
        Assert.Equal(
            409,
            (await CallAsync(Editor(), "cluster.stop", new { uniqueName = "test" })).StatusCode
        );

        // No Host proves a clean stop, so a start is refused like the page's disabled Start button.
        var start = await CallAsync(Editor(), "cluster.start", new { uniqueName = "test" });
        Assert.Equal(409, start.StatusCode);
        Assert.Contains("stable stopped state", start.Error);

        await _clusters.SetGoalStateAsync("test", DedicatedServerGoalState.On);
        var conflict = await UndoAsync(stop.Id);
        Assert.Equal(QuasarUndoStatus.Conflict, conflict.Status);
        Assert.Contains("On", conflict.Detail);
    }

    [Fact]
    public async Task ClusterModerationSendsThePlayersPageCommandsAndUndoesThem()
    {
        var cluster = await _clusters.CreateAsync(
            new("test", "Cluster", "http://gateway.test", ""),
            default
        );
        var early = await CallAsync(
            Editor(),
            "cluster.players.ban",
            new { uniqueName = "test", steamId = "7" }
        );
        Assert.Equal(409, early.StatusCode);
        Assert.Contains("Waiting for cluster player status", early.Error);
        await _clusterPlayers.RefreshAsync(cluster, default);
        Assert.Equal(
            403,
            (
                await CallAsync(
                    Person(QuasarRoles.Viewer),
                    "cluster.players.kick",
                    new { uniqueName = "test", steamId = "7" }
                )
            ).StatusCode
        );
        Assert.Equal(
            400,
            (
                await CallAsync(
                    Editor(),
                    "cluster.players.kick",
                    new { uniqueName = "test", steamId = "x" }
                )
            ).StatusCode
        );

        Assert.Equal(
            200,
            (
                await CallAsync(
                    Editor(),
                    "cluster.players.kick",
                    new { uniqueName = "test", steamId = "7" }
                )
            ).StatusCode
        );
        Assert.Equal("POST /admin/v1/clients/7/kick", _gateway.Mutations[^1]);
        Assert.Equal(
            409,
            (
                await CallAsync(
                    Editor(),
                    "cluster.players.kick",
                    new { uniqueName = "test", steamId = "8" }
                )
            ).StatusCode
        );
        Assert.Contains(
            "no kick cooldown",
            (
                await CallAsync(
                    Editor(),
                    "cluster.players.kick-cooldown.clear",
                    new { uniqueName = "test", steamId = "7" }
                )
            ).Error
        );

        Assert.Equal(
            200,
            (
                await CallAsync(
                    Editor(),
                    "cluster.players.ban",
                    new { uniqueName = "test", steamId = "7" }
                )
            ).StatusCode
        );
        Assert.Equal("POST /admin/v1/admission/bans", _gateway.Mutations[^1]);
        var ban = await LatestAsync("cluster.players.ban");
        Assert.Equal(QuasarReversal.Undo, ban.Reversal);
        Assert.Equal(QuasarUndoStatus.Conflict, (await UndoAsync(ban.Id)).Status);
        _gateway.Banned = [7];
        Assert.Equal(QuasarUndoStatus.Done, (await UndoAsync(ban.Id)).Status);
        Assert.Equal("DELETE /admin/v1/admission/bans/7", _gateway.Mutations[^1]);

        Assert.Equal(
            400,
            (
                await CallAsync(
                    Editor(),
                    "cluster.players.promotion.set",
                    new
                    {
                        uniqueName = "test",
                        steamId = "7",
                        level = "Captain",
                    }
                )
            ).StatusCode
        );
        Assert.Equal(
            200,
            (
                await CallAsync(
                    Editor(),
                    "cluster.players.promotion.set",
                    new
                    {
                        uniqueName = "test",
                        steamId = "7",
                        level = "admin",
                    }
                )
            ).StatusCode
        );
        Assert.Equal("PUT /admin/v1/players/7/promotion", _gateway.Mutations[^1]);
        Assert.Contains("Admin", _gateway.MutationBody);
        _gateway.Moderation = new([new(7, Admin.PlayerPromotionLevel.Admin, 3)], []);
        Assert.Equal(
            QuasarUndoStatus.Done,
            (await UndoAsync((await LatestAsync("cluster.players.promotion.set")).Id)).Status
        );
        Assert.Contains("None", _gateway.MutationBody);
        Assert.Contains("3", _gateway.MutationBody);

        var standalone = await CallAsync(
            Editor(),
            "players.kick",
            new { uniqueName = "test", steamId = "7" }
        );
        Assert.Equal(404, standalone.StatusCode);
        Assert.Contains("cluster.players.kick", standalone.Error);
    }

    [Fact]
    public async Task ClusterLogTailsReadNodePluginLogsAndRegistryEvents()
    {
        Assert.Equal(
            404,
            (await CallAsync(Editor(), "cluster.logs.tail", new { uniqueName = "nope" })).StatusCode
        );
        await _clusters.CreateAsync(new("test", "Cluster", "http://gateway.test", ""), default);
        Admin.NodeStatus Node(string name) =>
            new(
                name,
                name,
                2,
                Admin.NodeRole.Regular,
                Admin.NodeState.Active,
                "127.0.0.1:28417",
                null,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow.AddMinutes(1),
                0,
                0,
                "host",
                true,
                false
            );
        _gateway.Nodes = [Node("node-a"), Node("node-b")];
        foreach (var node in new[] { "node-a", "node-b" })
            _registry.UpsertHello(
                new AgentHello
                {
                    AgentId = node,
                    UniqueName = node,
                    HostId = "host",
                    ClusterMode = true,
                    ClusterId = "test",
                    ClusterSlot = node,
                    ClusterNodeId = node,
                    ClusterEpoch = 2,
                },
                node,
                (_, _) => Task.CompletedTask
            );
        var agents = _registry.GetAgents().ToDictionary(agent => agent.AgentId);
        for (var i = 1; i <= 3; i++)
            foreach (var node in new[] { "node-a", "node-b" })
                _logs.Append(
                    new PluginLogEntry
                    {
                        UniqueName = agents[node].TelemetryKey,
                        TimestampUtc = DateTimeOffset.UnixEpoch.AddMinutes(i),
                        Level = "Info",
                        Plugin = "Probe",
                        Message = $"{node} entry {i}",
                    }
                );

        var all = Assert.IsType<QuasarServerOperations.LogTailResponse>(
            (
                await CallAsync(
                    Person(QuasarRoles.Viewer),
                    "cluster.logs.tail",
                    new { uniqueName = "test", lines = 3 }
                )
            ).Value
        );
        Assert.Equal(3, all.Lines.Count);
        Assert.EndsWith("entry 3", all.Lines[^1]);
        Assert.True(all.Truncated);
        var one = Assert.IsType<QuasarServerOperations.LogTailResponse>(
            (
                await CallAsync(
                    Editor(),
                    "cluster.logs.tail",
                    new
                    {
                        uniqueName = "test",
                        node = "NODE-B",
                        filter = "entry 2",
                    }
                )
            ).Value
        );
        Assert.Equal(
            "1970-01-01 00:02:00Z · node-b · Probe · Info · node-b entry 2",
            Assert.Single(one.Lines)
        );
        Assert.Equal(1, one.TotalLines);
        Assert.Equal(
            404,
            (
                await CallAsync(
                    Editor(),
                    "cluster.logs.tail",
                    new { uniqueName = "test", node = "node-c" }
                )
            ).StatusCode
        );

        _gateway.Events =
        [
            new(1, DateTimeOffset.UnixEpoch, "node-joined", null, null, "node-a", null),
            new(
                2,
                DateTimeOffset.UnixEpoch,
                "world-saved",
                "quasar",
                null,
                null,
                new Dictionary<string, string> { ["partitions"] = "4" }
            ),
        ];
        var events = Assert.IsType<QuasarServerOperations.LogTailResponse>(
            (await CallAsync(Editor(), "cluster.events.tail", new { uniqueName = "test" })).Value
        );
        Assert.Equal(
            new[]
            {
                "1970-01-01 00:00:00Z · node-joined · node-a",
                "1970-01-01 00:00:00Z · world-saved · quasar · {\"partitions\":\"4\"}",
            },
            events.Lines
        );

        var tail = await CallAsync(Editor(), "logs.magnetar.tail", new { uniqueName = "test" });
        Assert.Contains("cluster.logs.tail", tail.Error);
    }

    private Task<QuasarServerOperations.LogTailResponse> Slice(
        string path,
        QuasarServerOperations.LogTailRequest request
    ) => QuasarServerOperations.ReadLogSliceAsync(path, request, default);

    private Task<QuasarOperationResult> SetField(string field, object value) =>
        CallAsync(
            Editor(),
            "servers.definition.set",
            new
            {
                uniqueName = "alpha",
                field,
                value,
            }
        );

    private Task<QuasarOperationResult> SetSetting(string option, object value) =>
        CallAsync(
            Editor(),
            "config.settings.set",
            new
            {
                uniqueName = "alpha",
                option,
                value,
            }
        );

    private Task<QuasarOperationResult> Player(string operation, ClaimsPrincipal? user = null) =>
        CallAsync(user ?? Editor(), operation, new { uniqueName = "alpha", steamId = SteamId });

    private KnownPlayerRecord? Known() =>
        _known.GetPlayers().FirstOrDefault(record => record.SteamId == long.Parse(SteamId));

    private void ConnectAgent(string uniqueName) =>
        _registry.UpsertHello(
            new AgentHello
            {
                AgentId = "agent",
                UniqueName = uniqueName,
                ServerId = uniqueName,
            },
            "connection",
            (message, _) =>
            {
                var command = message.Command!;
                _commands.Add(command);
                var entities =
                    command.CommandType == ServerCommandType.ListEntities
                        ? JsonSerializer.Serialize(
                            new EntityListResult
                            {
                                Entities = _alphaEntities,
                                TotalCount = _alphaEntities.Count,
                                TotalEntityCount = _alphaEntities.Count,
                            },
                            QuasarCoreOperations.Json
                        )
                        : null;
                var result = new ServerCommandResult
                {
                    AgentId = command.AgentId,
                    CommandId = command.CommandId,
                    UniqueName = uniqueName,
                    ServerId = uniqueName,
                    Success = _commandsSucceed,
                    Message = _commandsSucceed ? "done" : "refused",
                    Payload = entities,
                    CompletedAtUtc = DateTimeOffset.UtcNow,
                };
                // Answered before the send returns, so the known-player outcome is recorded before the call ends.
                _registry.UpdateCommandResult(result, "connection");
                return Task.CompletedTask;
            }
        );

    private async Task CreateServerAsync(string uniqueName, bool world, string profile = "")
    {
        await _servers.UpsertAsync(
            new DedicatedServerDefinition
            {
                UniqueName = uniqueName,
                WorldSaveName = "World",
                ConfigProfileId = profile,
            }
        );
        if (!world)
            return;
        var path = _servers.GetServer(uniqueName)!.GetWorldSavePath();
        Directory.CreateDirectory(path);
        await File.WriteAllTextAsync(Path.Combine(path, "Sandbox.sbc"), "save");
    }

    private Task CreateProfileAsync() =>
        _profiles.UpsertAsync(
            new QuasarConfigProfile
            {
                ConfigProfileId = "shared",
                Name = "Shared",
                RootSettings = new() { ServerPassword = "secret" },
            }
        );

    private async Task<QuasarAuditEntry> LatestAsync(string operation, int skip = 0) =>
        (
            await _services
                .GetRequiredService<QuasarAuditLog>()
                .QueryAsync(new() { Operation = Core + "/" + operation }, default)
        )
            .Where(entry =>
                entry.Operation == Core + "/" + operation
                && entry.Reverses is null
                && entry.Result == QuasarAuditResult.Ok
            )
            .Skip(skip)
            .First();

    private Task<QuasarUndoOutcome> UndoAsync(string entryId) =>
        _services
            .GetRequiredService<QuasarAuditLog>()
            .UndoAsync(Person(QuasarRoles.Admin), entryId, force: false, default);

    private static ClaimsPrincipal Editor() => Person(QuasarRoles.Editor);

    private static ClaimsPrincipal Person(string role) =>
        new(
            new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, role + "-person"),
                    new Claim(ClaimTypes.Role, role),
                ],
                "test"
            )
        );

    private async Task<QuasarOperationResult> CallAsync(
        ClaimsPrincipal user,
        string operationId,
        object request
    )
    {
        await using var scope = _services.CreateAsyncScope();
        return await scope
            .ServiceProvider.GetRequiredService<QuasarOperationService>()
            .DispatchAsync(
                new(user, scope.ServiceProvider),
                Core,
                operationId,
                JsonSerializer.SerializeToElement(request, QuasarCoreOperations.Json),
                default
            );
    }

    private sealed class NoHost : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken token
        ) => throw new HttpRequestException("No Host in these tests.");
    }

    private sealed class PersonState(ClaimsPrincipal user) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(user));
    }
}
