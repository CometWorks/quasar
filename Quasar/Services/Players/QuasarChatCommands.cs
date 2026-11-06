using System.Globalization;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Magnetar.Protocol.Model;
using Magnetar.Protocol.Transport;
using Quasar.Plugin.Abstractions.Commands;
using Quasar.Plugin.Abstractions.Servers;
using Quasar.Services.Auth;

namespace Quasar.Services.Players;

/// <summary>
/// The chat commands UI plugins add (<see cref="IQuasarChatCommand"/>) and core's <c>!link</c>: takes them from the
/// game and Discord, builds the caller's principal from the authenticated source and hands them to the plugin.
/// </summary>
public sealed class QuasarChatCommands
{
    public const int MaxTextLength = 2000;
    internal const string NotAvailable = "This command is not available on this server.";
    internal const string NotMapped =
        "Your Discord account is not mapped to a Quasar administrator. An admin can map it on the Security page.";
    private static readonly TimeSpan WhisperTimeout = TimeSpan.FromSeconds(15);
    private static readonly Regex NamePattern = new(@"\A[a-z0-9-]{1,32}\z");

    /// <summary>Core's own game and Discord commands, which plugins can't take.</summary>
    internal static readonly IReadOnlySet<string> Reserved = new HashSet<string>(
        StringComparer.OrdinalIgnoreCase
    )
    {
        "link",
        "whisper",
        "faction-channel",
        "help",
        "stop",
        "restart",
        "quit",
    };

    private readonly AgentRegistry registry;
    private readonly QuasarAgentPrincipals principals;
    private readonly QuasarPlayerLinks links;
    private readonly KnownPlayerCatalog knownPlayers;
    private readonly RbacConfigCatalog rbac;
    private readonly QuasarRoleMapper roles;
    private readonly ILogger<QuasarChatCommands> logger;
    private readonly Dictionary<string, IQuasarChatCommand> _commands = new(
        StringComparer.OrdinalIgnoreCase
    );

    public QuasarChatCommands(
        AgentRegistry registry,
        QuasarAgentPrincipals principals,
        QuasarPlayerLinks links,
        KnownPlayerCatalog knownPlayers,
        RbacConfigCatalog rbac,
        QuasarRoleMapper roles,
        IEnumerable<IQuasarChatCommand> commands,
        ILogger<QuasarChatCommands> logger
    )
    {
        this.registry = registry;
        this.principals = principals;
        this.links = links;
        this.knownPlayers = knownPlayers;
        this.rbac = rbac;
        this.roles = roles;
        this.logger = logger;
        foreach (var command in commands)
        {
            var name = command.Info.Name;
            if (!NamePattern.IsMatch(name ?? string.Empty) || Reserved.Contains(name!))
                logger.LogWarning(
                    "Left out the chat command {Name} of {Plugin}: the name is invalid or core's own.",
                    name,
                    command.GetType().FullName
                );
            else if (!_commands.TryAdd(name!, command))
                logger.LogWarning(
                    "Left out the chat command {Name} of {Plugin}: {Other} already has that name.",
                    name,
                    command.GetType().FullName,
                    _commands[name!].GetType().FullName
                );
        }
    }

    /// <summary>The plugins' commands, by name.</summary>
    public IReadOnlyCollection<QuasarChatCommandInfo> Commands =>
        _commands.Values.Select(command => command.Info).OrderBy(info => info.Name).ToList();

    /// <summary>The player commands as Quasar.Agent registers them.</summary>
    public List<ChatCommandInfo> GameCommands() =>
        Commands
            .Where(info => info.Scope == QuasarChatCommandScope.Player)
            .Select(info => new ChatCommandInfo
            {
                Name = info.Name,
                Description = Short(info.Description),
                Argument = NamePattern.IsMatch(info.ArgumentName ?? string.Empty)
                    ? info.ArgumentName
                    : "text",
                Acknowledgement = info.Acknowledgement,
            })
            .ToList();

    /// <summary>
    /// A player's <c>!link</c> or plugin command from the agent on <paramref name="connectionId"/>. Runs off the
    /// socket's receive loop; failures are logged, never thrown.
    /// </summary>
    public void Accept(string connectionId, PlayerRequest request, CancellationToken token)
    {
        if (!registry.TryGetConnectedAgent(connectionId, out var source))
        {
            logger.LogWarning(
                "Ignored a player request from unknown agent connection {ConnectionId}.",
                connectionId
            );
            return;
        }

        _ = Task.Run(
            async () =>
            {
                try
                {
                    await HandleGameRequestAsync(source, request, token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { }
                catch (Exception error)
                {
                    logger.LogWarning(
                        error,
                        "A player request from {Server} failed.",
                        ServerOf(source)
                    );
                }
            },
            CancellationToken.None
        );
    }

    internal async Task HandleGameRequestAsync(
        AgentRuntimeState source,
        PlayerRequest request,
        CancellationToken token
    )
    {
        var steamId = request.SteamId.ToString(CultureInfo.InvariantCulture);
        if (!RbacConfigCatalog.IsSteamId64(steamId))
            return;

        Func<string, CancellationToken, Task> reply = (text, ct) =>
            WhisperAsync(source, request.SteamId, text, ct);
        if (request.Kind == PlayerRequest.LinkKind)
        {
            var code = links.IssueCode(steamId);
            await reply($"Type /link {code} in the server's Discord within 10 minutes.", token);
            return;
        }

        var text = Cap(request.Text);
        if (text.Length == 0)
            return;
        if (
            !_commands.TryGetValue(request.Kind, out var command)
            || command.Info.Scope != QuasarChatCommandScope.Player
        )
        {
            await reply(NotAvailable, token);
            return;
        }

        var server = ServerOf(source);
        var conversation = "steam:" + steamId;
        var player = principals.CreatePlayer(
            steamId,
            NullIfEmpty(request.PlayerName),
            null,
            conversation,
            text,
            server
        );
        await CallAsync(
            command,
            new QuasarChatCommandRequest(
                command.Info.Name,
                player,
                conversation,
                text,
                new(QuasarChatChannel.Game, server, source.IsCluster ? source.ClusterNodeId : null),
                reply
            ),
            token
        );
    }

    public Task<QuasarPlayerLink?> LinkDiscordAsync(
        string discordUserId,
        string code,
        CancellationToken token
    ) => links.RedeemAsync(discordUserId, code, token);

    /// <summary>A plugin command on Discord. A player command runs as the linked player, or with no principal
    /// for an unlinked user; an admin command only for Discord users mapped to a Quasar subject.</summary>
    public async Task HandleDiscordAsync(
        string name,
        string discordUserId,
        string channelId,
        string text,
        Func<string, CancellationToken, Task> reply,
        CancellationToken token
    )
    {
        text = Cap(text);
        if (text.Length == 0)
            return;
        if (!_commands.TryGetValue(name, out var command))
        {
            await reply(NotAvailable, token);
            return;
        }

        var source = new QuasarChatCommandSource(
            QuasarChatChannel.Discord,
            DiscordUserId: discordUserId,
            DiscordChannelId: channelId
        );
        if (command.Info.Scope == QuasarChatCommandScope.Admin)
        {
            var asker = CreateDiscordAdmin(discordUserId);
            if (asker is null)
            {
                await reply(NotMapped, token);
                return;
            }
            await CallAsync(
                command,
                new QuasarChatCommandRequest(
                    command.Info.Name,
                    asker,
                    "discord:" + discordUserId,
                    text,
                    source,
                    reply
                ),
                token
            );
            return;
        }

        var link = links.FindByDiscordUser(discordUserId);
        var conversation = link is null ? "discord:" + discordUserId : "steam:" + link.SteamId;
        var player = link is null
            ? null
            : principals.CreatePlayer(
                link.SteamId,
                KnownName(link.SteamId),
                discordUserId,
                conversation,
                text
            );
        await CallAsync(
            command,
            new QuasarChatCommandRequest(
                command.Info.Name,
                player,
                conversation,
                text,
                source,
                reply
            ),
            token
        );
    }

    /// <summary>The mapped subject's principal with its current roles; null without a usable mapping.
    /// Steam is the only provider that can be mapped in v1.</summary>
    internal ClaimsPrincipal? CreateDiscordAdmin(string discordUserId)
    {
        var mapping = rbac.GetConfig()
            .DiscordSubjects.FirstOrDefault(m => m.DiscordUserId == discordUserId);
        if (
            mapping is null
            || !string.Equals(
                mapping.Provider,
                QuasarAuthSchemes.Steam,
                StringComparison.OrdinalIgnoreCase
            )
            || !RbacConfigCatalog.IsSteamId64(mapping.Subject)
        )
            return null;
        return roles.CreateSteamPrincipal(mapping.Subject);
    }

    /// <summary>Whispers an answer to the player, split into chat-sized messages, through the node that
    /// carried the request or, on a cluster, any node: the cluster chat relay finds the player's node.</summary>
    internal async Task WhisperAsync(
        AgentRuntimeState source,
        long steamId,
        string text,
        CancellationToken token
    )
    {
        foreach (
            var part in QuasarTextSplitter.Split(
                text,
                QuasarTextSplitter.WhisperLength,
                QuasarTextSplitter.MaxWhispers
            )
        )
        {
            var agent =
                WhisperAgent(source)
                ?? throw new InvalidOperationException(
                    $"No agent of {ServerOf(source)} is connected to whisper through."
                );
            var result = await registry.SendCommandAndWaitAsync(
                new ServerCommandEnvelope
                {
                    UniqueName = agent.UniqueNameKey,
                    AgentId = agent.AgentId,
                    ServerId = agent.ServerKey,
                    CommandType = ServerCommandType.SendWhisper,
                    Text = part,
                    SteamId = steamId,
                },
                WhisperTimeout,
                token
            );
            if (!result.Success)
                throw new InvalidOperationException(result.Message);
        }
    }

    private AgentRuntimeState? WhisperAgent(AgentRuntimeState source)
    {
        var agents = registry.GetAgents().Where(agent => agent.IsConnected).ToList();
        if (!source.IsCluster)
            return agents.FirstOrDefault(agent =>
                !agent.IsCluster
                && string.Equals(
                    agent.UniqueNameKey,
                    source.UniqueNameKey,
                    StringComparison.OrdinalIgnoreCase
                )
            );
        return agents.FirstOrDefault(agent => agent.AgentId == source.AgentId)
            ?? agents.FirstOrDefault(agent =>
                agent.IsCluster
                && agent.ClusterId == source.ClusterId
                && agent.Snapshot?.ClusterChatReady == true
            );
    }

    private async Task CallAsync(
        IQuasarChatCommand command,
        QuasarChatCommandRequest request,
        CancellationToken token
    )
    {
        try
        {
            await command.HandleAsync(request, token);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            logger.LogWarning(
                error,
                "The chat command {Name} of {Plugin} failed.",
                command.Info.Name,
                command.GetType().FullName
            );
        }
    }

    private string? KnownName(string steamId) =>
        knownPlayers
            .GetPlayers()
            .Where(player => player.SteamId.ToString(CultureInfo.InvariantCulture) == steamId)
            .OrderByDescending(player => player.LastSeenUtc)
            .Select(player => NullIfEmpty(player.DisplayName))
            .FirstOrDefault();

    internal static string ServerOf(AgentRuntimeState agent) =>
        agent.IsCluster ? agent.ClusterId : agent.UniqueNameKey;

    private static string Cap(string? text)
    {
        text = (text ?? string.Empty).Trim();
        return text.Length <= MaxTextLength ? text : text[..MaxTextLength];
    }

    internal static string Short(string? text)
    {
        text = (text ?? string.Empty).Trim();
        return text.Length <= 100 ? text : text[..99] + "…";
    }

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public static class QuasarPlayerServicesRegistration
{
    public static IServiceCollection AddQuasarPlayerServices(this IServiceCollection services) =>
        services
            .AddSingleton<QuasarPlayerLinks>()
            .AddSingleton<QuasarChatCommands>()
            .AddSingleton<QuasarServerQueries>()
            .AddSingleton<IQuasarServerQueries>(provider =>
                provider.GetRequiredService<QuasarServerQueries>()
            )
            .AddSingleton<QuasarPlayerTools>();
}
