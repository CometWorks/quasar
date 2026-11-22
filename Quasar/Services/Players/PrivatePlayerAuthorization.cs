using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using Quasar.Services.Auth;
using Quasar.Services.Discord;

namespace Quasar.Services.Players;

internal sealed class PrivatePlayerAuthorization(SharedAccountVerification verification, PlayerAccessCatalog catalog,
    PlayerAccessService access, QuasarPlayerLinks links, DiscordOptionsCatalog discord)
{
    private readonly ConcurrentDictionary<ulong, byte> pendingPlayers = new();
    private readonly SemaphoreSlim pendingLimit = new(32);

    internal async Task<string> AuthorizeAsync(ulong guild, string guildName, ulong player, string server,
        Func<Uri, Task> sendLink, CancellationToken token)
    {
        if (!IsConfiguredGuild(guild)) return "The Quasar bot is not configured for this Discord server.";
        var policies = catalog.GetSettings().Policies.Where(p => p.Enabled && p.GuildId == guild)
            .ToDictionary(p => p.Target, StringComparer.OrdinalIgnoreCase);
        var targets = access.Targets().Where(t => policies.ContainsKey(t.Key)).ToArray();
        var target = targets.FirstOrDefault(t => string.Equals(t.Key, server, StringComparison.OrdinalIgnoreCase));
        if (target is null)
        {
            var matches = targets.Where(t => string.Equals(t.Name, server, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length > 1) return "More than one enabled server has that name. Enter its server: or cluster: identifier.";
            target = matches.FirstOrDefault();
        }
        if (target is null) return "Player authorization is not enabled for that server or cluster.";
        if (!pendingLimit.Wait(0)) return "Too many players are authorizing. Try again in a few minutes.";
        if (!pendingPlayers.TryAdd(player, 0))
        {
            pendingLimit.Release();
            return "You already have an authorization request in progress.";
        }

        try
        {
            var discordId = player.ToString(CultureInfo.InvariantCulture);
            var destination = new string((guildName + " · " + target.Name).Where(c => !char.IsControl(c)).Take(200).ToArray());
            var request = await verification.StartAsync(steamId: null, discordId, destination, policies[target.Key].Instructions, token);
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
            lifetime.CancelAfter(TimeSpan.FromMinutes(5));
            await sendLink(request.Link);
            SharedAccountVerification.Proof? proof = null;
            while (proof is null)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), lifetime.Token);
                proof = await verification.RedeemAsync(request, lifetime.Token);
            }
            if (proof.DiscordId != discordId || !RbacConfigCatalog.IsSteamId64(proof.SteamId ?? ""))
                return "The verified accounts did not match the request. Run /authorize again.";
            var policy = catalog.GetSettings().Policies.FirstOrDefault(p => p.Enabled && p.Target == target.Key && p.GuildId == guild);
            if (policy is null || policy.Instructions != policies[target.Key].Instructions || !IsConfiguredGuild(guild))
                return "Player authorization settings changed. Run /authorize again.";
            var link = await links.AuthenticateAsync(proof.SteamId!, discordId, token);
            await access.AuthorizeAsync(policy, link, token);
            return "Server access authorized. Allow a moment for the policy to apply before joining.";
        }
        catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.NotFound)
        {
            return "The authorization request was unavailable or expired. Run /authorize again.";
        }
        catch (Exception error) when (error is HttpRequestException or InvalidDataException)
        {
            return "The shared authentication service is unavailable. Try /authorize again shortly.";
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return "Authorization timed out. Run /authorize again.";
        }
        finally
        {
            pendingPlayers.TryRemove(player, out _);
            pendingLimit.Release();
        }
    }

    internal async Task<string> UnlinkAsync(ulong guild, ulong player, CancellationToken token)
    {
        if (!IsConfiguredGuild(guild)) return "The Quasar bot is not configured for this Discord server.";
        if (!pendingPlayers.TryAdd(player, 0))
            return "An account action is in progress. Finish it or wait for it to expire before unlinking.";
        try
        {
            return await links.RemoveByDiscordAsync(player.ToString(CultureInfo.InvariantCulture), token)
                ? "Your accounts were unlinked and their linked server access was revoked."
                : "This Discord account has no authenticated link on this Quasar installation.";
        }
        finally { pendingPlayers.TryRemove(player, out _); }
    }

    private bool IsConfiguredGuild(ulong guild) => discord.GetOptions() is { Enabled: true } options
        && options.GuildId == guild && guild != 0;
}
