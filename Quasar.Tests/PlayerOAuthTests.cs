using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Quasar.Services;
using Quasar.Services.Auth;
using Quasar.Services.Players;
using Xunit;

namespace Quasar.Tests;

public sealed class PlayerOAuthTests : IDisposable
{
    private const string Steam = "76561198000000001";
    private readonly string directory = Path.Combine(Path.GetTempPath(), "player-oauth-" + Guid.NewGuid().ToString("N"));
    private readonly PlayerAccessCatalog catalog;
    private readonly QuasarPlayerLinks links;
    private readonly ServiceProvider services;
    private readonly IdentityProof authentication = new();

    public PlayerOAuthTests()
    {
        catalog = new(Path.Combine(directory, "access.json"), new EphemeralDataProtectionProvider());
        links = new(Path.Combine(directory, "links.json"));
        var registrations = new ServiceCollection().AddLogging();
        registrations.AddPlayerAuthentication();
        registrations.AddSingleton(catalog);
        registrations.AddSingleton(links);
        registrations.AddSingleton<IAuthenticationService>(authentication);
        registrations.AddSingleton(new QuasarAuthOptions { Enabled = true, Steam = new() { Enabled = true } });
        registrations.AddSingleton(p => new HostedPanelService(p.GetRequiredService<IHttpClientFactory>(), NullLogger<HostedPanelService>.Instance));
        services = registrations.BuildServiceProvider();
    }

    public void Dispose() { services.Dispose(); if (Directory.Exists(directory)) Directory.Delete(directory, true); }

    private async Task<OAuthOptions> ConfigureAsync()
    {
        await catalog.SaveSettingsAsync(new() { PublicUrl = "https://quasar.example.com" });
        await catalog.RecordVerificationAsync("https://quasar.example.com", DateTimeOffset.UtcNow.AddDays(1), default);
        return services.GetRequiredService<IOptionsMonitor<OAuthOptions>>().Get(PlayerAuthEndpoints.DiscordScheme);
    }

    private OAuthCreatingTicketContext Ticket(OAuthOptions options, string expectedSteam, string host = "quasar.example.com")
    {
        var http = new DefaultHttpContext { RequestServices = services };
        http.Request.Scheme = "https"; http.Request.Host = new(host);
        var properties = new AuthenticationProperties { RedirectUri = "/player-auth" };
        properties.Items["steam"] = expectedSteam;
        var principal = new ClaimsPrincipal(new ClaimsIdentity(PlayerAuthEndpoints.DiscordScheme));
        return new(principal, properties, http,
            new AuthenticationScheme(PlayerAuthEndpoints.DiscordScheme, null, typeof(OAuthHandler<OAuthOptions>)), options,
            new HttpClient(new DiscordIdentity()), OAuthTokenResponse.Success(JsonDocument.Parse("{\"access_token\":\"proof\",\"token_type\":\"Bearer\"}")), default);
    }

    [Fact]
    public async Task DiscordProofBindsTheSteamSessionAndCannotCreateAPanelPrincipal()
    {
        var options = await ConfigureAsync();
        Assert.True(options.UsePkce);
        Assert.Equal(["identify"], options.Scope);
        Assert.False(options.SaveTokens);
        Assert.Equal("quasar-player-links", options.ClientId);
        Assert.Equal("https://auth.se-servers.net/discord/authorize", options.AuthorizationEndpoint);
        Assert.Equal("https://auth.se-servers.net/discord/token", options.TokenEndpoint);
        Assert.Equal(PlayerAuthEndpoints.ExternalCookie, options.SignInScheme);
        var ticket = Ticket(options, Steam);
        await options.Events.CreatingTicket(ticket);
        Assert.Equal("111", links.FindBySteamId(Steam)!.DiscordUserId);
        Assert.Empty(ticket.Principal!.FindAll(ClaimTypes.Role));
        var received = new TicketReceivedContext(ticket.HttpContext, ticket.Scheme, options,
            new AuthenticationTicket(ticket.Principal, ticket.Properties, PlayerAuthEndpoints.DiscordScheme)) { ReturnUri = "/player-auth" };
        await options.Events.TicketReceived(received);
        Assert.True(received.Result!.Handled);
        Assert.Equal(0, authentication.SignIns);
        Assert.Equal("/player-auth", ticket.Response.Headers.Location);
    }

    [Fact]
    public async Task SessionSwapsTrustedNetworkIdentitiesAndWrongOriginsCannotLink()
    {
        var options = await ConfigureAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => options.Events.CreatingTicket(Ticket(options, "76561198000000002")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => options.Events.CreatingTicket(Ticket(options, Steam, "attacker.example.com")));
        authentication.SteamProof = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => options.Events.CreatingTicket(Ticket(options, Steam)));
        Assert.Empty(links.GetLinks());
    }

    [Fact]
    public async Task GameRequestsAreBoundSingleUseAndUnavailableWithoutVerifiedPublicAccess()
    {
        await ConfigureAsync();
        var browser = services.GetRequiredService<PlayerAuthBrowser>();
        var url = browser.Begin("server:alpha", Steam);
        Assert.Equal(url, browser.Begin("server:alpha", Steam));
        var key = url.Split("?request=")[1];
        Assert.Equal(64, key.Length);
        var request = browser.Find(key)!;
        Assert.Equal(Steam, request.SteamId);
        Assert.Equal("server:alpha", request.Target);
        Assert.InRange(request.ExpiresAt - DateTimeOffset.UtcNow, TimeSpan.FromMinutes(19), TimeSpan.FromMinutes(20));
        browser.Consume(key);
        Assert.Null(browser.Find(key));
        var changed = catalog.GetSettings(); changed.PublicUrl = "https://changed.example.com";
        await catalog.SaveSettingsAsync(changed);
        Assert.False(browser.Ready);
        Assert.Throws<InvalidOperationException>(() => browser.Begin("server:alpha", Steam));
        Assert.Null(browser.ChallengeProof("unrequested-challenge"));
    }

    [Fact]
    public async Task UnlinkRequiresSteamProofAndAntiforgeryButWorksDuringAnAuthenticationOutage()
    {
        await ConfigureAsync();
        await links.AuthenticateAsync(Steam, "111");
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(catalog);
        builder.Services.AddSingleton(links);
        builder.Services.AddSingleton(services.GetRequiredService<PlayerAuthBrowser>());
        builder.Services.AddSingleton<PlayerAccessService>(_ => throw new InvalidOperationException("Unlink must not require role lookup."));
        builder.Services.AddAuthentication(QuasarAuthSchemes.Cookie).AddCookie(QuasarAuthSchemes.Cookie);
        builder.Services.AddSingleton<IAuthenticationService>(authentication);
        builder.Services.AddAuthorization();
        builder.Services.AddAntiforgery();
        builder.Services.AddRateLimiter(options => options.AddConcurrencyLimiter("player-auth", limiter => limiter.PermitLimit = 10));
        await using var app = builder.Build();
        app.UseAuthentication(); app.UseAuthorization(); app.UseRateLimiter(); app.UseAntiforgery();
        app.MapPlayerAuthentication();
        app.MapGet("/csrf", (HttpContext context, IAntiforgery antiforgery) =>
        {
            var csrf = antiforgery.GetAndStoreTokens(context);
            return Results.Json(new { csrf.FormFieldName, csrf.RequestToken });
        });
        await app.StartAsync();
        using var client = app.GetTestClient();
        client.BaseAddress = new("https://quasar.example.com");
        using var missing = await client.PostAsync("/player-auth/unlink", new FormUrlEncodedContent([]));
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        using var csrfResponse = await client.GetAsync("/csrf");
        using var csrf = JsonDocument.Parse(await csrfResponse.Content.ReadAsStringAsync());
        var field = csrf.RootElement.GetProperty("formFieldName").GetString()!;
        var token = csrf.RootElement.GetProperty("requestToken").GetString()!;
        client.DefaultRequestHeaders.Add("Cookie", csrfResponse.Headers.GetValues("Set-Cookie").Single().Split(';')[0]);
        var changed = catalog.GetSettings(); changed.PublicUrl = "https://changed.example.com";
        await catalog.SaveSettingsAsync(changed);
        authentication.SteamProof = false;
        using var networkIdentity = await client.PostAsync("/player-auth/unlink", new FormUrlEncodedContent([new(field, token)]));
        Assert.Equal(HttpStatusCode.Unauthorized, networkIdentity.StatusCode);
        Assert.NotNull(links.FindBySteamId(Steam));
        authentication.SteamProof = true;
        using var removed = await client.PostAsync("/player-auth/unlink", new FormUrlEncodedContent([new(field, token)]));
        Assert.Equal(HttpStatusCode.Redirect, removed.StatusCode);
        Assert.Null(links.FindBySteamId(Steam));
    }

    private sealed class DiscordIdentity : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Assert.Equal("https://auth.se-servers.net/discord/userinfo", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"id\":\"111\"}") });
        }
    }

    private sealed class IdentityProof : IAuthenticationService
    {
        public bool SteamProof = true;
        public int SignIns;
        public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme)
        {
            Assert.Equal(QuasarAuthSchemes.Cookie, scheme);
            var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, Steam), new Claim(QuasarClaimTypes.SteamId, Steam),
                new Claim(QuasarClaimTypes.Provider, SteamProof ? QuasarAuthSchemes.Steam : QuasarAuthSchemes.TrustedNetwork)], "cookie");
            return Task.FromResult(AuthenticateResult.Success(new(new ClaimsPrincipal(identity), scheme!)));
        }
        public Task SignInAsync(HttpContext context, string? scheme, ClaimsPrincipal principal, AuthenticationProperties? properties) { SignIns++; return Task.CompletedTask; }
        public Task SignOutAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;
        public Task ChallengeAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;
        public Task ForbidAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;
    }
}
