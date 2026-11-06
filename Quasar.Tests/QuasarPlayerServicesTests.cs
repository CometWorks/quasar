using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using Magnetar.Protocol.Model;
using Magnetar.Protocol.Runtime;
using Magnetar.Protocol.Transport;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Quasar.Plugin.Abstractions.Commands;
using Quasar.Plugin.Abstractions.Companion;
using Quasar.Plugin.Abstractions.Operations;
using Quasar.Plugin.Abstractions.Security;
using Quasar.Plugin.Abstractions.Servers;
using Quasar.Services;
using Quasar.Services.Audit;
using Quasar.Services.Auth;
using Quasar.Services.Players;
using Quasar.Services.Plugins;
using Xunit;
using QuasarPolicyNames = Quasar.Services.Auth.QuasarPolicyNames;

namespace Quasar.Tests;

[Collection("Exact server creation")]
public sealed class QuasarPlayerServicesTests : IDisposable
{
    private const string Core = QuasarCoreOperations.PluginId;
    private const string Pilot = "76561198000000001";
    private const string Stranger = "76561198000000999";
    private const string AdminSteamId = "76561198000000042";
    private const string GridBackups = "9AC2F6A8-B8A7-4250-8814-BADE62C5CF3C";
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static readonly string[] MyOperations = ["my.server.info", "my.grid.locate"];

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "quasar-player-services-" + Guid.NewGuid().ToString("N")
    );
    private readonly FieldInfo _cache = typeof(MagnetarPaths).GetField(
        "_cachedQuasarDirectory",
        BindingFlags.NonPublic | BindingFlags.Static
    )!;
    private readonly object? _previous;
    private readonly RbacConfigCatalog _rbac;
    private readonly KnownPlayerCatalog _knownPlayers;
    private readonly AgentRegistry _registry;
    private readonly RecordingCommand _ask = new(new("ask", "Ask", "question", "Your question"));
    private readonly RecordingCommand _admin = new(
        new(
            "assistant",
            "Ask the admins' assistant",
            "question",
            "Your question",
            QuasarChatCommandScope.Admin
        )
    );
    private readonly ServiceProvider _services;

    public QuasarPlayerServicesTests()
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
        services.AddSingleton<IQuasarChatCommand>(_ask);
        services.AddSingleton<IQuasarChatCommand>(_admin);
        services.AddQuasarPlayerServices();
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
        Assert.Equal(MyOperations.Length, operations.Count);
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
    public async Task TheServerDefaultsToTheRequestsSourceAndOthersMustBeNamed()
    {
        Connect(Standalone("alpha"));
        Connect(Standalone("beta"));

        Assert.Equal(
            "alpha",
            Json(await CallAsync(Player("alpha"), "my.server.info"))
                .GetProperty("uniqueName")
                .GetString()
        );
        var discordPlayer = Player(null);
        var ambiguous = await CallAsync(discordPlayer, "my.server.info");
        Assert.Equal(400, ambiguous.StatusCode);
        Assert.Contains("alpha, beta", ambiguous.Error);
        Assert.Equal(
            "beta",
            Json(await CallAsync(discordPlayer, "my.server.info", new { uniqueName = "beta" }))
                .GetProperty("uniqueName")
                .GetString()
        );
        Assert.Equal(
            404,
            (
                await CallAsync(discordPlayer, "my.server.info", new { uniqueName = "delta" })
            ).StatusCode
        );
    }

    [Fact]
    public async Task ServerQueriesFindPlayersGridsAndAreaOwnersOnTheRegularNodes()
    {
        var nodeA = Connect(
            Node("a"),
            players: [new() { SteamId = long.Parse(Pilot), DisplayName = "Pilot" }]
        );
        var nodeB = Connect(Node("b"));
        var authority = Connect(Node("wa", "WA"));
        nodeA.Handle = command =>
            command.CommandType == ServerCommandType.ListEntities
                ? Entities([], command)
                : Owner(found: true, distance: 80);
        nodeB.Handle = command =>
            command.CommandType == ServerCommandType.ListEntities
                ? Entities([7], command)
                : Owner(found: true, distance: 50);
        authority.Handle = _ =>
            throw new InvalidOperationException("The World Authority simulates no grids.");
        var queries = _services.GetRequiredService<IQuasarServerQueries>();

        var fleet = Assert.Single(queries.GetServers());
        Assert.Equal("fleet", fleet.UniqueName);
        Assert.True(fleet.IsCluster);
        Assert.Equal(["node-a", "node-b"], fleet.Nodes.Select(node => node.ClusterNode));
        Assert.True(fleet.Nodes[0].HasPlugin(GridBackups));

        Assert.Equal("fleet-a:1", queries.FindPlayer("cluster:fleet", Pilot));
        Assert.Null(queries.FindPlayer("fleet", Stranger));
        var grids = await queries.FindGridsAsync("fleet", [7, 8], default);
        Assert.Equal("fleet-b:1", Assert.Single(grids, pair => pair.Key == 7).Value);
        Assert.False(grids.ContainsKey(8));
        Assert.Equal(
            "fleet-b:1",
            await queries.FindPositionOwnerAsync("fleet", 1, 2, 3, 100, default)
        );
        var asked = nodeA.Commands.Single(c =>
            c.CommandType == ServerCommandType.ResolvePositionOwner
        );
        Assert.Equal(2.0, JsonSerializer.Deserialize<PositionOwnerRequest>(asked.Payload, Web)!.Y);
        Assert.Empty(authority.Commands);

        nodeB.Handle = command => Owner(found: false, distance: 0);
        nodeA.Handle = command => Owner(found: false, distance: 0);
        Assert.Null(await queries.FindPositionOwnerAsync("fleet", 1, 2, 3, 100, default));
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
        var requests = _services.GetRequiredService<QuasarChatCommands>();
        _ask.Answer = new string('x', 700);

        await requests.HandleGameRequestAsync(
            source,
            new PlayerRequest
            {
                Kind = "ask",
                SteamId = long.Parse(Pilot),
                PlayerName = "Pilot",
                Text = "where is my ship?",
            },
            default
        );
        var request = Assert.Single(_ask.Requests);
        Assert.Equal("ask", request.Command);
        Assert.True(request.Caller!.IsQuasarAgent(out var identity));
        Assert.Equal(Pilot, identity.PlayerSteamId);
        Assert.Equal("alpha", identity.Server);
        Assert.Equal("steam:" + Pilot, request.ConversationKey);
        Assert.Equal(new QuasarChatCommandSource(QuasarChatChannel.Game, "alpha"), request.Source);
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

        // Admin commands and unknown names don't run from the game.
        foreach (var kind in new[] { "assistant", "dance" })
            await requests.HandleGameRequestAsync(
                source,
                new PlayerRequest
                {
                    Kind = kind,
                    SteamId = long.Parse(Pilot),
                    Text = "hi",
                },
                default
            );
        Assert.Equal(QuasarChatCommands.NotAvailable, whispers[^1].Text);
        Assert.Empty(_admin.Requests);

        // A linked Discord user asks as that player; an unlinked one without a player.
        var replies = new List<string>();
        await requests.HandleDiscordAsync(
            "ask",
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
        await requests.HandleDiscordAsync(
            "ask",
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
        Assert.True(_ask.Requests[1].Caller!.IsQuasarAgent(out var linked));
        Assert.Equal(Pilot, linked.PlayerSteamId);
        Assert.Equal("555", linked.DiscordUserId);
        Assert.Null(linked.Server);
        Assert.Null(_ask.Requests[2].Caller);
        Assert.Equal("discord:777", _ask.Requests[2].ConversationKey);
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

        var requests = _services.GetRequiredService<QuasarChatCommands>();
        var replies = new List<string>();
        Task Reply(string text, CancellationToken _)
        {
            replies.Add(text);
            return Task.CompletedTask;
        }
        foreach (var user in new[] { "100", "200", "300" })
            await requests.HandleDiscordAsync(
                "assistant",
                user,
                "admin-channel",
                "status?",
                Reply,
                default
            );

        var asked = Assert.Single(_admin.Requests);
        Assert.True(asked.Caller!.IsInRole(QuasarRoles.Admin));
        Assert.False(asked.Caller.IsQuasarAgent(out _));
        Assert.Equal(AdminSteamId, asked.Caller.FindFirstValue(ClaimTypes.NameIdentifier));
        Assert.Equal("admin-channel", asked.Source.DiscordChannelId);
        Assert.Equal(
            new[] { "ok", QuasarChatCommands.NotMapped, QuasarChatCommands.NotMapped },
            replies
        );
    }

    [Fact]
    public void PluginCommandsNeedAFreeValidNameAndOnlyPlayerOnesGoToTheGame()
    {
        var commands = new QuasarChatCommands(
            _registry,
            _services.GetRequiredService<QuasarAgentPrincipals>(),
            _services.GetRequiredService<QuasarPlayerLinks>(),
            _knownPlayers,
            _rbac,
            _services.GetRequiredService<QuasarRoleMapper>(),
            [
                _ask,
                _admin,
                new RecordingCommand(new("ask", "Another ask", "text", "Text")),
                new RecordingCommand(new("link", "Not core's", "text", "Text")),
                new RecordingCommand(new("Shout", "Upper case", "text", "Text")),
                new RecordingCommand(
                    new(
                        "roll",
                        new string('d', 150),
                        "Bad Argument",
                        "Text",
                        Acknowledgement: "Rolling..."
                    )
                ),
            ],
            NullLogger<QuasarChatCommands>.Instance
        );

        Assert.Equal(["ask", "assistant", "roll"], commands.Commands.Select(info => info.Name));
        Assert.Same(_ask.Info, commands.Commands.First());
        var game = commands.GameCommands();
        Assert.Equal(["ask", "roll"], game.Select(info => info.Name));
        Assert.Equal("question", game[0].Argument);
        Assert.Equal("text", game[1].Argument);
        Assert.Equal(100, game[1].Description.Length);
        Assert.Equal("Rolling...", game[1].Acknowledgement);
    }

    private sealed class FakeAgent
    {
        internal readonly List<ServerCommandEnvelope> Commands = [];
        internal Func<ServerCommandEnvelope, (bool Success, string Payload)> Handle = _ =>
            (false, "Not handled.");
    }

    private sealed class RecordingCommand(QuasarChatCommandInfo info) : IQuasarChatCommand
    {
        internal readonly List<QuasarChatCommandRequest> Requests = [];
        internal string Answer = "ok";

        public QuasarChatCommandInfo Info => info;

        public async Task HandleAsync(QuasarChatCommandRequest request, CancellationToken token)
        {
            Requests.Add(request);
            await request.ReplyAsync(Answer, token);
        }
    }

    private FakeAgent Connect(AgentHello hello, List<PlayerSnapshot>? players = null)
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
                Plugins = [new() { PluginId = GridBackups.ToLowerInvariant(), IsLoaded = true }],
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

    private static (bool, string) Owner(bool found, double distance) =>
        (
            true,
            JsonSerializer.Serialize(
                new PositionOwnerResult { Found = found, DistanceMeters = distance },
                Web
            )
        );

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
