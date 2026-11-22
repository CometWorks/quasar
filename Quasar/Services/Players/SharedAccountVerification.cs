using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.WebUtilities;

namespace Quasar.Services.Players;

/// <summary>Outbound Steam and Discord proof exchange shared by the bot and panel.</summary>
internal sealed class SharedAccountVerification(IHttpClientFactory clients)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    internal static readonly Uri Origin = new("https://auth.se-servers.net/");
    internal sealed record Request(string Id, string Verifier)
    {
        public Uri Link => new(Origin, "link?request=" + Id);
    }
    internal sealed record Proof(string SteamId, string DiscordId);
    private sealed record Registration(string RequestId);

    internal async Task<Request> StartAsync(string? steamId, string? discordId, string destination, string instructions, CancellationToken token)
    {
        using var client = clients.CreateClient(nameof(SharedAccountVerification));
        var verifier = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var challenge = WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        using var response = await client.PostAsJsonAsync("link/requests",
            new { steamId, discordId, destination, instructions, codeChallenge = challenge }, Json, token);
        response.EnsureSuccessStatusCode();
        var registration = await response.Content.ReadFromJsonAsync<Registration>(token);
        if (registration?.RequestId is not { Length: 43 } id || !id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            throw new InvalidDataException("The shared authentication service returned an invalid request.");
        return new(id, verifier);
    }

    // An unfinished or rate-limited request may be checked again. The broker consumes a completed proof once.
    internal async Task<Proof?> RedeemAsync(Request request, CancellationToken token)
    {
        using var client = clients.CreateClient(nameof(SharedAccountVerification));
        using var response = await client.PostAsJsonAsync("link/redeem",
            new { requestId = request.Id, codeVerifier = request.Verifier }, token);
        if (response.StatusCode is HttpStatusCode.Accepted or HttpStatusCode.TooManyRequests) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<Proof>(token)
            ?? throw new InvalidDataException("The shared authentication service did not return account proof.");
    }
}
