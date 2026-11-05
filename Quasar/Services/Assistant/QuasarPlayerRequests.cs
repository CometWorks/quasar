using System.Globalization;
using System.Security.Claims;
using Magnetar.Protocol.Model;
using Magnetar.Protocol.Transport;
using Quasar.Plugin.Abstractions.Assistant;
using Quasar.Services.Auth;

namespace Quasar.Services.Assistant;

/// <summary>
/// Turns players' and admins' requests from the game and Discord into calls to the assistant plugin's
/// handlers, with principals core builds from the authenticated source.
/// </summary>
public sealed class QuasarPlayerRequests(
    AgentRegistry registry,
    QuasarAgentPrincipals principals,
    QuasarPlayerLinks links,
    KnownPlayerCatalog knownPlayers,
    RbacConfigCatalog rbac,
    QuasarRoleMapper roles,
    IEnumerable<IQuasarPlayerRequestHandler> playerHandlers,
    IEnumerable<IQuasarAdminRequestHandler> adminHandlers,
    ILogger<QuasarPlayerRequests> logger
)
{
    public const int MaxQuestionLength = 2000;
    internal const string NotEnabled = "The assistant is not enabled on this server.";
    internal const string NotMapped =
        "Your Discord account is not mapped to a Quasar administrator. An admin can map it on the Security page.";
    private static readonly TimeSpan WhisperTimeout = TimeSpan.FromSeconds(15);

    private readonly IQuasarPlayerRequestHandler[] _playerHandlers = playerHandlers.ToArray();
    private readonly IQuasarAdminRequestHandler[] _adminHandlers = adminHandlers.ToArray();

    public bool HasPlayerHandlers => _playerHandlers.Length > 0;
    public bool HasAdminHandlers => _adminHandlers.Length > 0;

    /// <summary>
    /// A player's <c>!ask</c> or <c>!link</c> from the agent on <paramref name="connectionId"/>. Runs off the
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
        switch (request.Kind)
        {
            case PlayerRequest.LinkKind:
                var code = links.IssueCode(steamId);
                await reply($"Type /link {code} in the server's Discord within 10 minutes.", token);
                break;

            case PlayerRequest.AskKind:
                var text = Cap(request.Text);
                if (text.Length == 0)
                    return;
                if (!HasPlayerHandlers)
                {
                    await reply(NotEnabled, token);
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
                    new QuasarPlayerRequest(
                        player,
                        conversation,
                        text,
                        new(
                            QuasarRequestChannel.Game,
                            server,
                            source.IsCluster ? source.ClusterNodeId : null
                        ),
                        reply
                    ),
                    token
                );
                break;
        }
    }

    public Task<QuasarPlayerLink?> LinkDiscordAsync(
        string discordUserId,
        string code,
        CancellationToken token
    ) => links.RedeemAsync(discordUserId, code, token);

    /// <summary><c>/ask</c>: a linked user asks as their player, an unlinked one gets general answers only.</summary>
    public async Task AskFromDiscordAsync(
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
        if (!HasPlayerHandlers)
        {
            await reply(NotEnabled, token);
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
            new QuasarPlayerRequest(
                player,
                conversation,
                text,
                new(
                    QuasarRequestChannel.Discord,
                    DiscordUserId: discordUserId,
                    DiscordChannelId: channelId
                ),
                reply
            ),
            token
        );
    }

    /// <summary><c>/assistant</c>: only for Discord users mapped to a Quasar subject.</summary>
    public async Task AskAdminFromDiscordAsync(
        string discordUserId,
        string channelId,
        string text,
        Func<string, CancellationToken, Task> reply,
        CancellationToken token
    )
    {
        var asker = CreateDiscordAdmin(discordUserId);
        if (asker is null)
        {
            await reply(NotMapped, token);
            return;
        }

        text = Cap(text);
        if (text.Length == 0)
            return;
        foreach (var handler in _adminHandlers)
        {
            try
            {
                await handler.HandleAsync(
                    new QuasarAdminRequest(asker, channelId, text, reply),
                    token
                );
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                logger.LogWarning(
                    error,
                    "The admin request handler {Handler} failed.",
                    handler.GetType().FullName
                );
            }
        }
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

    private async Task CallAsync(QuasarPlayerRequest request, CancellationToken token)
    {
        foreach (var handler in _playerHandlers)
        {
            try
            {
                await handler.HandleAsync(request, token);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                logger.LogWarning(
                    error,
                    "The player request handler {Handler} failed.",
                    handler.GetType().FullName
                );
            }
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
        return text.Length <= MaxQuestionLength ? text : text[..MaxQuestionLength];
    }

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public static class QuasarPlayerAssistanceRegistration
{
    public static IServiceCollection AddQuasarPlayerAssistance(this IServiceCollection services) =>
        services
            .AddSingleton<QuasarPlayerLinks>()
            .AddSingleton<QuasarPlayerRequests>();
}
