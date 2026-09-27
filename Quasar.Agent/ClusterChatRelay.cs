using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Magnetar.Protocol.Transport;
using Newtonsoft.Json;
using PluginSdk.Clustering;
using Sandbox.Engine.Multiplayer;
using Sandbox.Game.World;

namespace Quasar.Agent
{
    // Transport and incarnation fencing belong to PluginSdk, including across Hosts.
    internal sealed class ClusterChatRelay : IDisposable
    {
        private const string Topic = "discord/chat";
        private volatile PluginClusterClient _client;
        private IPluginClusterProvider _provider;
        private IDisposable _registration;
        private IDisposable _preparation;
        private readonly Dictionary<Guid, (string Payload, byte[] Reply, DateTimeOffset At)> _seen = new();

        public bool IsReady => _registration != null && _preparation != null && _client?.Context.Available == true
            && PluginCluster.Current is IPluginClusterBroadcastProvider && PluginCluster.Current is IPluginClusterViewProvider;

        public void Update()
        {
            if (ReferenceEquals(_provider, PluginCluster.Current)) return;
            _registration?.Dispose();
            _preparation?.Dispose();
            _registration = null;
            _preparation = null;
            _provider = PluginCluster.Current;
            if (_provider == null) return;
            _client = PluginCluster.ForPlugin("quasar-agent");
            _registration = _client.RegisterHandler(Topic, ReceiveAsync);
            _preparation = _client.RegisterHandler(Topic + "/prepare", PrepareAsync);
        }

        public static bool Handles(ServerCommandType type) => type is
            ServerCommandType.SendChat or ServerCommandType.SendWhisper or ServerCommandType.SendFactionChat;

        public async Task<ServerCommandResult> SendAsync(ServerCommandEnvelope command, CancellationToken token)
        {
            var result = new ServerCommandResult { CommandId = command.CommandId, UniqueName = command.UniqueName,
                AgentId = command.AgentId, ServerId = command.ServerId };
            var client = _client;
            if (client == null || !client.Context.Available || !Guid.TryParse(command.CommandId, out var id))
            { result.Message = "Cluster chat service is unavailable."; return result; }
            try
            {
                var payload = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(command));
                var preparation = await client.RequestAsync(new PluginTarget { Kind = PluginTargetKind.WorldAuthority },
                    Topic + "/prepare", payload, id, TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
                if (preparation.Code != PluginResultCode.Success || preparation.Payload == null)
                { result.Message = $"World Authority could not prepare chat ({preparation.Code})."; return result; }
                var plan = JsonConvert.DeserializeObject<DeliveryPlan>(Encoding.UTF8.GetString(preparation.Payload));
                if (plan?.Error != null || plan?.Recipients == null)
                { result.Message = plan?.Error ?? "Invalid chat preparation."; return result; }
                var broadcast = await client.BroadcastAsync(Topic, preparation.Payload, id, TimeSpan.FromSeconds(15),
                    includeWorldAuthority: true, cancellationToken: token).ConfigureAwait(false);
                var replies = broadcast.Nodes.Values.Where(r => r.Code == PluginResultCode.Success && r.Payload != null)
                    .Select(r => JsonConvert.DeserializeObject<Delivery>(Encoding.UTF8.GetString(r.Payload))).ToArray();
                result.Success = broadcast.Code == PluginResultCode.Success && broadcast.Nodes.Count > 0
                    && replies.Length == broadcast.Nodes.Count && replies.All(r => r != null && r.Error == null)
                    && replies.Sum(r => r.Recipients) == plan.Recipients.Length
                    && (command.CommandType == ServerCommandType.SendChat || plan.Recipients.Length > 0);
                result.Message = result.Success ? $"Chat sent to {replies.Sum(r => r.Recipients)} player(s) across {broadcast.Nodes.Count} node(s)."
                    : $"Cluster chat was not fully delivered ({broadcast.Code}). Some recipients may have received it. "
                        + (replies.FirstOrDefault(r => r?.Error != null)?.Error ?? "A node or recipient is unavailable; check the cluster before retrying.");
                return result;
            }
            catch (JsonException)
            {
                result.Success = false;
                result.Message = "Invalid cluster chat response. Delivery may be partial; check the cluster before retrying.";
                return result;
            }
        }

        private Task<byte[]> PrepareAsync(PluginMessage message)
        {
            var command = JsonConvert.DeserializeObject<ServerCommandEnvelope>(Encoding.UTF8.GetString(message.Payload));
            var plan = new DeliveryPlan { Command = command };
            var session = MySession.Static;
            var players = _client.OnlinePlayers();
            if (!Valid(command, message.OperationId) || _client.Context.Role != "WorldAuthority"
                || !_client.Context.Available || session == null || !session.Ready || players == null)
                plan.Error = "World Authority player view or chat request is unavailable.";
            else
            {
                var faction = command.CommandType == ServerCommandType.SendFactionChat
                    ? session.Factions.TryGetFactionByTag(command.Payload) : null;
                if (command.CommandType == ServerCommandType.SendFactionChat && faction == null)
                    plan.Error = "Faction was not found.";
                else
                    plan.Recipients = players.Where(p => p.SteamId > 0 && p.SerialId == 0 && p.IdentityId != 0 && !string.IsNullOrWhiteSpace(p.Node)
                        && (command.CommandType != ServerCommandType.SendWhisper || p.SteamId == (ulong)command.SteamId.Value)
                        && (faction == null || faction.IsMember(p.IdentityId)))
                        .GroupBy(p => p.SteamId).Select(g => new Recipient { SteamId = g.Key, Node = g.First().Node }).ToArray();
            }
            return Task.FromResult(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(plan)));
        }

        private static bool Valid(ServerCommandEnvelope command, Guid id) => command != null && Handles(command.CommandType)
            && !string.IsNullOrWhiteSpace(command.Text) && command.Text.Length <= 4096
            && Guid.TryParse(command.CommandId, out var commandId) && commandId == id
            && (command.CommandType != ServerCommandType.SendWhisper || command.SteamId is > 0)
            && DateTimeOffset.UtcNow - command.IssuedAtUtc <= TimeSpan.FromSeconds(30)
            && command.IssuedAtUtc - DateTimeOffset.UtcNow <= TimeSpan.FromSeconds(5);

        // SDK handlers run on the game thread and are fenced against the recipient incarnation.
        private Task<byte[]> ReceiveAsync(PluginMessage message)
        {
            var now = DateTimeOffset.UtcNow;
            var json = Encoding.UTF8.GetString(message.Payload ?? Array.Empty<byte>());
            var plan = JsonConvert.DeserializeObject<DeliveryPlan>(json);
            var command = plan?.Command;
            if (!Valid(command, message.OperationId) || plan?.Recipients == null || plan.Error != null)
                return Reply(new Delivery { Error = "Invalid or expired chat request." });
            var id = message.OperationId;
            foreach (var key in _seen.Where(p => now - p.Value.At > TimeSpan.FromMinutes(1)).Select(p => p.Key).ToArray())
                _seen.Remove(key);
            if (_seen.TryGetValue(id, out var previous))
                return json == previous.Payload ? Task.FromResult(previous.Reply) : Reply(new Delivery { Error = "Chat operation id was reused." });
            if (_seen.Count >= 2048) return Reply(new Delivery { Error = "Chat service is busy." });
            var session = MySession.Static;
            var players = _client.OnlinePlayers();
            if (session == null || !session.Ready || players == null || !_client.Context.Available
                || MyMultiplayer.Static is not MyDedicatedServerBase server)
                return Reply(new Delivery { Error = "Cluster player view is unavailable." });
            var recipients = plan.Recipients.Where(p => p.Node == _client.Context.Node).Select(p => p.SteamId).Distinct().ToArray();
            // A handover may have changed the attachment since WA preparation. Fail visibly instead of
            // sending to both the old and new nodes or claiming delivery to an absent player.
            if (recipients.Any(id => !server.Members.Contains(id) || !players.Any(p => p.SteamId == id && p.Node == _client.Context.Node)))
                return Reply(new Delivery { Error = "A chat recipient moved or disconnected; refresh the cluster and retry." });
            var delivery = new Delivery { Error = "Chat delivery was interrupted." };
            var reply = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(delivery));
            _seen[id] = (json, reply, now);
            try
            {
                foreach (var recipient in recipients)
                {
                    // Direct delivery avoids re-entering the player's WA admission/replication path.
                    server.SendChatMessageToPlayer(command.CommandType == ServerCommandType.SendFactionChat
                        ? $"[Faction {command.Payload}] {command.Text}" : command.Text, recipient);
                    delivery.Recipients++;
                }
                delivery.Error = null;
            }
            catch { /* Preserve partial delivery; a repeated operation must not send it twice. */ }
            reply = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(delivery));
            _seen[id] = (json, reply, now);
            return Task.FromResult(reply);
        }

        private static Task<byte[]> Reply(Delivery reply) => Task.FromResult(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(reply)));
        public void Dispose()
        {
            _registration?.Dispose(); _preparation?.Dispose();
            _registration = null; _preparation = null; _provider = null; _client = null;
        }
        private sealed class Recipient { public ulong SteamId { get; set; } public string Node { get; set; } }
        private sealed class DeliveryPlan
        {
            public ServerCommandEnvelope Command { get; set; }
            public Recipient[] Recipients { get; set; }
            public string Error { get; set; }
        }
        private sealed class Delivery { public int Recipients { get; set; } public string Error { get; set; } }
    }
}
