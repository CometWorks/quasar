using Discord;
using Discord.WebSocket;
using Magnetar.Protocol.Model;
using Magnetar.Protocol.Transport;
using Quasar.Models;
using Quasar.Services.Analytics;

namespace Quasar.Services.Discord;

public sealed class DiscordCommandDispatcher
{
    private readonly AgentRegistry _registry;
    private readonly DedicatedServerSupervisor _supervisor;
    private readonly DedicatedServerInstanceCatalog _instanceCatalog;
    private readonly ILogger<DiscordCommandDispatcher> _logger;

    public DiscordCommandDispatcher(
        AgentRegistry registry,
        DedicatedServerSupervisor supervisor,
        DedicatedServerInstanceCatalog instanceCatalog,
        ILogger<DiscordCommandDispatcher> logger)
    {
        _registry = registry;
        _supervisor = supervisor;
        _instanceCatalog = instanceCatalog;
        _logger = logger;
    }

    public async Task DispatchAsync(
        DiscordInstanceOptions instanceOptions,
        string verb,
        string args,
        SocketMessage message,
        CancellationToken cancellationToken = default)
    {
        try
        {
            switch (verb)
            {
                case "chat":
                    if (string.IsNullOrWhiteSpace(args))
                    {
                        await ReplyAsync(message, "Usage: `chat <text>`");
                        return;
                    }

                    await SendAgentCommandAsync(instanceOptions.InstanceId, ServerCommandType.SendChat, text: args, cancellationToken: cancellationToken);
                    await ReplyAsync(message, "Chat sent.");
                    return;

                case "save":
                    await SendAgentCommandAsync(instanceOptions.InstanceId, ServerCommandType.SaveWorld, cancellationToken: cancellationToken);
                    await ReplyAsync(message, "Save requested.");
                    return;

                case "stop":
                    await _supervisor.StopInstanceAsync(instanceOptions.InstanceId, cancellationToken);
                    await ReplyAsync(message, "Stop requested.");
                    return;

                case "start":
                    await _supervisor.StartInstanceAsync(instanceOptions.InstanceId, cancellationToken);
                    await ReplyAsync(message, "Start requested.");
                    return;

                case "restart":
                    await _supervisor.RestartInstanceAsync(instanceOptions.InstanceId, cancellationToken);
                    await ReplyAsync(message, "Restart requested.");
                    return;

                case "kick":
                    await DispatchSteamIdCommandAsync(message, instanceOptions.InstanceId, args, ServerCommandType.KickPlayer, "Kick requested.", cancellationToken);
                    return;

                case "ban":
                    await DispatchSteamIdCommandAsync(message, instanceOptions.InstanceId, args, ServerCommandType.BanPlayer, "Ban requested.", cancellationToken);
                    return;

                case "unban":
                    await DispatchSteamIdCommandAsync(message, instanceOptions.InstanceId, args, ServerCommandType.UnbanPlayer, "Unban requested.", cancellationToken);
                    return;

                case "promote":
                    await DispatchSteamIdCommandAsync(message, instanceOptions.InstanceId, args, ServerCommandType.PromotePlayer, "Promote requested.", cancellationToken);
                    return;

                case "demote":
                    await DispatchSteamIdCommandAsync(message, instanceOptions.InstanceId, args, ServerCommandType.DemotePlayer, "Demote requested.", cancellationToken);
                    return;

                case "status":
                    await message.Channel.SendMessageAsync(embed: BuildStatusEmbed(instanceOptions.InstanceId).Build());
                    return;

                case "help":
                    await message.Channel.SendMessageAsync(embed: BuildHelpEmbed(instanceOptions).Build());
                    return;

                default:
                    await ReplyAsync(message, $"Unknown command `{verb}`.");
                    await message.Channel.SendMessageAsync(embed: BuildHelpEmbed(instanceOptions).Build());
                    return;
            }
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Discord command {Verb} failed for instance {InstanceId}", verb, instanceOptions.InstanceId);
            await ReplyAsync(message, $"Error: {exception.Message}");
        }
    }

    public async Task RelayChatAsync(
        DiscordInstanceOptions instanceOptions,
        string text,
        SocketMessage message,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(text))
                return;

            await SendAgentCommandAsync(instanceOptions.InstanceId, ServerCommandType.SendChat, text: text.Trim(), cancellationToken: cancellationToken);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Discord chat relay failed for instance {InstanceId}", instanceOptions.InstanceId);
            await ReplyAsync(message, $"Error: {exception.Message}");
        }
    }

    private async Task DispatchSteamIdCommandAsync(
        SocketMessage message,
        string instanceId,
        string args,
        ServerCommandType commandType,
        string successReply,
        CancellationToken cancellationToken)
    {
        if (!long.TryParse(args?.Trim(), out var steamId))
        {
            await ReplyAsync(message, $"Usage: `{ResolveCommandName(commandType)} <steamId>`");
            return;
        }

        await SendAgentCommandAsync(instanceId, commandType, steamId: steamId, cancellationToken: cancellationToken);
        await ReplyAsync(message, successReply);
    }

    private async Task SendAgentCommandAsync(
        string instanceId,
        ServerCommandType commandType,
        string text = "",
        long? steamId = null,
        CancellationToken cancellationToken = default)
    {
        var agent = ResolveConnectedAgent(instanceId);
        if (agent is null)
            throw new InvalidOperationException("Instance not connected.");

        await _registry.SendCommandAsync(new ServerCommandEnvelope
        {
            InstanceId = instanceId,
            AgentId = agent.AgentId,
            ServerId = agent.ServerKey,
            CommandType = commandType,
            Text = text,
            SteamId = steamId,
            IssuedAtUtc = DateTimeOffset.UtcNow,
        }, cancellationToken);
    }

    private AgentRuntimeState? ResolveConnectedAgent(string instanceId)
    {
        return _registry.GetAgents().FirstOrDefault(agent =>
            agent.IsConnected &&
            string.Equals(agent.InstanceKey, instanceId, StringComparison.OrdinalIgnoreCase));
    }

    private EmbedBuilder BuildStatusEmbed(string instanceId)
    {
        var definition = _instanceCatalog.GetInstance(instanceId);
        var runtime = _supervisor.GetSnapshots()
            .FirstOrDefault(snapshot => string.Equals(snapshot.InstanceId, instanceId, StringComparison.OrdinalIgnoreCase));
        var agent = _registry.GetAgents().FirstOrDefault(item =>
            string.Equals(item.InstanceKey, instanceId, StringComparison.OrdinalIgnoreCase));
        var metrics = agent?.Snapshot?.Metrics;

        var title = string.IsNullOrWhiteSpace(definition?.Name)
            ? runtime?.Name ?? instanceId
            : definition.Name;

        var builder = new EmbedBuilder()
            .WithTitle($"{title} status")
            .WithColor(agent?.IsConnected == true ? Color.Green : Color.Orange)
            .WithTimestamp(DateTimeOffset.UtcNow)
            .AddField("Instance", $"`{instanceId}`", inline: false)
            .AddField("Goal", runtime?.GoalState.ToString() ?? definition?.GoalState.ToString() ?? "Unknown", inline: true)
            .AddField("State", runtime?.State.ToString() ?? "Unknown", inline: true)
            .AddField("Agent", agent?.IsConnected == true ? "Connected" : "Disconnected", inline: true)
            .AddField("Health", string.IsNullOrWhiteSpace(runtime?.HealthSummary)
                ? runtime?.HealthState.ToString() ?? "Unknown"
                : $"{runtime.HealthState}: {runtime.HealthSummary}", inline: false);

        if (runtime?.ProcessId is not null)
            builder.AddField("Process", runtime.ProcessId.Value.ToString(), inline: true);

        if (!string.IsNullOrWhiteSpace(agent?.WorldDisplayName))
            builder.AddField("World", agent.WorldDisplayName, inline: true);

        builder.AddField("Uptime", FormatUptime(runtime, metrics), inline: true);

        if (metrics is not null)
        {
            builder
                .AddField("Players", $"{metrics.PlayersOnline}/{metrics.MaxPlayers}", inline: true)
                .AddField("SimSpeed", metrics.SimSpeed.ToString("0.000"), inline: true)
                .AddField("CPU", $"{metrics.ServerCpuLoadPercent:0.0}%", inline: true)
                .AddField("Memory", metrics.MemoryWorkingSetMb is > 0 ? $"{metrics.MemoryWorkingSetMb.Value} MB" : "n/a", inline: true)
                .AddField("PCU", $"{metrics.UsedPcu}/{metrics.TotalPcu}", inline: true)
                .AddField("Grids", metrics.ActiveGridCount?.ToString() ?? "n/a", inline: true)
                .AddField("Entities", metrics.ActiveEntityCount?.ToString() ?? "n/a", inline: true);
        }

        if (!string.IsNullOrWhiteSpace(runtime?.LastMessage))
            builder.AddField("Last Message", runtime.LastMessage, inline: false);

        return builder;
    }

    private static string FormatUptime(DedicatedServerInstanceRuntimeSnapshot? runtime, ServerMetrics? metrics)
    {
        if (runtime?.StartedAtUtc is not null)
            return FormatDuration(DateTimeOffset.UtcNow - runtime.StartedAtUtc.Value);

        if (metrics?.UptimeSeconds is > 0)
            return FormatDuration(TimeSpan.FromSeconds(metrics.UptimeSeconds));

        return "n/a";
    }

    private EmbedBuilder BuildHelpEmbed(DiscordInstanceOptions instanceOptions)
    {
        var prefix = string.IsNullOrWhiteSpace(instanceOptions.CommandPrefix) ? "!" : instanceOptions.CommandPrefix;

        return new EmbedBuilder()
            .WithTitle("Quasar Discord Commands")
            .WithColor(Color.Blue)
            .WithDescription(string.Join('\n', new[]
            {
                $"`{prefix} help`",
                $"`{prefix} status`",
                $"`{prefix} chat <text>`",
                $"`{prefix} save`",
                $"`{prefix} start`",
                $"`{prefix} stop`",
                $"`{prefix} restart`",
                $"`{prefix} kick <steamId>`",
                $"`{prefix} ban <steamId>`",
                $"`{prefix} unban <steamId>`",
                $"`{prefix} promote <steamId>`",
                $"`{prefix} demote <steamId>`",
            }));
    }

    private static string ResolveCommandName(ServerCommandType commandType)
    {
        return commandType switch
        {
            ServerCommandType.KickPlayer => "kick",
            ServerCommandType.BanPlayer => "ban",
            ServerCommandType.UnbanPlayer => "unban",
            ServerCommandType.PromotePlayer => "promote",
            ServerCommandType.DemotePlayer => "demote",
            _ => commandType.ToString(),
        };
    }

    private static string FormatDuration(TimeSpan duration)
    {
        duration = duration < TimeSpan.Zero ? TimeSpan.Zero : duration;

        if (duration.TotalDays >= 1)
            return $"{(int)duration.TotalDays}d {duration.Hours}h {duration.Minutes}m";

        if (duration.TotalHours >= 1)
            return $"{(int)duration.TotalHours}h {duration.Minutes}m";

        if (duration.TotalMinutes >= 1)
            return $"{(int)duration.TotalMinutes}m {duration.Seconds}s";

        return $"{Math.Max(0, duration.Seconds)}s";
    }

    private static Task ReplyAsync(SocketMessage message, string text)
    {
        return message.Channel.SendMessageAsync(text: text);
    }
}
