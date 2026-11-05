using System.Net;
using System.Security.Claims;
using Microsoft.Extensions.Logging.Abstractions;
using Quasar.Services.Auth;
using Xunit;

namespace Quasar.Tests;

public sealed class SteamProfileClientTests
{
    private const string SteamId = "76561198000000000";

    [Fact]
    public async Task PublicProfileNameSurvivesRoleRefreshWithoutChangingIdentity()
    {
        using var http = new HttpClient(new ProfileHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent($"<profile><steamID64>{SteamId}</steamID64><steamID><![CDATA[ Space & Engineer 🚀 ]]></steamID></profile>"),
        }));
        var client = new SteamProfileClient(http, NullLogger<SteamProfileClient>.Instance);
        var name = await client.GetDisplayNameAsync(SteamId, CancellationToken.None);
        Assert.Equal("Space & Engineer 🚀", name);

        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, SteamId),
            new Claim(ClaimTypes.Name, SteamId),
            new Claim(QuasarClaimTypes.Provider, QuasarAuthSchemes.Steam),
            new Claim(QuasarClaimTypes.SteamId, SteamId),
            new Claim(QuasarClaimTypes.SteamDisplayName, name!),
        ], QuasarAuthSchemes.Steam));
        var refreshed = QuasarRoleMapper.ReplaceRoles(principal, [QuasarRoles.Viewer]);

        Assert.Equal(name, refreshed.GetQuasarDisplayName());
        Assert.Equal(SteamId, refreshed.Identity?.Name);
        Assert.Equal(SteamId, refreshed.FindFirstValue(ClaimTypes.NameIdentifier));
        Assert.True(refreshed.IsInRole(QuasarRoles.Viewer));
    }

    [Theory]
    [InlineData("<profile><steamID64>76561198000000001</steamID64><steamID>Wrong user</steamID></profile>")]
    [InlineData("<profile><steamID64>76561198000000000</steamID64><steamID> </steamID></profile>")]
    [InlineData("<profile><steamID64>76561198000000000</steamID64></profile>")]
    [InlineData("<response><error>Profile unavailable</error></response>")]
    [InlineData("<html>")]
    [InlineData("<!DOCTYPE profile [<!ENTITY name SYSTEM 'file:///etc/passwd'>]><profile><steamID>&name;</steamID></profile>")]
    public async Task MissingOrInvalidProfilesFallBackToSteamId(string xml)
    {
        using var http = new HttpClient(new ProfileHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(xml),
        }));
        var client = new SteamProfileClient(http, NullLogger<SteamProfileClient>.Instance);

        Assert.Null(await client.GetDisplayNameAsync(SteamId, CancellationToken.None));
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(QuasarClaimTypes.SteamId, SteamId)]));
        Assert.Equal($"Steam {SteamId}", principal.GetQuasarDisplayName());
    }

    [Fact]
    public async Task HttpFailureDoesNotBlockSignIn()
    {
        using var http = new HttpClient(new ProfileHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        var client = new SteamProfileClient(http, NullLogger<SteamProfileClient>.Instance);

        Assert.Null(await client.GetDisplayNameAsync(SteamId, CancellationToken.None));
    }

    [Fact]
    public async Task LookupTimeoutDoesNotBlockSignIn()
    {
        using var http = new HttpClient(new ProfileHandler(_ => throw new TaskCanceledException("Timed out")));
        var client = new SteamProfileClient(http, NullLogger<SteamProfileClient>.Instance);

        Assert.Null(await client.GetDisplayNameAsync(SteamId, CancellationToken.None));
    }

    [Theory]
    [InlineData("../other-profile")]
    [InlineData("7656119800000000a")]
    public async Task InvalidSteamIdsNeverSendRequests(string steamId)
    {
        using var http = new HttpClient(new ProfileHandler(_ => throw new InvalidOperationException("Unexpected request")));
        var client = new SteamProfileClient(http, NullLogger<SteamProfileClient>.Instance);

        Assert.Null(await client.GetDisplayNameAsync(steamId, CancellationToken.None));
    }

    [Theory]
    [InlineData(" 76561198000000000 ")]
    [InlineData("https://steamcommunity.com/profiles/76561198000000000/")]
    [InlineData("http://www.steamcommunity.com/profiles/76561198000000000?l=english#profile")]
    public async Task NumericIdsAndProfileUrlsResolveWithoutNetwork(string input)
    {
        using var http = new HttpClient(new ProfileHandler(_ => throw new InvalidOperationException("Unexpected request")));
        var client = new SteamProfileClient(http, NullLogger<SteamProfileClient>.Instance);

        Assert.True(SteamProfileClient.IsSupportedInput(input));
        Assert.Equal(SteamId, await client.ResolveSteamIdAsync(input));
    }

    [Theory]
    [InlineData("https://steamcommunity.com/id/rex00991/")]
    [InlineData(" http://www.steamcommunity.com/id/rex00991?l=english#profile ")]
    public async Task VanityUrlsResolveUsingCanonicalSteamEndpoint(string input)
    {
        using var http = new HttpClient(new ProfileHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent($"<profile><steamID64>{SteamId}</steamID64></profile>"),
        }, "id/rex00991"));
        var client = new SteamProfileClient(http, NullLogger<SteamProfileClient>.Instance);

        Assert.True(SteamProfileClient.IsSupportedInput(input));
        Assert.Equal(SteamId, await client.ResolveSteamIdAsync(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("rex00991")]
    [InlineData("7656119800000000a")]
    [InlineData("７６５６１１９８０００００００００")]
    [InlineData("https://steamcommunity.com.evil.test/id/rex00991")]
    [InlineData("https://steamcommunity.com@localhost/id/rex00991")]
    [InlineData("https://user@steamcommunity.com/id/rex00991")]
    [InlineData("https://steamcommunity.com:8080/id/rex00991")]
    [InlineData("ftp://steamcommunity.com/id/rex00991")]
    [InlineData("https://steamcommunity.com/groups/rex00991")]
    [InlineData("https://steamcommunity.com/id/rex00991/friends")]
    [InlineData("https://steamcommunity.com/id/rex%2f00991")]
    [InlineData("https://steamcommunity.com/profiles/rex00991")]
    [InlineData("https://steamcommunity.com/profiles/7656119800000000")]
    public async Task InvalidProfileInputsNeverSendRequests(string? input)
    {
        using var http = new HttpClient(new ProfileHandler(_ => throw new InvalidOperationException("Unexpected request")));
        var client = new SteamProfileClient(http, NullLogger<SteamProfileClient>.Instance);

        Assert.False(SteamProfileClient.IsSupportedInput(input));
        await Assert.ThrowsAsync<ArgumentException>(() => client.ResolveSteamIdAsync(input));
    }

    [Theory]
    [InlineData("<response><error>Profile not found</error></response>")]
    [InlineData("<profile><steamID64>arbitrary data</steamID64></profile>")]
    [InlineData("<profile><steamID64>7656119800000000</steamID64></profile>")]
    [InlineData("<profile><steamID>Display name only</steamID></profile>")]
    [InlineData("<html>")]
    [InlineData("<!DOCTYPE profile [<!ENTITY id SYSTEM 'file:///etc/passwd'>]><profile><steamID64>&id;</steamID64></profile>")]
    public async Task InvalidLookupResultsAreRejected(string xml)
    {
        using var http = new HttpClient(new ProfileHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(xml),
        }, "id/rex00991"));
        var client = new SteamProfileClient(http, NullLogger<SteamProfileClient>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.ResolveSteamIdAsync("https://steamcommunity.com/id/rex00991/"));
    }

    [Fact]
    public async Task LookupTimeoutShowsResolutionError()
    {
        using var http = new HttpClient(new ProfileHandler(_ => throw new TaskCanceledException("Timed out"), "id/rex00991"));
        var client = new SteamProfileClient(http, NullLogger<SteamProfileClient>.Instance);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => client.ResolveSteamIdAsync("https://steamcommunity.com/id/rex00991/"));
        Assert.Contains("17-digit SteamID64", error.Message);
    }

    private sealed class ProfileHandler(Func<HttpRequestMessage, HttpResponseMessage> respond, string path = $"profiles/{SteamId}") : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal($"https://steamcommunity.com/{path}?xml=1", request.RequestUri?.AbsoluteUri);
            return Task.FromResult(respond(request));
        }
    }
}
