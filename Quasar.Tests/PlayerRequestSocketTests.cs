using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Magnetar.Protocol.Model;
using Magnetar.Protocol.Runtime;
using Magnetar.Protocol.Transport;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Quasar.Agent;
using Quasar.Host.Contract.V1;
using Quasar.Plugin.Abstractions.Assistant;
using Quasar.Plugin.Abstractions.Security;
using Quasar.Services;
using Quasar.Services.Assistant;
using Quasar.Services.Auth;
using Quasar.Services.PluginSdk;
using Xunit;

namespace Quasar.Tests;

[Collection("Exact server creation")]
public sealed class PlayerRequestSocketTests : IDisposable
{
    private const long Pilot = 76561198000000001;
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "player-request-socket-" + Guid.NewGuid().ToString("N")
    );
    private readonly FieldInfo _cache = typeof(MagnetarPaths).GetField(
        "_cachedQuasarDirectory",
        BindingFlags.NonPublic | BindingFlags.Static
    )!;
    private readonly object? _previous;
    private readonly ClusterCatalog _clusters;
    private readonly AgentSocketAuthenticator _authenticator;
    private readonly RbacConfigCatalog _rbac;
    private readonly AgentRegistry _registry;

    public PlayerRequestSocketTests()
    {
        _previous = _cache.GetValue(null);
        _cache.SetValue(null, _root);
        Directory.CreateDirectory(_root);
        var credentials = new ClusterCredentialStore(
            new EphemeralDataProtectionProvider(),
            Path.Combine(_root, "credentials.json")
        );
        _clusters = new(
            NullLogger<ClusterCatalog>.Instance,
            new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["Quasar:ClusterCatalogPath"] = Path.Combine(_root, "clusters"),
                    }
                )
                .Build()
        );
        _authenticator = new(
            credentials,
            new ClusterHostCatalog(credentials, Path.Combine(_root, "hosts")),
            _clusters
        );
        _rbac = new(NullLogger<RbacConfigCatalog>.Instance);
        _registry = new(
            new KnownPlayerCatalog(NullLogger<KnownPlayerCatalog>.Instance),
            null!,
            null!,
            null!
        );
    }

    public void Dispose()
    {
        _clusters.Dispose();
        _rbac.Dispose();
        _cache.SetValue(null, _previous);
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException) { }
    }

    private sealed class Handler : IQuasarPlayerRequestHandler
    {
        internal readonly TaskCompletionSource<QuasarPlayerRequest> Received = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        internal readonly TaskCompletionSource Replied = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        public async Task HandleAsync(QuasarPlayerRequest request, CancellationToken token)
        {
            Received.TrySetResult(request);
            await request.ReplyAsync("Your ship is at home.", token);
            Replied.TrySetResult();
        }
    }

    [Fact]
    public async Task PlayerRequestReachesTheHandlerAsTheConnectionsServerAndIsAnsweredByWhisper()
    {
        var handler = new Handler();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(_registry);
        builder.Services.AddSingleton(_authenticator);
        builder.Services.AddSingleton(
            new PluginConfigService(
                _registry,
                NullLogger<PluginConfigService>.Instance,
                Path.Combine(_root, "configs")
            )
        );
        builder.Services.AddSingleton<DedicatedServerSupervisor>(_ => null!);
        builder.Services.AddSingleton<PluginLogStream>();
        builder.Services.AddSingleton(_rbac);
        builder.Services.AddSingleton(new QuasarAuthOptions());
        builder.Services.AddSingleton<QuasarRoleMapper>();
        builder.Services.AddSingleton<KnownPlayerCatalog>();
        builder.Services.AddAuthorization(Program.ConfigureAuthorizationPolicies);
        builder.Services.AddSingleton<
            IAuthorizationPolicyProvider,
            QuasarAuthorizationPolicyProvider
        >();
        builder.Services.AddSingleton<QuasarAgentPrincipals>();
        builder.Services.AddSingleton<IQuasarPlayerRequestHandler>(handler);
        builder.Services.AddQuasarPlayerAssistance();
        builder.Services.AddSingleton<AgentSocketHandler>();
        await using var app = builder.Build();
        app.UseWebSockets();
        app.Map(
            "/ws/agent",
            (HttpContext context, AgentSocketHandler socketHandler) =>
                socketHandler.HandleAsync(context)
        );
        await app.StartAsync();

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var client = app.GetTestServer().CreateWebSocketClient();
        client.SubProtocols.Add("quasar.agent.v1");
        client.ConfigureRequest = request =>
            request.Headers.Add(AgentSocketToken.HeaderName, _authenticator.ForServer("alpha"));
        using var socket = await client.ConnectAsync(
            new Uri("ws://localhost/ws/agent"),
            deadline.Token
        );
        Task Send(string json) =>
            socket.SendAsync(
                Encoding.UTF8.GetBytes(json),
                WebSocketMessageType.Text,
                true,
                deadline.Token
            );
        await socket.SendAsync(
            AgentConnection.SerializeMessage(
                new AgentWireMessage
                {
                    Kind = WireMessageKind.Hello,
                    Hello = new()
                    {
                        UniqueName = "alpha",
                        ServerId = "alpha",
                        AgentId = "alpha:1",
                    },
                }
            ),
            WebSocketMessageType.Text,
            true,
            deadline.Token
        );
        while (_registry.GetAgents().Count == 0)
            await Task.Delay(10, deadline.Token);

        // Server fields smuggled into the payload are not part of the contract and change nothing.
        await Send(
            """{"kind":"player-request","playerRequest":{"requestId":"r1","kind":"ask","steamId":"""
                + Pilot
                + ""","identityId":5,"playerName":"Pilot","text":"where is my ship?","uniqueName":"beta","server":"beta","clusterId":"fleet"}}"""
        );
        var request = await handler.Received.Task.WaitAsync(deadline.Token);
        Assert.True(request.Player!.IsQuasarAgent(out var player));
        Assert.Equal(QuasarAgentKind.Player, player.Kind);
        Assert.Equal(Pilot.ToString(), player.PlayerSteamId);
        Assert.Equal("Pilot", player.PlayerName);
        Assert.Equal("alpha", player.Server);
        Assert.Equal("alpha", request.Source.Server);
        Assert.Equal(QuasarRequestChannel.Game, request.Source.Channel);
        Assert.Equal("where is my ship?", request.Text);

        var buffer = new byte[64 * 1024];
        var received = await socket.ReceiveAsync(buffer, deadline.Token);
        var wire = JsonSerializer.Deserialize<AgentWireMessage>(
            Encoding.UTF8.GetString(buffer, 0, received.Count),
            new JsonSerializerOptions(JsonSerializerDefaults.Web)
        )!;
        Assert.Equal(ServerCommandType.SendWhisper, wire.Command!.CommandType);
        Assert.Equal(Pilot, wire.Command.SteamId);
        Assert.Equal("Your ship is at home.", wire.Command.Text);
        await socket.SendAsync(
            AgentConnection.SerializeMessage(
                new AgentWireMessage
                {
                    Kind = WireMessageKind.CommandResult,
                    CommandResult = new()
                    {
                        CommandId = wire.Command.CommandId,
                        AgentId = "alpha:1",
                        Success = true,
                    },
                }
            ),
            WebSocketMessageType.Text,
            true,
            deadline.Token
        );
        await handler.Replied.Task.WaitAsync(deadline.Token);
        await app.StopAsync(deadline.Token);
    }
}
