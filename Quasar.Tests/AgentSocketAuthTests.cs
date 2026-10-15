using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Magnetar.Protocol.Model;
using Magnetar.Protocol.Transport;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Quasar.Host.Contract.V1;
using Quasar.Services;
using Quasar.Services.PluginSdk;
using Xunit;

namespace Quasar.Tests;

public sealed class AgentSocketAuthTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "agent-socket-auth-" + Guid.NewGuid().ToString("N"));
    private readonly ClusterCredentialStore _credentials;
    private readonly ClusterHostCatalog _hosts;
    private readonly AgentSocketAuthenticator _authenticator;
    private readonly AgentRegistry _registry = new(null!, null!, null!, null!);

    public AgentSocketAuthTests()
    {
        Directory.CreateDirectory(_root);
        _credentials = new(new EphemeralDataProtectionProvider(), Path.Combine(_root, "credentials.json"));
        _hosts = new(_credentials, Path.Combine(_root, "hosts"));
        _authenticator = new(_credentials, _hosts);
    }

    public void Dispose() => Directory.Delete(_root, true);

    private static AgentHello Standalone(string name, string agentId = "agent-a") => new() { UniqueName = name, AgentId = agentId };

    private static AgentHello ClusterNode(string hostId, string agentId = "node-a") => new()
        { AgentId = agentId, HostId = hostId, ClusterMode = true, ClusterId = "fleet", ClusterSlot = "slot-a" };

    private static Task Sender(AgentWireMessage message, CancellationToken token) => Task.CompletedTask;

    [Fact]
    public void StandaloneTokenIsBoundToItsServer()
    {
        string token = _authenticator.ForServer("alpha");
        Assert.True(_authenticator.IsValid(Standalone("alpha"), token));
        Assert.False(_authenticator.IsValid(Standalone("beta"), token));
        Assert.False(_authenticator.IsValid(Standalone("alpha"), token.ToUpperInvariant()));
        Assert.False(_authenticator.IsValid(Standalone(""), _authenticator.ForServer("")));
        // A standalone token does not open the cluster path for a Host of the same name.
        Assert.False(_authenticator.IsValid(ClusterNode("alpha"), token));
        // The key survives a Quasar restart, so servers that outlive Quasar can reconnect.
        Assert.Equal(token, new AgentSocketAuthenticator(_credentials, _hosts).ForServer("alpha"));
    }

    [Fact]
    public async Task ClusterTokenIsDerivedFromTheEnrolledHostCredential()
    {
        var host = await _hosts.RegisterAsync("one", "One", "10.0.0.1", 18400, default);
        await _hosts.RegisterAsync("two", "Two", "10.0.0.2", 18400, default);
        string token = AgentSocketToken.ForHost(_credentials.Resolve(host.CredentialReference)!);
        Assert.True(_authenticator.IsValid(ClusterNode("one"), token));
        Assert.False(_authenticator.IsValid(ClusterNode("two"), token));
        Assert.False(_authenticator.IsValid(ClusterNode("missing"), token));
        Assert.False(_authenticator.IsValid(ClusterNode("one"), _credentials.Resolve(host.CredentialReference)!));
    }

    [Fact]
    public void SecondConnectionForALiveServerIsRefusedUntilTheFirstGoesQuiet()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.True(_registry.TryUpsertHello(Standalone("alpha", "alpha:1"), "first", Sender, now.AddSeconds(-30)));
        Assert.False(_registry.TryUpsertHello(Standalone("alpha", "alpha:2"), "second", Sender, now.AddSeconds(-30)));
        Assert.False(_registry.TryUpsertHello(Standalone("ALPHA", "alpha:1"), "second", Sender, now.AddSeconds(-30)));
        Assert.Equal("first", _registry.GetAgents().Single(a => a.IsConnected).ConnectionId);

        // The first socket has been silent past the cut-off: the reconnecting agent replaces it.
        Assert.True(_registry.TryUpsertHello(Standalone("alpha", "alpha:2"), "second", Sender, now.AddSeconds(1)));
        Assert.Equal("second", _registry.GetAgents().Single(a => a.IsConnected).ConnectionId);
        Assert.False(_registry.TryGetUniqueName("first", out _));
    }

    [Fact]
    public void ClusterIncarnationsOfOneSlotMayOverlap()
    {
        var cutoff = DateTimeOffset.UtcNow.AddSeconds(-30);
        Assert.True(_registry.TryUpsertHello(ClusterNode("one", "old"), "old", Sender, cutoff));
        Assert.True(_registry.TryUpsertHello(ClusterNode("one", "new"), "new", Sender, cutoff));
        Assert.False(_registry.TryUpsertHello(ClusterNode("one", "new"), "impostor", Sender, cutoff));
        Assert.Equal(2, _registry.GetAgents().Count(a => a.IsConnected));
    }

    [Fact]
    public async Task SocketRequiresAValidTokenAndOneConnectionPerServer()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(_registry);
        builder.Services.AddSingleton(_authenticator);
        builder.Services.AddSingleton(new PluginConfigService(_registry, NullLogger<PluginConfigService>.Instance, Path.Combine(_root, "configs")));
        builder.Services.AddSingleton<DedicatedServerSupervisor>(_ => null!);
        builder.Services.AddSingleton<PluginLogStream>();
        builder.Services.AddSingleton<AgentSocketHandler>();
        await using var app = builder.Build();
        app.UseWebSockets();
        app.Map("/ws/agent", (HttpContext context, AgentSocketHandler handler) => handler.HandleAsync(context));
        await app.StartAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var url = new UriBuilder(app.Urls.Single()) { Scheme = "ws", Path = "/ws/agent" }.Uri;

        async Task<ClientWebSocket> Connect(string? token, AgentWireMessage first)
        {
            var socket = new ClientWebSocket();
            socket.Options.AddSubProtocol("quasar.agent.v1");
            if (token is not null) socket.Options.SetRequestHeader(AgentSocketToken.HeaderName, token);
            await socket.ConnectAsync(url, deadline.Token);
            var json = JsonSerializer.Serialize(first, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            await socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, deadline.Token);
            return socket;
        }

        async Task<string?> ClosedWith(ClientWebSocket socket)
        {
            var result = await socket.ReceiveAsync(new byte[1024], deadline.Token);
            return result.MessageType == WebSocketMessageType.Close ? socket.CloseStatusDescription : null;
        }

        AgentWireMessage Hello(string name, string agentId) => new() { Kind = WireMessageKind.Hello, Hello = Standalone(name, agentId) };

        // An older agent sends no token: refused before the upgrade.
        var old = await Assert.ThrowsAsync<WebSocketException>(() => Connect(null, Hello("alpha", "alpha:1")));
        Assert.Contains("401", old.Message);

        using (var forged = await Connect(_authenticator.ForServer("beta"), Hello("alpha", "alpha:1")))
            Assert.Equal("Agent token rejected.", await ClosedWith(forged));
        using (var silent = await Connect(_authenticator.ForServer("alpha"), new() { Kind = WireMessageKind.Ping }))
            Assert.Equal("Hello expected.", await ClosedWith(silent));
        Assert.Empty(_registry.GetAgents());

        using var agent = await Connect(_authenticator.ForServer("alpha"), Hello("alpha", "alpha:1"));
        while (_registry.GetAgents().Count == 0) await Task.Delay(10, deadline.Token);
        using (var second = await Connect(_authenticator.ForServer("alpha"), Hello("alpha", "alpha:2")))
            Assert.Equal("This server already has a connected agent.", await ClosedWith(second));

        var registered = _registry.GetAgents().Single();
        Assert.True(registered.IsConnected);
        Assert.Equal("alpha:1", registered.AgentId);
        await app.StopAsync(deadline.Token);
    }
}
