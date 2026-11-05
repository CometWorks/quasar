using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using Magnetar.Protocol.Model;
using Magnetar.Protocol.Runtime;
using Magnetar.Protocol.Transport;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Quasar.Plugin.Abstractions.Assistant;
using Quasar.Plugin.Abstractions.Companion;
using Quasar.Plugin.Abstractions.Operations;
using Quasar.Plugin.Abstractions.Security;
using Quasar.Services;
using Quasar.Services.Assistant;
using Quasar.Services.Audit;
using Quasar.Services.Auth;
using Quasar.Services.Plugins;
using Xunit;
using QuasarPolicyNames = Quasar.Services.Auth.QuasarPolicyNames;

namespace Quasar.Tests;

[Collection("Exact server creation")]
public sealed class QuasarPlayerAssistantTests : IDisposable
{
    private const string Core = QuasarCoreOperations.PluginId;
    private const string Pilot = "76561198000000001";
    private const string Stranger = "76561198000000999";
    private const string AdminSteamId = "76561198000000042";
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static readonly string[] MyOperations =
    [
        "my.server.info",
        "my.grids",
        "my.grid.backups",
        "my.grid.losses",
        "my.grid.locate",
        "my.grid.restore",
    ];

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "quasar-player-assistant-" + Guid.NewGuid().ToString("N")
    );
    private readonly FieldInfo _cache = typeof(MagnetarPaths).GetField(
        "_cachedQuasarDirectory",
        BindingFlags.NonPublic | BindingFlags.Static
    )!;
    private readonly object? _previous;
    private readonly RbacConfigCatalog _rbac;
    private readonly KnownPlayerCatalog _knownPlayers;
    private readonly AgentRegistry _registry;
    private readonly RecordingHandler _handler = new();
    private readonly RecordingAdminHandler _adminHandler = new();
    private readonly ServiceProvider _services;

    public QuasarPlayerAssistantTests()
    {
        _previous = _cache.GetValue(null);
        _cache.SetValue(null, _root);
        Directory.CreateDirectory(_root);
        _rbac = new(NullLogger<RbacConfigCatalog>.Instance);
        _knownPlayers = new(NullLogger<KnownPlayerCatalog>.Instance);
        _registry = new(_knownPlayers, null!, null!, null!);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(_rbac);
        services.AddSingleton(_knownPlayers);
        services.AddSingleton(_registry);
        services.AddSingleton(new QuasarAuthOptions());
        services.AddSingleton<QuasarRoleMapper>();
        services.AddAuthorization(Program.ConfigureAuthorizationPolicies);
        services.AddSingleton<IAuthorizationPolicyProvider, QuasarAuthorizationPolicyProvider>();
        services.AddScoped<QuasarOperationService>();
        services.AddSingleton(new QuasarAuditOptions { Directory = Path.Combine(_root, "audit") });
        services.AddSingleton<QuasarAuditLog>();
        services.AddSingleton<QuasarAgentPrincipals>();
        services.AddSingleton<IQuasarCompanionChannel>(
            new QuasarCompanionChannel(_registry, null!)
        );
        services.AddSingleton<IQuasarPlayerRequestHandler>(_handler);
        services.AddSingleton<IQuasarAdminRequestHandler>(_adminHandler);
        services.AddQuasarPlayerAssistance();
        services.AddQuasarMyOperations();
        _services = services.BuildServiceProvider();
    }

    public void Dispose()
    {
        _services.Dispose();
        _rbac.Dispose();
        _cache.SetValue(null, _previous);
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException) { }
    }

    [Fact]
    public void MyOperationsDescribeThemselvesAndExportTheirRequestSchema()
    {
        var operations = _services
            .GetServices<QuasarOperation>()
            .Where(operation => operation.OperationId.StartsWith("my.", StringComparison.Ordinal))
            .ToList();
        Assert.Equal(6, operations.Count);
        foreach (var operation in operations)
        {
            Assert.False(
                string.IsNullOrWhiteSpace(operation.Metadata.Description),
                operation.OperationId
            );
            var schema = System.Text.Json.Schema.JsonSchemaExporter.GetJsonSchemaAsNode(
                JsonSerializerOptions.Web,
                operation.RequestType!
            );
            Assert.NotNull(schema["properties"]?["uniqueName"]);
        }
    }

    [Fact]
    public async Task MyOperationsAreOnlyForAPlayerAgentWithASteamId()
    {
        Connect(Standalone("alpha"));
        var admin = Person(QuasarRoles.Admin);
        var assistant = await _services
            .GetRequiredService<QuasarAgentPrincipals>()
            .CreateAssistantAsync(admin, "c", "r");
        var unlinked = _services
            .GetRequiredService<QuasarRoleMapper>()
            .RefreshRoles(
                new ClaimsPrincipal(
                    new QuasarAgentIdentity(
                        QuasarAgentKind.Player,
                        null,
                        null,
                        null,
                        "999",
                        "discord:999",
                        "q",
                        []
                    )
                )
            );
        foreach (var operation in MyOperations)
        {
            Assert.Equal(403, (await CallAsync(admin, operation, new { gridId = 1 })).StatusCode);
            Assert.Equal(
                403,
                (await CallAsync(assistant, operation, new { gridId = 1 })).StatusCode
            );
            var refused = await CallAsync(unlinked, operation, new { gridId = 1 });
            Assert.Equal(403, refused.StatusCode);
            Assert.Contains("player agent", refused.Error);
        }

        var info = await CallAsync(Player("alpha"), "my.server.info");
        Assert.Equal(200, info.StatusCode);
        Assert.Equal("alpha", Json(info).GetProperty("uniqueName").GetString());
    }

    [Fact]
    public async Task CompanionRequestsAlwaysCarryTheCallersOwnSteamId()
    {
        var alpha = Connect(Standalone("alpha"));
        var requests = new List<JsonElement>();
        alpha.Handle = command =>
            Companion(
                command,
                request =>
                {
                    requests.Add(JsonDocument.Parse(request.PayloadJson).RootElement.Clone());
                    return request.Operation switch
                    {
                        "list" => new { grids = new[] { new { gridId = 7L, present = false } } },
                        _ => new { ok = true },
                    };
                }
            );

        var player = Player("alpha");
        Assert.Equal(
            200,
            (await CallAsync(player, "my.grids", new { steamId = Stranger })).StatusCode
        );
        Assert.Equal(
            200,
            (
                await CallAsync(player, "my.grid.backups", new { gridId = 7, steamId = Stranger })
            ).StatusCode
        );
        Assert.Equal(
            200,
            (await CallAsync(player, "my.grid.losses", new { steamId = Stranger })).StatusCode
        );
        Assert.Equal(3, requests.Count);
        Assert.All(
            requests,
            request => Assert.Equal(Pilot, request.GetProperty("steamId").GetString())
        );
        Assert.Equal(7, requests[1].GetProperty("gridId").GetInt64());
    }

    [Fact]
    public async Task TheServerDefaultsToTheRequestsSourceAndOthersMustBeNamed()
    {
        var alpha = Connect(Standalone("alpha"));
        var beta = Connect(Standalone("beta"));
        Connect(Standalone("gamma"), gridBackups: false);
        alpha.Handle = command =>
            Companion(command, _ => new { grids = Array.Empty<object>(), server = "alpha" });
        beta.Handle = command =>
            Companion(command, _ => new { grids = Array.Empty<object>(), server = "beta" });

        Assert.Equal(
            "alpha",
            Json(await CallAsync(Player("alpha"), "my.grids")).GetProperty("server").GetString()
        );
        var discordPlayer = Player(null);
        var ambiguous = await CallAsync(discordPlayer, "my.grids");
        Assert.Equal(400, ambiguous.StatusCode);
        Assert.Contains("alpha, beta", ambiguous.Error);
        Assert.DoesNotContain("gamma", ambiguous.Error);
        Assert.Equal(
            "beta",
            Json(await CallAsync(discordPlayer, "my.grids", new { uniqueName = "beta" }))
                .GetProperty("server")
                .GetString()
        );
        Assert.Equal(
            409,
            (await CallAsync(discordPlayer, "my.grids", new { uniqueName = "gamma" })).StatusCode
        );
        Assert.Equal(
            404,
            (await CallAsync(discordPlayer, "my.grids", new { uniqueName = "delta" })).StatusCode
        );
    }

    [Fact]
    public async Task RestoreIsAuditedAndItsUndoRemovesTheRestoredGrids()
    {
        var alpha = Connect(Standalone("alpha"));
        var world = new HashSet<long>();
        var removed = new List<long[]>();
        var restoreRequests = new List<JsonElement>();
        var answer = "restored";
        alpha.Handle = command =>
            command.CommandType switch
            {
                ServerCommandType.ListEntities => Entities(world, command),
                _ => Companion(
                    command,
                    request =>
                    {
                        var payload = JsonDocument.Parse(request.PayloadJson).RootElement.Clone();
                        if (request.Operation == "remove-restored")
                        {
                            var ids = payload
                                .GetProperty("entityIds")
                                .EnumerateArray()
                                .Select(id => id.GetInt64())
                                .ToArray();
                            removed.Add(ids);
                            world.ExceptWith(ids);
                            return new { removed = ids, missing = Array.Empty<long>() };
                        }

                        restoreRequests.Add(payload);
                        if (answer != "restored")
                            return new { status = answer, reason = "No free space." };
                        world.UnionWith([10, 11]);
                        return new
                        {
                            status = "restored",
                            backupId = "b/1.sbc",
                            entityIds = new[] { 10L, 11L },
                        };
                    }
                ),
            };

        var restored = await CallAsync(
            Player("alpha"),
            "my.grid.restore",
            new { gridId = 10, steamId = Stranger }
        );
        Assert.Equal(200, restored.StatusCode);
        Assert.Equal("restored", Json(restored).GetProperty("status").GetString());
        var request = Assert.Single(restoreRequests);
        Assert.Equal(Pilot, request.GetProperty("steamId").GetString());
        Assert.Equal("original", request.GetProperty("placement").GetString());
        Assert.Equal(2000, request.GetProperty("foreignExclusionMeters").GetDouble());
        Assert.Equal(10000, request.GetProperty("maxSearchMeters").GetDouble());

        var audit = _services.GetRequiredService<QuasarAuditLog>();
        var entry = Assert.Single(await audit.QueryAsync(new(), default));
        Assert.Equal(QuasarAuditResult.Ok, entry.Result);
        Assert.Equal("alpha", entry.Server);
        Assert.Equal(Pilot, entry.Requester.SteamId);
        Assert.Equal(
            new long[] { 10, 11 },
            entry
                .UndoData!.Value.GetProperty("entityIds")
                .EnumerateArray()
                .Select(id => id.GetInt64())
        );

        // A player's action can be undone by an admin, although no person satisfies the player policy.
        world.Remove(11);
        var conflict = await audit.UndoAsync(
            Person(QuasarRoles.Admin),
            entry.Id,
            force: false,
            default
        );
        Assert.Equal(QuasarUndoStatus.Conflict, conflict.Status);
        Assert.Contains("11", conflict.Detail);
        Assert.Equal(
            QuasarUndoStatus.Refused,
            (await audit.UndoAsync(Person(QuasarRoles.Editor), entry.Id, true, default)).Status
        );
        Assert.Equal(
            QuasarUndoStatus.Done,
            (
                await audit.UndoAsync(Person(QuasarRoles.Admin), entry.Id, force: true, default)
            ).Status
        );
        Assert.Equal(new long[] { 10 }, Assert.Single(removed));
        Assert.Empty(world);

        answer = "no-spot";
        var noSpot = await CallAsync(Player("alpha"), "my.grid.restore", new { gridId = 10 });
        Assert.Equal(409, noSpot.StatusCode);
        Assert.StartsWith("no-spot: No free space.", noSpot.Error);
        var refused = (await audit.QueryAsync(new(), default)).First();
        Assert.Equal((QuasarAuditResult.Refused, "alpha"), (refused.Result, refused.Server));
        Assert.StartsWith("no-spot: No free space.", refused.Error);
        Assert.Equal(
            400,
            (
                await CallAsync(
                    Player("alpha"),
                    "my.grid.restore",
                    new { gridId = 10, placement = "orbit" }
                )
            ).StatusCode
        );
    }

    [Fact]
    public async Task ClusterRestoreGoesToTheNodeNearTheBackupOrThePlayer()
    {
        var nodeA = Connect(
            Node("a"),
            players: [new() { SteamId = long.Parse(Pilot), DisplayName = "Pilot" }]
        );
        var nodeB = Connect(Node("b"));
        var authority = Connect(Node("wa", "WA"));
        var restoredOn = new List<string>();
        Func<ServerCommandEnvelope, (bool, string)> Agent(
            string name,
            bool ownsArea,
            bool present
        ) =>
            command =>
                command.CommandType switch
                {
                    ServerCommandType.ResolvePositionOwner => (
                        true,
                        JsonSerializer.Serialize(
                            new PositionOwnerResult { Found = ownsArea, DistanceMeters = 50 },
                            Web
                        )
                    ),
                    _ => Companion(
                        command,
                        request =>
                        {
                            if (request.Operation == "restore")
                                restoredOn.Add(name);
                            return request.Operation switch
                            {
                                "list" => new { grids = new[] { new { gridId = 7L, present } } },
                                "describe" => new
                                {
                                    gridId = 7L,
                                    backups = new object[]
                                    {
                                        new
                                        {
                                            takenUtc = "2026-11-05T12:00:00Z",
                                            position = new
                                            {
                                                x = 9.0,
                                                y = 9.0,
                                                z = 9.0,
                                            },
                                        },
                                        new
                                        {
                                            takenUtc = "2026-11-05T10:00:00Z",
                                            position = new
                                            {
                                                x = 1.0,
                                                y = 2.0,
                                                z = 3.0,
                                            },
                                        },
                                    },
                                    loss = new
                                    {
                                        timeUtc = "2026-11-05T11:00:00Z",
                                        cause = "destroyed",
                                    },
                                },
                                _ => new { status = "restored", entityIds = new[] { 7L } },
                            };
                        }
                    ),
                };
        nodeA.Handle = Agent("a", ownsArea: false, present: false);
        nodeB.Handle = Agent("b", ownsArea: true, present: true);
        authority.Handle = _ =>
            throw new InvalidOperationException("The World Authority simulates no grids.");

        var player = Player("fleet");
        Assert.Equal(
            200,
            (await CallAsync(player, "my.grid.restore", new { gridId = 7 })).StatusCode
        );
        Assert.Equal(
            200,
            (
                await CallAsync(
                    player,
                    "my.grid.restore",
                    new { gridId = 7, placement = "near-player" }
                )
            ).StatusCode
        );
        Assert.Equal(new[] { "b", "a" }, restoredOn);
        var position = nodeB.Commands.Single(c =>
            c.CommandType == ServerCommandType.ResolvePositionOwner
        );
        Assert.Equal(
            1.0,
            JsonSerializer.Deserialize<PositionOwnerRequest>(position.Payload, Web)!.X
        ); // the backup before the loss

        var grids = Json(await CallAsync(player, "my.grids"));
        Assert.True(grids.GetProperty("grids")[0].GetProperty("present").GetBoolean());
        Assert.Empty(authority.Commands);

        // Nobody owns the area: the player's node.
        nodeB.Handle = Agent("b", ownsArea: false, present: true);
        await CallAsync(player, "my.grid.restore", new { gridId = 7 });
        Assert.Equal("a", restoredOn[^1]);
        Assert.Null(QuasarPlayerTools.Nearest([]));
    }

    [Fact]
    public async Task LocateGivesAGpsOnlyForGridsThePlayerBigOwns()
    {
        var alpha = Connect(Standalone("alpha"));
        alpha.Handle = command =>
            (
                true,
                JsonSerializer.Serialize(
                    new EntityListResult
                    {
                        Entities =
                        [
                            new()
                            {
                                EntityId = 5,
                                DisplayName = "Mi:ner",
                                TypeTag = "Grid",
                                PositionX = 1.5,
                                PositionY = -2,
                                PositionZ = 3,
                                OwnerSteamId = ulong.Parse(Stranger),
                                BigOwnerSteamIds = [ulong.Parse(Stranger), ulong.Parse(Pilot)],
                            },
                            new()
                            {
                                EntityId = 6,
                                DisplayName = "Base",
                                TypeTag = "Grid",
                                OwnerSteamId = ulong.Parse(Stranger),
                            },
                        ],
                    },
                    Web
                )
            );

        var located = Json(await CallAsync(Player("alpha"), "my.grid.locate", new { gridId = 5 }));
        Assert.Equal("GPS:Miner:1.50:-2.00:3.00:", located.GetProperty("gps").GetString());
        Assert.Equal(
            403,
            (await CallAsync(Player("alpha"), "my.grid.locate", new { gridId = 6 })).StatusCode
        );
        Assert.Equal(
            404,
            (await CallAsync(Player("alpha"), "my.grid.locate", new { gridId = 8 })).StatusCode
        );
    }

    [Fact]
    public async Task GameRequestsBuildThePlayerFromTheConnectionAndWhisperBack()
    {
        var alpha = Connect(Standalone("alpha"));
        var whispers = new List<ServerCommandEnvelope>();
        alpha.Handle = command =>
        {
            whispers.Add(command);
            return (true, "");
        };
        var source = _registry.GetAgents().Single(agent => agent.UniqueNameKey == "alpha");
        var requests = _services.GetRequiredService<QuasarPlayerRequests>();
        _handler.Answer = new string('x', 700);

        await requests.HandleGameRequestAsync(
            source,
            new PlayerRequest
            {
                Kind = PlayerRequest.AskKind,
                SteamId = long.Parse(Pilot),
                PlayerName = "Pilot",
                Text = "where is my ship?",
            },
            default
        );
        var request = Assert.Single(_handler.Requests);
        Assert.True(request.Player!.IsQuasarAgent(out var identity));
        Assert.Equal(Pilot, identity.PlayerSteamId);
        Assert.Equal("alpha", identity.Server);
        Assert.Equal("steam:" + Pilot, request.ConversationKey);
        Assert.Equal(
            new QuasarPlayerRequestSource(QuasarRequestChannel.Game, "alpha"),
            request.Source
        );
        Assert.Equal(2, whispers.Count);
        Assert.All(
            whispers,
            whisper =>
            {
                Assert.Equal(ServerCommandType.SendWhisper, whisper.CommandType);
                Assert.Equal(long.Parse(Pilot), whisper.SteamId);
            }
        );

        await requests.HandleGameRequestAsync(
            source,
            new PlayerRequest { Kind = PlayerRequest.LinkKind, SteamId = long.Parse(Pilot) },
            default
        );
        var code = whispers[^1].Text.Split(' ')[2];
        Assert.Equal(Pilot, (await requests.LinkDiscordAsync("555", code, default))!.SteamId);

        // A linked Discord user asks as that player; an unlinked one without a player.
        var replies = new List<string>();
        await requests.AskFromDiscordAsync(
            "555",
            "chan",
            "hello",
            (text, _) =>
            {
                replies.Add(text);
                return Task.CompletedTask;
            },
            default
        );
        await requests.AskFromDiscordAsync(
            "777",
            "chan",
            "hello",
            (text, _) =>
            {
                replies.Add(text);
                return Task.CompletedTask;
            },
            default
        );
        Assert.True(_handler.Requests[1].Player!.IsQuasarAgent(out var linked));
        Assert.Equal(Pilot, linked.PlayerSteamId);
        Assert.Equal("555", linked.DiscordUserId);
        Assert.Null(linked.Server);
        Assert.Null(_handler.Requests[2].Player);
        Assert.Equal("discord:777", _handler.Requests[2].ConversationKey);
        Assert.Equal(2, replies.Count);
    }

    [Fact]
    public async Task DiscordAdminsAreTheirMappedSubjectAndNeverAnAgent()
    {
        await _rbac.UpdateAsync(config =>
        {
            config.SubjectRoleMappings.Add(
                new() { Subject = AdminSteamId, Roles = [QuasarRoles.Admin] }
            );
            config.DiscordSubjects.Add(new() { DiscordUserId = "100", Subject = AdminSteamId });
            config.DiscordSubjects.Add(
                new()
                {
                    DiscordUserId = "200",
                    Provider = "Oidc",
                    Subject = "someone",
                }
            );
            config.DiscordSubjects.Add(
                new() { DiscordUserId = "not-a-snowflake", Subject = AdminSteamId }
            );
            return Task.CompletedTask;
        });
        Assert.Equal(2, _rbac.GetConfig().DiscordSubjects.Count);

        var requests = _services.GetRequiredService<QuasarPlayerRequests>();
        var replies = new List<string>();
        Task Reply(string text, CancellationToken _)
        {
            replies.Add(text);
            return Task.CompletedTask;
        }
        await requests.AskAdminFromDiscordAsync("100", "admin-channel", "status?", Reply, default);
        await requests.AskAdminFromDiscordAsync("200", "admin-channel", "status?", Reply, default);
        await requests.AskAdminFromDiscordAsync("300", "admin-channel", "status?", Reply, default);

        var asked = Assert.Single(_adminHandler.Requests);
        Assert.True(asked.Asker.IsInRole(QuasarRoles.Admin));
        Assert.False(asked.Asker.IsQuasarAgent(out _));
        Assert.Equal(AdminSteamId, asked.Asker.FindFirstValue(ClaimTypes.NameIdentifier));
        Assert.Equal("admin-channel", asked.ChannelId);
        Assert.Equal(
            new[] { QuasarPlayerRequests.NotMapped, QuasarPlayerRequests.NotMapped },
            replies
        );
    }

    private sealed class FakeAgent
    {
        internal readonly List<ServerCommandEnvelope> Commands = [];
        internal Func<ServerCommandEnvelope, (bool Success, string Payload)> Handle = _ =>
            (false, "Not handled.");
    }

    private sealed class RecordingHandler : IQuasarPlayerRequestHandler
    {
        internal readonly List<QuasarPlayerRequest> Requests = [];
        internal string Answer = "ok";

        public async Task HandleAsync(QuasarPlayerRequest request, CancellationToken token)
        {
            Requests.Add(request);
            await request.ReplyAsync(Answer, token);
        }
    }

    private sealed class RecordingAdminHandler : IQuasarAdminRequestHandler
    {
        internal readonly List<QuasarAdminRequest> Requests = [];

        public Task HandleAsync(QuasarAdminRequest request, CancellationToken token)
        {
            Requests.Add(request);
            return Task.CompletedTask;
        }
    }

    private FakeAgent Connect(
        AgentHello hello,
        List<PlayerSnapshot>? players = null,
        bool gridBackups = true
    )
    {
        var agent = new FakeAgent();
        var connection = "connection-" + hello.AgentId;
        Assert.True(
            _registry.TryUpsertHello(
                hello,
                connection,
                (message, _) =>
                {
                    if (message.Command is { } command)
                    {
                        lock (agent.Commands)
                            agent.Commands.Add(command);
                        var (success, payload) = agent.Handle(command);
                        _registry.UpdateCommandResult(
                            new ServerCommandResult
                            {
                                CommandId = command.CommandId,
                                AgentId = command.AgentId,
                                Success = success,
                                Payload = success ? payload : string.Empty,
                                Message = success ? string.Empty : payload,
                            },
                            connection
                        );
                    }

                    return Task.CompletedTask;
                },
                DateTimeOffset.UtcNow.AddMinutes(-1)
            )
        );
        _registry.UpdateSnapshot(
            new AgentSnapshot
            {
                UniqueName = hello.UniqueName,
                AgentId = hello.AgentId,
                HostId = hello.HostId,
                ServerId = hello.ServerId,
                ServerName = hello.UniqueName,
                ClusterMode = hello.ClusterMode,
                ClusterId = hello.ClusterId,
                ClusterSlot = hello.ClusterSlot,
                ClusterNodeId = hello.ClusterNodeId,
                ClusterNodeRole = hello.ClusterNodeRole,
                ClusterEpoch = hello.ClusterEpoch,
                IsRunning = true,
                TelemetryPaused = true,
                Players = players ?? [],
                Plugins = gridBackups
                    ?
                    [
                        new()
                        {
                            PluginId = QuasarPlayerTools.GridBackupsPluginId.ToLowerInvariant(),
                            IsLoaded = true,
                        },
                    ]
                    : [],
            },
            connection
        );
        return agent;
    }

    private static AgentHello Standalone(string name) =>
        new()
        {
            UniqueName = name,
            ServerId = name,
            AgentId = name + ":1",
        };

    private static AgentHello Node(string node, string role = "Regular") =>
        new()
        {
            UniqueName = "fleet-" + node,
            ServerId = "fleet-" + node,
            AgentId = "fleet-" + node + ":1",
            HostId = "host",
            ClusterMode = true,
            ClusterId = "fleet",
            ClusterSlot = "slot-" + node,
            ClusterNodeId = "node-" + node,
            ClusterNodeRole = role,
            ClusterLaunchAttempt = "1",
            ClusterEpoch = 1,
        };

    private static (bool, string) Companion(
        ServerCommandEnvelope command,
        Func<CompanionPluginRequest, object> answer
    )
    {
        Assert.Equal(ServerCommandType.PluginRequest, command.CommandType);
        var request = JsonSerializer.Deserialize<CompanionPluginRequest>(command.Payload, Web)!;
        Assert.Equal(QuasarPlayerTools.GridBackupsPluginId, request.PluginId);
        return (
            true,
            JsonSerializer.Serialize(
                new CompanionPluginResponse
                {
                    CorrelationId = request.CorrelationId,
                    Success = true,
                    PayloadJson = JsonSerializer.Serialize(answer(request), Web),
                },
                Web
            )
        );
    }

    private static (bool, string) Entities(IEnumerable<long> world, ServerCommandEnvelope command)
    {
        var filter = JsonSerializer.Deserialize<EntityListFilter>(command.Payload, Web)!;
        return (
            true,
            JsonSerializer.Serialize(
                new EntityListResult
                {
                    Entities = world
                        .Where(id => id.ToString() == filter.Search)
                        .Select(id => new EntitySummary { EntityId = id, TypeTag = "Grid" })
                        .ToList(),
                },
                Web
            )
        );
    }

    private ClaimsPrincipal Player(string? server) =>
        _services
            .GetRequiredService<QuasarAgentPrincipals>()
            .CreatePlayer(Pilot, "Pilot", null, "steam:" + Pilot, "question", server);

    private static ClaimsPrincipal Person(string role) =>
        new(
            new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, "person"), new Claim(ClaimTypes.Role, role)],
                "test"
            )
        );

    private static JsonElement Json(QuasarOperationResult result)
    {
        Assert.True(result.Error is null, result.Error);
        return JsonSerializer.SerializeToElement(result.Value, Web);
    }

    private async Task<QuasarOperationResult> CallAsync(
        ClaimsPrincipal user,
        string operationId,
        object? request = null
    )
    {
        await using var scope = _services.CreateAsyncScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<QuasarOperationService>();
        return await dispatcher.DispatchAsync(
            new(user, scope.ServiceProvider),
            Core,
            operationId,
            JsonSerializer.SerializeToElement(request ?? new { }),
            default
        );
    }
}
