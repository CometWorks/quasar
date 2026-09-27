using System.Text;
using System.Text.Json;
using Magnetar.Protocol.Transport;
using PluginSdk.Clustering;
using Quasar.Agent;
using Xunit;

namespace Quasar.Tests;

[Collection("Cluster identity environment")]
public sealed class ClusterChatRelayTests
{
    [Theory]
    [InlineData(PluginResultCode.Success, null, 2, true)]
    [InlineData(PluginResultCode.Timeout, null, 2, false)]
    [InlineData(PluginResultCode.Success, "Recipient moved", 2, false)]
    [InlineData(PluginResultCode.Success, null, 1, false)]
    public async Task BroadcastRequiresEveryNodeAndEveryPreparedRecipient(PluginResultCode code, string? error, int recipients, bool success)
    {
        var provider = new Provider { Code = code, Error = error, Recipients = recipients };
        Assert.True(PluginCluster.Register(provider));
        try
        {
            PluginCluster.BindOwner("quasar-agent", typeof(ClusterChatRelay).Assembly);
            using var relay = new ClusterChatRelay();
            relay.Update();
            Assert.True(relay.IsReady);
            var command = new ServerCommandEnvelope { CommandType = ServerCommandType.SendChat, Text = "hello" };
            var result = await relay.SendAsync(command, default);
            Assert.Equal(success, result.Success);
            Assert.Equal(PluginTargetKind.WorldAuthority, provider.PrepareTarget!.Kind);
            Assert.Equal(Guid.Parse(command.CommandId), provider.BroadcastId);
            Assert.True(provider.IncludeAuthority);
            Assert.Equal(2, provider.Topics.Count); // preparation + delivery, registered on every node
        }
        finally { PluginCluster.Unregister(provider); }
    }

    [Fact]
    public async Task MissingProviderFailsWithoutLocalChatFallback()
    {
        using var relay = new ClusterChatRelay();
        Assert.False(relay.IsReady);
        var result = await relay.SendAsync(new() { CommandType = ServerCommandType.SendWhisper, SteamId = 123, Text = "hello" }, default);
        Assert.False(result.Success);
        Assert.Contains("unavailable", result.Message);
    }

    private sealed class Provider : IPluginClusterProvider, IPluginClusterBroadcastProvider, IPluginClusterViewProvider
    {
        public IReadOnlyList<PluginPlayerInfo> OnlinePlayers() => [];
        public IReadOnlyList<PluginPlayerPosition> PlayerPositions() => [];
        public PluginEntityPlacement LocateEntity(long id) => new();
        public NodeContext Context => new() { Available = true, ClusterId = "test", Node = "wa", Role = "WorldAuthority", Incarnation = 1 };
        public event Action? ContextChanged { add { } remove { } }
        public PluginResultCode Code { get; init; }
        public string? Error { get; init; }
        public int Recipients { get; init; }
        public PluginTarget? PrepareTarget { get; private set; }
        public Guid BroadcastId { get; private set; }
        public bool IncludeAuthority { get; private set; }
        public List<string> Topics { get; } = [];
        public Task<PluginResult> ExecuteAsync(PluginRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PluginResult> RequestAsync(string plugin, PluginTarget target, string topic, byte[] payload,
            Guid operationId, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Assert.Equal("quasar-agent", plugin);
            PrepareTarget = target;
            return Task.FromResult(new PluginResult { Code = PluginResultCode.Success,
                Payload = JsonSerializer.SerializeToUtf8Bytes(new { Command = JsonSerializer.Deserialize<ServerCommandEnvelope>(payload),
                    Recipients = new[] { new { SteamId = 123, Node = "host-1-node" }, new { SteamId = 456, Node = "host-2-node" } } }) });
        }
        public IDisposable RegisterHandler(string plugin, string topic, Func<PluginMessage, Task<byte[]>> handler)
        { Topics.Add(topic); return new Registration(); }
        public Task<PluginBroadcastResult> BroadcastAsync(string plugin, string topic, byte[] payload, Guid operationId,
            bool includeWorldAuthority, TimeSpan timeout, CancellationToken cancellationToken)
        {
            BroadcastId = operationId;
            IncludeAuthority = includeWorldAuthority;
            return Task.FromResult(new PluginBroadcastResult { Code = Code, Nodes = new Dictionary<string, PluginResult>
            {
                ["host-1-node"] = new() { Code = PluginResultCode.Success, Payload = Encoding.UTF8.GetBytes("{\"Recipients\":1}") },
                ["host-2-node"] = new() { Code = Code, Payload = JsonSerializer.SerializeToUtf8Bytes(new { Recipients = Recipients - 1, Error }) },
                ["wa"] = new() { Code = PluginResultCode.Success, Payload = Encoding.UTF8.GetBytes("{\"Recipients\":0}") },
            } });
        }
        private sealed class Registration : IDisposable { public void Dispose() { } }
    }
}
