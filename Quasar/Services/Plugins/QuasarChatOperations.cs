using System.Globalization;
using Magnetar.Protocol.Model;
using Magnetar.Protocol.Transport;
using Quasar.Models;
using Quasar.Plugin.Abstractions.Operations;
using Quasar.Services.Auth;
using Quasar.Services.Discord;
using static Quasar.Services.Plugins.QuasarCoreOperations;
using Admin = CometWorks.ClusterGateway.AdminContract.V1;

namespace Quasar.Services.Plugins;

/// <summary>What the Chat page shows and sends: recent chat, chat commands, global chat and console commands.</summary>
public static class QuasarChatOperations
{
    public sealed record ChatRequest(string UniqueName = "", int Limit = 50);

    public sealed record ChatSendRequest(string UniqueName = "", string Text = "");

    /// <param name="SteamId">The author's SteamID64, null for the server and console.</param>
    public sealed record ChatLine(
        DateTimeOffset? Time,
        string Author,
        string? SteamId,
        string Channel,
        string Text
    );

    /// <param name="Error">Why the chat is unavailable; Messages is then empty.</param>
    public sealed record ChatResult(
        string UniqueName,
        bool IsCluster,
        string? Error,
        IReadOnlyList<ChatLine> Messages
    );

    public sealed record ChatCommand(
        string Command,
        string Syntax,
        string Description,
        string MinimumPromoteLevel
    );

    public sealed record ChatCommandList(string UniqueName, IReadOnlyList<ChatCommand> Commands);

    public sealed record ChatSendResult(string UniqueName, string Message);

    private const int MaxLimit = 100;
    private const string ClusterHint = "use chat.messages and chat.send for cluster chat";
    private static readonly TimeSpan CommandWait = TimeSpan.FromSeconds(15);

    public static void AddQuasarChatOperations(this IServiceCollection services)
    {
        services.AddQuasarOperation<ChatRequest, ChatResult>(
            QuasarCoreOperations.PluginId,
            "chat.messages",
            [QuasarPolicyNames.CanView],
            MessagesAsync,
            new()
            {
                Effect = QuasarOperationEffect.Read,
                Description =
                    $"Returns the recent chat of a standalone server or a cluster, oldest first: time, author, Steam ID, channel and text, at most limit (default 50, up to {MaxLimit}) messages. A cluster gives its accepted global chat only.",
            }
        );
        services.AddQuasarOperation<ServerRequest, ChatCommandList>(
            QuasarCoreOperations.PluginId,
            "chat.commands",
            [QuasarPolicyNames.CanView],
            (caller, input, _) => Task.FromResult(Commands(caller.Services, input.UniqueName)),
            new()
            {
                Effect = QuasarOperationEffect.Read,
                Description =
                    "Lists the ! chat commands registered on a standalone server (Quasar.Agent and plugin commands) with syntax, description and the minimum promote level.",
            }
        );
        services.AddQuasarOperation<ChatSendRequest, ChatSendResult>(
            QuasarCoreOperations.PluginId,
            "chat.send",
            [QuasarPolicyNames.CanControlServers],
            SendAsync,
            new()
            {
                Reversal = QuasarReversal.NotReversible,
                Deadline = TimeSpan.FromSeconds(60),
                Description =
                    "Sends a message to the global chat of a standalone server or a cluster. It appears from Server.",
            }
        );
        services.AddQuasarOperation<ChatSendRequest, ChatSendResult>(
            QuasarCoreOperations.PluginId,
            "chat.command.run",
            [QuasarPolicyNames.CanControlServers],
            RunAsync,
            new()
            {
                Reversal = QuasarReversal.NotReversible,
                Description =
                    "Runs a ! chat command on a standalone server as its console, with Owner rights, and returns the command's replies. Players don't see the command line.",
            }
        );
    }

    private static async Task<ChatResult> MessagesAsync(
        QuasarOperationContext caller,
        ChatRequest input,
        CancellationToken token
    )
    {
        var name = input.UniqueName?.Trim() ?? string.Empty;
        var limit = Math.Clamp(input.Limit <= 0 ? 50 : input.Limit, 1, MaxLimit);
        var agent = FindAgent(caller.Services, name);
        if (agent is not null)
            return new(
                agent.UniqueNameKey,
                false,
                null,
                (agent.Snapshot?.RecentChat ?? [])
                    .OrderBy(message => message.TimestampTicksUtc)
                    .TakeLast(limit)
                    .Select(Line)
                    .ToList()
            );
        if (
            caller.Services.GetRequiredService<DedicatedServerCatalog>().GetServer(name) is
            { } server
        )
            return new(
                server.UniqueName,
                false,
                $"Server '{server.UniqueName}' is not connected.",
                []
            );
        var cluster = RequireCluster(caller, name);
        try
        {
            return new(
                cluster.UniqueName,
                true,
                null,
                await ClusterChatAsync(caller.Services, cluster, limit, token)
            );
        }
        catch (ClusterGatewayException error)
        {
            return new(cluster.UniqueName, true, error.Message, []);
        }
    }

    // The Gateway pages its history oldest first, so the newest messages are on the last page.
    private static async Task<IReadOnlyList<ChatLine>> ClusterChatAsync(
        IServiceProvider services,
        ClusterDefinition cluster,
        int limit,
        CancellationToken token
    )
    {
        var gateway = services.GetRequiredService<ClusterGatewayClient>();
        var names = services
            .GetRequiredService<KnownPlayerCatalog>()
            .GetPlayers()
            .Where(record =>
                record.IsCluster
                && string.Equals(
                    record.UniqueName,
                    cluster.UniqueName,
                    StringComparison.OrdinalIgnoreCase
                )
            )
            .GroupBy(record => (ulong)record.SteamId)
            .ToDictionary(group => group.Key, group => group.First().DisplayName);
        var messages = new Queue<Admin.ChatMessage>();
        long cursor = 0,
            last = 0;
        for (var page = 0; page < 20; page++)
        {
            var history = (await gateway.GetChatAsync(cluster, cursor, 500, token)).Data;
            var fresh = history
                .Messages.Where(message => message.Seq > last)
                .OrderBy(message => message.Seq)
                .ToArray();
            if (fresh.Length == 0)
                break;
            foreach (var message in fresh)
            {
                messages.Enqueue(message);
                if (messages.Count > limit)
                    messages.Dequeue();
                last = message.Seq;
            }
            cursor = Math.Max(cursor, history.NextCursor);
        }
        return messages
            .Select(message => new ChatLine(
                message.At,
                names.TryGetValue(message.Author, out var author)
                && !string.IsNullOrWhiteSpace(author)
                    ? TextSanitizer.CleanGameText(author)
                    : message.Author.ToString(CultureInfo.InvariantCulture),
                message.Author.ToString(CultureInfo.InvariantCulture),
                "Global",
                TextSanitizer.CleanGameText(message.Text, keepLineBreaks: true)
            ))
            .ToList();
    }

    private static ChatLine Line(ChatMessageSnapshot message)
    {
        var name = TextSanitizer.CleanGameText(message.AuthorName);
        var server =
            message.IsServerMessage
            || message.SteamId <= 0
            || name.Equals("Good.bot", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Server", StringComparison.OrdinalIgnoreCase);
        return new(
            message.TimestampTicksUtc > 0
                ? new DateTimeOffset(message.TimestampTicksUtc, TimeSpan.Zero)
                : null,
            server ? "Server"
                : string.IsNullOrWhiteSpace(name)
                    ? message.SteamId.ToString(CultureInfo.InvariantCulture)
                : name,
            server ? null : message.SteamId.ToString(CultureInfo.InvariantCulture),
            message.Channel switch
            {
                ChatMessageChannel.Faction => string.IsNullOrWhiteSpace(message.FactionTag)
                    ? "Faction"
                    : $"Faction {message.FactionTag}",
                ChatMessageChannel.Whisper => string.IsNullOrWhiteSpace(message.TargetName)
                    ? "Whisper"
                    : $"Whisper to {TextSanitizer.CleanGameText(message.TargetName)}",
                _ => message.Channel.ToString(),
            },
            TextSanitizer.CleanGameText(message.Content, keepLineBreaks: true)
        );
    }

    private static ChatCommandList Commands(IServiceProvider services, string uniqueName)
    {
        var name = uniqueName?.Trim() ?? string.Empty;
        var agent = RequireAgent(services, name);
        return new(
            agent.UniqueNameKey,
            (agent.Snapshot?.ChatCommands ?? [])
                .DistinctBy(command => command.Text, StringComparer.OrdinalIgnoreCase)
                .OrderBy(command => command.Text, StringComparer.OrdinalIgnoreCase)
                .Select(command => new ChatCommand(
                    command.Text,
                    command.Syntax,
                    command.Description,
                    command.MinimumPromoteLevel
                ))
                .ToList()
        );
    }

    private static async Task<ChatSendResult> SendAsync(
        QuasarOperationContext caller,
        ChatSendRequest input,
        CancellationToken token
    )
    {
        var name = input.UniqueName?.Trim() ?? string.Empty;
        var text = input.Text?.Trim() ?? string.Empty;
        if (text.Length == 0)
            throw new QuasarOperationException(400, "Text is empty.");
        if (text.StartsWith('!'))
            throw new QuasarOperationException(
                400,
                "Text starting with ! is a command; use chat.command.run."
            );
        var agent = FindAgent(caller.Services, name);
        if (
            agent is null
            && caller.Services.GetRequiredService<DedicatedServerCatalog>().GetServer(name) is null
        )
        {
            var cluster = RequireCluster(caller, name);
            caller.Undo!.Server = cluster.UniqueName;
            var bridge =
                caller.Services.GetService<DiscordClusterBridge>()
                ?? throw new QuasarOperationException(409, "Cluster chat is unavailable.");
            try
            {
                await bridge.SendCommandAsync(
                    new DiscordServerOptions { UniqueName = cluster.UniqueName, IsCluster = true },
                    ServerCommandType.SendChat,
                    text,
                    token: token
                );
            }
            catch (Exception error)
                when (error
                        is InvalidOperationException
                            or ClusterGatewayException
                            or TimeoutException
                )
            {
                throw new QuasarOperationException(409, error.Message);
            }
            return new(cluster.UniqueName, "Chat message sent.");
        }
        caller.Undo!.Server = name;
        return new(
            name,
            await SendCommandAsync(caller.Services, name, ServerCommandType.SendChat, text, token)
        );
    }

    private static async Task<ChatSendResult> RunAsync(
        QuasarOperationContext caller,
        ChatSendRequest input,
        CancellationToken token
    )
    {
        var name = input.UniqueName?.Trim() ?? string.Empty;
        var text = input.Text?.Trim() ?? string.Empty;
        if (!text.StartsWith('!') || text.Length == 1)
            throw new QuasarOperationException(400, "A command starts with !, like !help.");
        caller.Undo!.Server = name;
        return new(
            name,
            await SendCommandAsync(
                caller.Services,
                name,
                ServerCommandType.RunChatCommand,
                text,
                token
            )
        );
    }

    private static async Task<string> SendCommandAsync(
        IServiceProvider services,
        string uniqueName,
        ServerCommandType command,
        string text,
        CancellationToken token
    )
    {
        var agent = RequireAgent(services, uniqueName);
        var result = await services
            .GetRequiredService<AgentRegistry>()
            .SendCommandAndWaitAsync(
                new ServerCommandEnvelope
                {
                    UniqueName = agent.UniqueNameKey,
                    AgentId = agent.AgentId,
                    ServerId = agent.ServerKey,
                    CommandType = command,
                    Text = text,
                },
                CommandWait,
                token
            );
        if (!result.Success)
            throw new QuasarOperationException(409, result.Message);
        return result.Message;
    }

    private static AgentRuntimeState RequireAgent(IServiceProvider services, string uniqueName) =>
        FindAgent(services, uniqueName)
        ?? (
            services.GetRequiredService<DedicatedServerCatalog>().GetServer(uniqueName) is null
                ? throw NotFound(services, uniqueName, ClusterHint)
                : throw new QuasarOperationException(
                    409,
                    $"Server '{uniqueName}' is not connected."
                )
        );

    private static ClusterDefinition RequireCluster(QuasarOperationContext caller, string name) =>
        caller.Services.GetService<ClusterCatalog>()?.GetCluster(name) is { } cluster
        && caller.User.CanQueryCluster(cluster.UniqueName)
            ? cluster
            : throw new QuasarOperationException(404, $"Server or cluster '{name}' not found.");

    private static AgentRuntimeState? FindAgent(IServiceProvider services, string uniqueName) =>
        services
            .GetRequiredService<AgentRegistry>()
            .GetAgents()
            .FirstOrDefault(agent =>
                agent.IsConnected
                && !agent.IsCluster
                && agent.UniqueNameKey.Equals(uniqueName, StringComparison.OrdinalIgnoreCase)
            );
}
