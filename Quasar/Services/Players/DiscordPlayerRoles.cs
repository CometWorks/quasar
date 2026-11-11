using System.Collections.Concurrent;
using System.Net;
using Discord;
using Discord.Net;
using Discord.WebSocket;
using Quasar.Services.Discord;

namespace Quasar.Services.Players;

public sealed record DiscordPlayerMembership(bool Known, bool Member, ulong[] Roles, DateTimeOffset ExpiresAt);

/// <summary>Fresh REST membership, invalidated by gateway events. Failures never extend an old grant.</summary>
public sealed class DiscordPlayerRoles(DiscordBotService bot, DiscordOptionsCatalog options)
{
    private readonly ConcurrentDictionary<(ulong Guild, ulong User), DiscordPlayerMembership> cache = new();
    private readonly SemaphoreSlim gate = new(1, 1);
    private DiscordSocketClient? subscribed;
    private long invalidation;
    private DateTimeOffset retryAfter;

    public async Task<DiscordPlayerMembership> GetAsync(ulong guild, ulong user, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            var client = bot.CurrentClient;
            if (!ReferenceEquals(client, subscribed))
            {
                if (subscribed is not null)
                {
                    subscribed.GuildMemberUpdated -= Updated;
                    subscribed.UserLeft -= Left;
                    subscribed.RoleDeleted -= RoleDeleted;
                    subscribed.RoleUpdated -= RoleUpdated;
                }
                subscribed = client;
                cache.Clear();
                if (client is not null)
                {
                    client.GuildMemberUpdated += Updated;
                    client.UserLeft += Left;
                    client.RoleDeleted += RoleDeleted;
                    client.RoleUpdated += RoleUpdated;
                }
            }
            var config = options.GetOptions();
            var key = (guild, user);
            cache.TryGetValue(key, out var previous);
            DiscordPlayerMembership LastKnown() => previous is not null && previous.ExpiresAt > DateTimeOffset.UtcNow ? previous : Unknown();
            if (!config.Enabled || config.GuildId != guild) return Unknown();
            if (client?.ConnectionState != ConnectionState.Connected || client.GetGuild(guild) is null) return LastKnown();
            if (cache.TryGetValue(key, out var cached) && cached.ExpiresAt > DateTimeOffset.UtcNow.AddSeconds(90)) return cached;
            if (retryAfter > DateTimeOffset.UtcNow) return LastKnown();
            var generation = Interlocked.Read(ref invalidation);
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                var member = await client.Rest.GetGuildUserAsync(guild, user, new RequestOptions { CancelToken = timeout.Token });
                if (generation != Interlocked.Read(ref invalidation)) return Unknown();
                var fresh = new DiscordPlayerMembership(true, member is not null, member?.RoleIds.ToArray() ?? [], DateTimeOffset.UtcNow.AddMinutes(2));
                if (cache.Count >= 10000) cache.Clear();
                cache[key] = fresh;
                return fresh;
            }
            catch (HttpException error) when (error.HttpCode == HttpStatusCode.NotFound)
            {
                return cache[key] = new(true, false, [], DateTimeOffset.UtcNow.AddMinutes(2));
            }
            catch (Exception error) when (error is HttpException or HttpRequestException or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
            {
                token.ThrowIfCancellationRequested();
                retryAfter = DateTimeOffset.UtcNow.AddSeconds(5);
                return cached is not null && cached.ExpiresAt > DateTimeOffset.UtcNow ? cached : Unknown();
            }
        }
        finally { gate.Release(); }
    }

    private Task Updated(Cacheable<SocketGuildUser, ulong> _, SocketGuildUser user)
    { Interlocked.Increment(ref invalidation); cache.TryRemove((user.Guild.Id, user.Id), out var ignored); return Task.CompletedTask; }
    private Task Left(SocketGuild guild, SocketUser user)
    { Interlocked.Increment(ref invalidation); cache[(guild.Id, user.Id)] = new(true, false, [], DateTimeOffset.UtcNow.AddMinutes(2)); return Task.CompletedTask; }
    private Task RoleDeleted(SocketRole role) { Invalidate(role.Guild.Id); return Task.CompletedTask; }
    private Task RoleUpdated(SocketRole _, SocketRole role) { Invalidate(role.Guild.Id); return Task.CompletedTask; }
    private void Invalidate(ulong guild)
    { Interlocked.Increment(ref invalidation); foreach (var key in cache.Keys.Where(key => key.Guild == guild)) cache.TryRemove(key, out _); }
    private static DiscordPlayerMembership Unknown() => new(false, false, [], DateTimeOffset.UtcNow);
}
