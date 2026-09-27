using System.Collections.Concurrent;
using Discord;
using Discord.WebSocket;
using Magnetar.Protocol.Model;
using Magnetar.Protocol.Transport;
using Quasar.Models;
using Admin = CometWorks.ClusterGateway.AdminContract.V1;

namespace Quasar.Services.Discord;

/// <summary>Discord's cluster boundary: Registry identity, Gateway lifecycle and fenced SDK chat via Agent.</summary>
public sealed class DiscordClusterBridge(
    ClusterCatalog catalog, ClusterGatewayClient gateway, ClusterCommandService commands,
    AgentRegistry registry, DiscordChatRelayService chat, DiscordRateLimiter rateLimiter,
    ILogger<DiscordClusterBridge> logger)
{
    private readonly ConcurrentDictionary<string, Observation> _observations = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ChatCursor> _chatCursors = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _errors = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _started = DateTimeOffset.UtcNow;

    internal IEnumerable<DiscordPresenceServer> PresenceServers()
    {
        foreach (var cluster in catalog.GetClusters())
        {
            _observations.TryGetValue(cluster.UniqueName, out var observation);
            var status = observation != null && DateTimeOffset.UtcNow - observation.At <= TimeSpan.FromSeconds(15)
                ? observation.Status : null;
            var stopped = cluster.GoalState == DedicatedServerGoalState.Off && cluster.ShutdownProof?.LifecycleId == cluster.GetLifecycleId();
            yield return new("cluster:" + cluster.UniqueName, cluster.UniqueName,
                status?.Phase is Admin.ClusterPhase.Serving or Admin.ClusterPhase.Degraded,
                status?.Counts.ConnectedClients ?? (stopped ? 0 : (int?)null),
                status?.Phase.ToString() ?? (stopped ? "Stopped" : "Unavailable"),
                status?.Health == Admin.AdminHealth.Unhealthy,
                status?.Health == Admin.AdminHealth.Warning || status == null && !stopped);
        }
    }

    public void Reset()
    {
        _observations.Clear();
        _chatCursors.Clear();
        _errors.Clear();
        _started = DateTimeOffset.UtcNow;
    }

    public IReadOnlyList<AgentRuntimeState> GetAgents(DiscordServerOptions target)
    {
        if (!target.IsCluster)
            return registry.GetAgents().Where(a => !a.IsCluster && a.IsConnected && a.Snapshot != null
                && string.Equals(a.UniqueNameKey, target.UniqueName, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (!_observations.TryGetValue(target.UniqueName, out var observed)
            || DateTimeOffset.UtcNow - observed.At > TimeSpan.FromSeconds(15)) return [];
        return MatchAgents(observed.Status, registry.GetAgents());
    }

    internal static AgentRuntimeState[] MatchAgents(Admin.ClusterStatus status, IReadOnlyList<AgentRuntimeState> agents) =>
        status.Nodes.Where(n => n.State == Admin.NodeState.Active && n.LeaseFresh)
            .Select(n => ClusterFleetService.MatchAgent(status.ClusterId, n, agents))
            .Where(a => a is { IsConnected: true, Snapshot: not null }
                && DateTimeOffset.UtcNow - a.LastSnapshotReceivedUtc < TimeSpan.FromSeconds(15)).Cast<AgentRuntimeState>().ToArray();

    public async Task PollAsync(DiscordSocketClient client, DiscordOptions options, CancellationToken token)
    {
        // Presence also works before a cluster has chat/channel bindings. Those observations must not enable relays.
        var targets = options.Servers.Where(s => s.IsCluster).Concat(catalog.GetClusters()
            .Where(c => options.Presence.Includes("cluster:" + c.UniqueName))
            .Select(c => new DiscordServerOptions { UniqueName = c.UniqueName, IsCluster = true,
                EnableChatRelay = false, EnablePlayerNotifications = false, EnableServerNotifications = false }));
        foreach (var settings in targets.DistinctBy(s => s.TargetKey, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var cluster = RequireCluster(settings);
                if (cluster.GoalState == DedicatedServerGoalState.Off && cluster.ShutdownProof?.LifecycleId == cluster.GetLifecycleId())
                {
                    if (_observations.TryRemove(settings.UniqueName, out var stopped) && stopped.Status.Phase != Admin.ClusterPhase.Down
                        && settings.EnableServerNotifications)
                        await SendAsync(client, settings.StatusChannelId ?? settings.ChatRelayChannelId,
                            $"[{Format.Sanitize(settings.UniqueName)}] Cluster: Down.", token);
                    continue;
                }
                var status = (await gateway.GetStatusAsync(cluster, token)).Data;
                ValidateIdentity(cluster, status);
                var clients = settings.EnablePlayerNotifications || settings.EnableChatRelay
                    ? (await gateway.GetClientsAsync(cluster, token)).Data : Array.Empty<Admin.ClientSummary>();
                _observations.TryGetValue(settings.UniqueName, out var previous);
                var current = new Observation(DateTimeOffset.UtcNow, status, clients);
                _observations[settings.UniqueName] = current;
                if (previous != null)
                    foreach (var text in StatusMessages(settings, previous.Status, previous.Clients, status, clients))
                        await SendAsync(client, settings.StatusChannelId ?? settings.ChatRelayChannelId, text, token);
                if (settings.EnableChatRelay)
                {
                    if (!_chatCursors.TryGetValue(settings.UniqueName, out var cursor))
                        _chatCursors[settings.UniqueName] = cursor = new ChatCursor();
                    var history = (await gateway.GetChatAsync(cluster, cursor.Next, 500, token)).Data;
                    // Sequences restart with the WA incarnation. Fetch from zero before advancing again.
                    if (cursor.Epoch != 0 && cursor.Epoch != history.WorldAuthorityEpoch)
                        history = (await gateway.GetChatAsync(cluster, 0, 500, token)).Data;
                    foreach (var message in cursor.Observe(history, _started))
                    {
                        // The history contract has no private recipient/faction metadata. Never treat it as public.
                        if (message.Channel != 0) continue;
                        var author = clients.FirstOrDefault(p => p.ClientId == message.Author)?.PlayerName ?? message.Author.ToString();
                        chat.EnqueueClusterGlobal(client, settings, author, message.Text, token);
                    }
                    var authority = GetAgents(settings).SingleOrDefault(a => a.ClusterNodeId == status.WorldAuthority.Node
                        && a.ClusterEpoch == status.WorldAuthority.Epoch);
                    if (authority?.Snapshot != null)
                        chat.RelayPrivateClusterChat(client, settings, authority.Snapshot.RecentChat, authority.ClusterEpoch, token);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception error)
            {
                _observations.TryRemove(settings.UniqueName, out _);
                if (!_errors.TryGetValue(settings.UniqueName, out var last) || DateTimeOffset.UtcNow - last > TimeSpan.FromMinutes(1))
                {
                    _errors[settings.UniqueName] = DateTimeOffset.UtcNow;
                    logger.LogWarning(error, "Discord cluster observation failed for {Cluster}", settings.UniqueName);
                }
            }
        }
    }

    public async Task SendCommandAsync(DiscordServerOptions settings, ServerCommandType type, string text = "",
        long? steamId = null, string payload = "", CancellationToken token = default)
    {
        if (type is not (ServerCommandType.SendChat or ServerCommandType.SendWhisper or ServerCommandType.SendFactionChat
            or ServerCommandType.PromotePlayer or ServerCommandType.DemotePlayer))
            throw new InvalidOperationException("Use the Gateway for cluster lifecycle and admission commands.");
        var cluster = RequireCluster(settings);
        var status = (await gateway.GetStatusAsync(cluster, token)).Data;
        ValidateIdentity(cluster, status);
        var matched = MatchAgents(status, registry.GetAgents());
        if (type is ServerCommandType.SendChat or ServerCommandType.SendWhisper or ServerCommandType.SendFactionChat
            && (matched.Length != status.Nodes.Count(n => n.State == Admin.NodeState.Active)
                || matched.Any(a => a.Snapshot?.ClusterChatReady != true)))
            throw new InvalidOperationException("Cluster chat requires a connected, current Quasar Agent with SDK chat support on every active node.");
        var agent = matched.SingleOrDefault(a =>
            a.ClusterNodeId == status.WorldAuthority.Node && a.ClusterEpoch == status.WorldAuthority.Epoch)
            ?? throw new InvalidOperationException("The current World Authority Agent is unavailable.");
        var result = await registry.SendCommandAndWaitAsync(new ServerCommandEnvelope
        {
            UniqueName = agent.UniqueNameKey, AgentId = agent.AgentId, ServerId = agent.ServerKey,
            CommandType = type, Text = text, SteamId = steamId, Payload = payload,
        }, TimeSpan.FromSeconds(25), token);
        if (!result.Success) throw new InvalidOperationException(result.Message);
    }

    public async Task<long> ResolvePlayerAsync(DiscordServerOptions settings, string recipient, CancellationToken token)
    {
        var clients = (await gateway.GetClientsAsync(RequireCluster(settings), token)).Data;
        var matches = clients.Where(p => p.ClientId > 0 && (p.ClientId.ToString() == recipient.Trim()
            || string.Equals(p.PlayerName, recipient.Trim(), StringComparison.OrdinalIgnoreCase))).ToArray();
        return matches.Length switch
        {
            1 => checked((long)matches[0].ClientId),
            0 => throw new InvalidOperationException("Online player was not found in the cluster."),
            _ => throw new InvalidOperationException("Player name is ambiguous; use the Steam ID."),
        };
    }

    public async Task<string> ExecuteAsync(DiscordServerOptions settings, string verb, string args, string key,
        string actor, CancellationToken token)
    {
        var cluster = RequireCluster(settings);
        if (verb is "start" or "stop" or "restart")
        {
            var goal = await commands.SetGoalAsync(cluster.UniqueName, new ClusterGoalRequest(
                verb == "start" ? DedicatedServerGoalState.On : DedicatedServerGoalState.Off), key + ":goal", actor, token);
            if (goal.Error != null) throw new InvalidOperationException(goal.Error.Message);
            if (verb == "restart")
            {
                var stopped = RequireCluster(settings);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromMinutes(10));
                using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
                while (true)
                {
                    var current = RequireCluster(settings);
                    if (current.GoalState != DedicatedServerGoalState.Off || current.UpdatedAtUtc != stopped.UpdatedAtUtc)
                        throw new InvalidOperationException("Cluster settings changed during restart; automatic start was cancelled.");
                    if (await commands.CanStartAsync(current, timeout.Token)) break;
                    await timer.WaitForNextTickAsync(timeout.Token);
                }
                var start = await commands.SetGoalAsync(cluster.UniqueName, new ClusterGoalRequest(DedicatedServerGoalState.On),
                    key + ":start", actor, timeout.Token);
                if (start.Error != null) throw new InvalidOperationException(start.Error.Message);
            }
            return $"Cluster {verb} requested.";
        }
        var action = verb == "save" ? "save-all" : verb;
        if (verb is not ("save" or "kick" or "ban" or "unban")) throw new InvalidOperationException("Unsupported cluster command.");
        var operation = await commands.SubmitAsync(cluster.UniqueName,
            ClusterAdminCommand.Create(action, target: verb == "save" ? null : args.Trim()), key, actor, token);
        if (operation.Error != null) throw new InvalidOperationException(operation.Error.Message);
        return $"Cluster {verb} submitted. Operation: {operation.OperationId}";
    }

    public async Task<EmbedBuilder> BuildStatusAsync(DiscordServerOptions settings, CancellationToken token)
    {
        var cluster = RequireCluster(settings);
        var status = (await gateway.GetStatusAsync(cluster, token)).Data;
        ValidateIdentity(cluster, status);
        return new EmbedBuilder().WithTitle($"{(string.IsNullOrWhiteSpace(cluster.DisplayName) ? cluster.UniqueName : cluster.DisplayName)} cluster")
            .WithColor(status.Health == Admin.AdminHealth.Healthy ? Color.Green : Color.Orange)
            .WithTimestamp(status.ObservedAt)
            .AddField("State", status.Phase, true).AddField("Goal", cluster.GoalState, true)
            .AddField("Players", status.Counts.ConnectedClients, true)
            .AddField("Nodes", status.Nodes.Count(n => n.State == Admin.NodeState.Active), true)
            .AddField("Hosts", status.Nodes.Select(n => n.Host).Where(h => h != null).Distinct().Count(), true)
            .AddField("World Authority", status.WorldAuthority.Node ?? "Unavailable", true);
    }

    private ClusterDefinition RequireCluster(DiscordServerOptions settings) => catalog.GetCluster(settings.UniqueName)
        ?? throw new InvalidOperationException($"Cluster '{settings.UniqueName}' no longer exists.");
    private static void ValidateIdentity(ClusterDefinition cluster, Admin.ClusterStatus status)
    {
        if (!string.Equals(cluster.UniqueName, status.ClusterId, StringComparison.Ordinal))
            throw new InvalidOperationException("Gateway returned a different cluster identity.");
    }

    internal static IReadOnlyList<string> StatusMessages(DiscordServerOptions settings, Admin.ClusterStatus previous,
        Admin.ClientSummary[] oldClients, Admin.ClusterStatus current, Admin.ClientSummary[] clients)
    {
        var messages = new List<string>();
        string prefix = $"[{Format.Sanitize(settings.UniqueName)}] ";
        if (settings.EnableServerNotifications && previous.Phase != current.Phase)
            messages.Add(prefix + $"Cluster: {current.Phase}.");
        if (settings.EnablePlayerNotifications)
        {
            var oldIds = oldClients.Select(p => p.ClientId).ToHashSet();
            var ids = clients.Select(p => p.ClientId).ToHashSet();
            foreach (var player in clients.Where(p => !oldIds.Contains(p.ClientId)))
                messages.Add(prefix + $"🚀 **{Format.Sanitize(player.PlayerName ?? player.ClientId.ToString())}** connected.");
            foreach (var player in oldClients.Where(p => !ids.Contains(p.ClientId)))
                messages.Add(prefix + $"☄️ **{Format.Sanitize(player.PlayerName ?? player.ClientId.ToString())}** disconnected.");
        }
        return messages;
    }

    private async Task SendAsync(DiscordSocketClient client, ulong? channelId, string text, CancellationToken token)
    {
        if (channelId.HasValue && client.GetChannel(channelId.Value) is IMessageChannel channel)
            await rateLimiter.RunAsync(channelId.Value, () => channel.SendMessageAsync(text: text,
                allowedMentions: AllowedMentions.None), token);
    }

    internal sealed class ChatCursor
    {
        public long Epoch { get; private set; }
        public long Next { get; private set; }
        public IReadOnlyList<Admin.ChatMessage> Observe(Admin.ChatHistory history, DateTimeOffset started)
        {
            if (history.WorldAuthorityEpoch <= 0) return [];
            if (Epoch != history.WorldAuthorityEpoch) { Epoch = history.WorldAuthorityEpoch; Next = 0; }
            var fresh = history.Messages.Where(m => m.Seq > Next && m.At > started).OrderBy(m => m.Seq).ToArray();
            Next = Math.Max(Next, history.NextCursor);
            return fresh;
        }
    }
    private sealed record Observation(DateTimeOffset At, Admin.ClusterStatus Status, Admin.ClientSummary[] Clients);
}
