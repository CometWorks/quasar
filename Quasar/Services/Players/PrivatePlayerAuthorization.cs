using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;
using Quasar.Services.Auth;
using Quasar.Services.Discord;

namespace Quasar.Services.Players;

public sealed class PrivatePlayerAuthorization(IHttpClientFactory clients, PlayerAccessCatalog catalog,
    PlayerAccessService access, QuasarPlayerLinks links, DiscordOptionsCatalog discord)
{
    internal const string ClientName = "PrivatePlayerAuthorization";
    private static readonly Uri LinkPage = new(PlayerAuthEndpoints.AuthenticationOrigin + "/link");
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
            using var client = clients.CreateClient(ClientName);
            var discordId = player.ToString(CultureInfo.InvariantCulture);
            var codeVerifier = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
            var codeChallenge = WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier)));
            var destination = new string((guildName + " · " + target.Name).Where(c => !char.IsControl(c)).Take(200).ToArray());
            using var response = await client.PostAsJsonAsync("link/requests",
                new { discordId, destination, policies[target.Key].Instructions, codeChallenge }, token);
            if (!response.IsSuccessStatusCode)
                return "The shared authentication service is unavailable. Try /authorize again shortly.";
            var registration = await response.Content.ReadFromJsonAsync<Registration>(token);
            if (registration?.RequestId is not { Length: 43 } id || !id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
                return "The shared authentication service returned an invalid request. Try /authorize again.";

            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
            lifetime.CancelAfter(TimeSpan.FromMinutes(5));
            await sendLink(new Uri(QueryHelpers.AddQueryString(LinkPage.AbsoluteUri, "request", id)));
            var proof = await WaitForProofAsync(client, id, codeVerifier, lifetime.Token);
            if (proof is null) return "This authorization request has expired. Run /authorize again.";
            if (proof.DiscordId != discordId || !RbacConfigCatalog.IsSteamId64(proof.SteamId ?? ""))
                return "The verified accounts did not match the request. Run /authorize again.";
            var policy = catalog.GetSettings().Policies.FirstOrDefault(p => p.Enabled && p.Target == target.Key && p.GuildId == guild);
            if (policy is null || policy.Instructions != policies[target.Key].Instructions || !IsConfiguredGuild(guild))
                return "Player authorization settings changed. Run /authorize again.";
            var link = await links.AuthenticateAsync(proof.SteamId!, discordId, token);
            await access.AuthorizeAsync(policy, link, token);
            return "Server access authorized. Allow a moment for the policy to apply before joining.";
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

    private static async Task<Proof?> WaitForProofAsync(HttpClient client, string requestId, string codeVerifier, CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (await timer.WaitForNextTickAsync(token))
        {
            using var response = await client.PostAsJsonAsync("link/redeem", new { requestId, codeVerifier }, token);
            if (response.StatusCode is HttpStatusCode.Accepted or HttpStatusCode.TooManyRequests) continue;
            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<Proof>(token)
                ?? throw new InvalidDataException("The shared authentication service did not return account proof.");
        }
        return null;
    }

    private sealed record Registration(string RequestId);
    private sealed record Proof(string SteamId, string DiscordId);
}
