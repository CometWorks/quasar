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
    private readonly DedicatedServerCatalog _serverCatalog;
    private readonly DiscordChatRelayService _chatRelayService;
    private readonly ILogger<DiscordCommandDispatcher> _logger;

    public DiscordCommandDispatcher(
        AgentRegistry registry,
        DedicatedServerSupervisor supervisor,
        DedicatedServerCatalog serverCatalog,
        DiscordChatRelayService chatRelayService,
        ILogger<DiscordCommandDispatcher> logger)
    {
        _registry = registry;
        _supervisor = supervisor;
        _serverCatalog = serverCatalog;
        _chatRelayService = chatRelayService;
        _logger = logger;
    }

    public async Task DispatchAsync(
        DiscordServerOptions serverOptions,
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

                    _chatRelayService.TrackDiscordToGameMessage(serverOptions.UniqueName, args);
                    await SendAgentCommandAsync(serverOptions.UniqueName, ServerCommandType.SendChat, text: args, cancellationToken: cancellationToken);
                    await ReplyAsync(message, "Chat sent.");
                    return;

                case "save":
                    await SendAgentCommandAsync(serverOptions.UniqueName, ServerCommandType.SaveWorld, cancellationToken: cancellationToken);
                    await ReplyAsync(message, "Save requested.");
                    return;

                case "stop":
                    await _supervisor.StopServerAsync(serverOptions.UniqueName, cancellationToken);
                    await ReplyAsync(message, "Stop requested.");
                    return;

                case "start":
                    await _supervisor.StartServerAsync(serverOptions.UniqueName, cancellationToken);
                    await ReplyAsync(message, "Start requested.");
                    return;

                case "restart":
                    await _supervisor.RestartServerAsync(serverOptions.UniqueName, cancellationToken);
                    await ReplyAsync(message, "Restart requested.");
                    return;

                case "kick":
                    await DispatchSteamIdCommandAsync(message, serverOptions.UniqueName, args, ServerCommandType.KickPlayer, "Kick requested.", cancellationToken);
                    return;

                case "ban":
                    await DispatchSteamIdCommandAsync(message, serverOptions.UniqueName, args, ServerCommandType.BanPlayer, "Ban requested.", cancellationToken);
                    return;

                case "unban":
                    await DispatchSteamIdCommandAsync(message, serverOptions.UniqueName, args, ServerCommandType.UnbanPlayer, "Unban requested.", cancellationToken);
                    return;

                case "promote":
                    await DispatchSteamIdCommandAsync(message, serverOptions.UniqueName, args, ServerCommandType.PromotePlayer, "Promote requested.", cancellationToken);
                    return;

                case "demote":
                    await DispatchSteamIdCommandAsync(message, serverOptions.UniqueName, args, ServerCommandType.DemotePlayer, "Demote requested.", cancellationToken);
                    return;

                case "status":
                    await message.Channel.SendMessageAsync(embed: BuildStatusEmbed(serverOptions.UniqueName).Build());
                    return;

                case "help":
                    await message.Channel.SendMessageAsync(embed: BuildHelpEmbed(serverOptions).Build());
                    return;

                default:
                    await ReplyAsync(message, $"Unknown command `{verb}`.");
                    await message.Channel.SendMessageAsync(embed: BuildHelpEmbed(serverOptions).Build());
                    return;
            }
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Discord command {Verb} failed for server {UniqueName}", verb, serverOptions.UniqueName);
            await ReplyAsync(message, $"Error: {exception.Message}");
        }
    }

    public async Task RelayChatAsync(
        DiscordServerOptions serverOptions,
        string text,
        SocketMessage message,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(text))
                return;

            var trimmed = text.Trim();
            _chatRelayService.TrackDiscordToGameMessage(serverOptions.UniqueName, trimmed);
            await SendAgentCommandAsync(serverOptions.UniqueName, ServerCommandType.SendChat, text: trimmed, cancellationToken: cancellationToken);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Discord chat relay failed for server {UniqueName}", serverOptions.UniqueName);
            await ReplyAsync(message, $"Error: {exception.Message}");
        }
    }

    private async Task DispatchSteamIdCommandAsync(
        SocketMessage message,
        string uniqueName,
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

        await SendAgentCommandAsync(uniqueName, commandType, steamId: steamId, cancellationToken: cancellationToken);
        await ReplyAsync(message, successReply);
    }

    private async Task SendAgentCommandAsync(
        string uniqueName,
        ServerCommandType commandType,
        string text = "",
        long? steamId = null,
        CancellationToken cancellationToken = default)
    {
        var agent = ResolveConnectedAgent(uniqueName);
        if (agent is null)
            throw new InvalidOperationException("Server not connected.");

        await _registry.SendCommandAsync(new ServerCommandEnvelope
        {
            UniqueName = uniqueName,
            AgentId = agent.AgentId,
            ServerId = agent.ServerKey,
            CommandType = commandType,
            Text = text,
            SteamId = steamId,
            IssuedAtUtc = DateTimeOffset.UtcNow,
        }, cancellationToken);
    }

    private AgentRuntimeState? ResolveConnectedAgent(string uniqueName)
    {
        return _registry.GetAgents().FirstOrDefault(agent =>
            agent.IsConnected &&
            string.Equals(agent.UniqueNameKey, uniqueName, StringComparison.OrdinalIgnoreCase));
    }

    private EmbedBuilder BuildStatusEmbed(string uniqueName)
    {
        var definition = _serverCatalog.GetServer(uniqueName);
        var runtime = _supervisor.GetSnapshots()
            .FirstOrDefault(snapshot => string.Equals(snapshot.UniqueName, uniqueName, StringComparison.OrdinalIgnoreCase));
        var agent = _registry.GetAgents().FirstOrDefault(item =>
            string.Equals(item.UniqueNameKey, uniqueName, StringComparison.OrdinalIgnoreCase));
        var metrics = agent?.Snapshot?.Metrics;

        var title = definition?.UniqueName ?? runtime?.UniqueName ?? uniqueName;

        var builder = new EmbedBuilder()
            .WithTitle($"{title} status")
            .WithColor(agent?.IsConnected == true ? Color.Green : Color.Orange)
            .WithTimestamp(DateTimeOffset.UtcNow)
            .AddField("Server", $"`{uniqueName}`", inline: false)
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

    private static string FormatUptime(DedicatedServerRuntimeSnapshot? runtime, ServerMetrics? metrics)
    {
        if (runtime?.StartedAtUtc is not null)
            return FormatDuration(DateTimeOffset.UtcNow - runtime.StartedAtUtc.Value);

        if (metrics?.UptimeSeconds is > 0)
            return FormatDuration(TimeSpan.FromSeconds(metrics.UptimeSeconds));

        return "n/a";
    }

    private EmbedBuilder BuildHelpEmbed(DiscordServerOptions serverOptions)
    {
        var prefix = string.IsNullOrWhiteSpace(serverOptions.CommandPrefix) ? "!" : serverOptions.CommandPrefix;

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
