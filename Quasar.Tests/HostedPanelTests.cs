extern alias Bootstrap;
extern alias Core;
extern alias Host;

using System.Net;
using System.Reflection;
using System.Security.Claims;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using AspNet.Security.OpenId.Steam;
using Magnetar.Protocol.Runtime;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Quasar.Hosting;
using Quasar.Networking;
using Quasar.Services;
using Quasar.Services.Auth;
using Quasar.Services.Plugins;
using Xunit;
using QuasarControlAddress = Core::Quasar.Networking.QuasarControlAddress;
using Connector = Bootstrap::Quasar.Bootstrap.HostedPanelConnector;
using ConnectorState = Bootstrap::Quasar.Hosting.HostedPanelState;

namespace Quasar.Tests;

[Collection("Exact server creation")]
public sealed partial class HostedPanelTests : IDisposable
{
    private readonly FieldInfo cache = typeof(MagnetarPaths).GetField("_cachedQuasarDirectory", BindingFlags.NonPublic | BindingFlags.Static)!;
    private readonly object? previous;
    private readonly string root = Path.Combine(Path.GetTempPath(), "quasar-hosted-" + Guid.NewGuid().ToString("N"));
    public HostedPanelTests() { previous = cache.GetValue(null); cache.SetValue(null, root); }
    private static HostedPanelState State => new() { Choice = "hosted", PanelId = "my-server-panel", Credential = "my-server-panel." + new string('a', 64) };

    [Fact]
    public async Task HostedPrefixRequiresPrivateLocalConnectorAndNeverUsesTrustedNetworkBypass()
    {
        var state = State;
        await HostedPanelState.SaveAsync(state);
        var context = Request(state);
        bool reached = false;
        await HostedPanelRequest.InvokeAsync(context, next => { reached = true; return Task.CompletedTask; }, new());
        Assert.True(reached);
        Assert.Equal("/api/backup/download", context.Request.Path);
        Assert.Equal("/my-server-panel", context.Request.PathBase);
        var trusted = new TrustedNetworkEvaluator(new(), new WebServiceOptions());
        trusted.CaptureOriginalRequest(context, new ForwardedHeadersOptions());
        Assert.False(trusted.IsTrusted(context));
        Assert.False(context.Request.Headers.ContainsKey(HostedPanelRequest.Header));
        Assert.Equal("/my-server-panel/", context.Response.Headers["Service-Worker-Allowed"]);
    }

    [Theory]
    [InlineData("wrong", "127.0.0.1", "/my-server-panel/api/backup/download", true)]
    [InlineData(null, "203.0.113.1", "/my-server-panel/api/backup/download", true)]
    [InlineData(null, "127.0.0.1", "/other-panel/api/backup/download", true)]
    [InlineData(null, "127.0.0.1", "/my-server-panel/api/backup/download", false)]
    public async Task InvalidProofPeerPrefixOrDisabledAuthIsDenied(string? proof, string peer, string path, bool authEnabled)
    {
        var state = State;
        await HostedPanelState.SaveAsync(state);
        var context = Request(state);
        context.Connection.RemoteIpAddress = IPAddress.Parse(peer);
        context.Request.Path = path;
        if (proof is not null) context.Request.Headers[HostedPanelRequest.Header] = proof;
        await HostedPanelRequest.InvokeAsync(context, _ => throw new Exception("Must not forward"), new() { Enabled = authEnabled });
        Assert.Equal(403, context.Response.StatusCode);
    }

    [Fact]
    public async Task HostedTransportRetainsOriginGateAndMachineDestinationPolicy()
    {
        var state = State;
        await HostedPanelState.SaveAsync(state);
        var policy = new MachineEndpointPolicy(["127.0.0.1"], ["127.0.0.1:8080"]);
        foreach (bool withOrigin in new[] { false, true })
        {
            var context = Request(state);
            context.Request.Path = "/my-server-panel/ws/agent";
            context.Request.Host = new("dash.se-servers.net");
            if (withOrigin) context.Request.Headers.Origin = "https://dash.se-servers.net";
            bool reached = false;
            await HostedPanelRequest.InvokeAsync(context,
                next => MachineEndpointSecurity.InvokeAsync(next, _ => { reached = true; return Task.CompletedTask; }, policy), new());
            Assert.Equal(!withOrigin, reached);
            if (withOrigin) Assert.Equal(403, context.Response.StatusCode);
        }
    }

    [Fact]
    public async Task HostedCookiesHavePanelNamesAndPathsWhileLocalCookiesKeepTheirName()
    {
        var state = State;
        await HostedPanelState.SaveAsync(state);
        var context = Request(state);
        await HostedPanelRequest.InvokeAsync(context, _ => Task.CompletedTask, new());
        var cookies = new HostedPanelCookieManager();
        cookies.AppendResponseCookie(context, "Quasar.Auth", "protected-ticket", new CookieOptions());
        string value = context.Response.Headers.SetCookie.ToString();
        Assert.Contains("Quasar.Auth.my-server-panel=", value);
        Assert.Contains("path=/my-server-panel/", value);
        Assert.Contains("secure", value);
        var local = new DefaultHttpContext();
        cookies.AppendResponseCookie(local, "Quasar.Auth", "local-ticket", new CookieOptions());
        Assert.StartsWith("Quasar.Auth=", local.Response.Headers.SetCookie.ToString());
    }

    [Fact]
    public void ConnectorAlwaysVerifiesTlsAndCarriesOnlyItsOwnPrefix()
    {
        var config = Connector.BuildConfiguration(new ConnectorState { Choice = "hosted", PanelId = "my-server-panel",
            Credential = "my-server-panel." + new string('a', 64), LocalProxySecret = new string('B', 64) }, 8080, "/private/roots.pem");
        Assert.Contains("transport.protocol = \"wss\"", config);
        Assert.Contains("transport.tls.trustedCaFile = \"/private/roots.pem\"", config);
        Assert.Contains("locations = [\"/my-server-panel/\"]", config);
        Assert.Contains("requestHeaders.set.X-Quasar-Hosted", config);
    }

    [Theory]
    [InlineData("/api/health", "https://dash.test/my-panel/api/health")]
    [InlineData("/_quasar/plugins/viewer/index.html", "https://dash.test/my-panel/_quasar/plugins/viewer/index.html")]
    [InlineData("https://example.com/help", "https://example.com/help")]
    public void PluginAndApplicationLinksUseTheirPanelBase(string input, string expected) =>
        Assert.Equal(expected, new Navigation().PanelUrl(input));

    [Theory]
    [InlineData("https://steamcommunity.com/openid/id/76561198000000001", "76561198000000001")]
    [InlineData("http://steamcommunity.com/openid/id/76561198000000001", "76561198000000001")]
    [InlineData("76561198000000001", null)]
    [InlineData("https://example.test/openid/id/76561198000000001", null)]
    [InlineData("https://steamcommunity.com/openid/id/76561198000000001/", null)]
    [InlineData(null, null)]
    public void SteamIdentityComesFromTheProviderClaimedIdentifier(string? identifier, string? expected) =>
        Assert.Equal(expected, SteamAuthConstants.GetClaimedSteamId(identifier));

    [Fact]
    public async Task RealMiddlewareRoutesWithPathBaseChallengesSteamAndPreservesAuthenticatedApis()
    {
        var state = State;
        await HostedPanelState.SaveAsync(state);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(new QuasarAuthOptions());
        builder.Services.AddSingleton<RbacConfigCatalog>();
        builder.Services.AddSingleton<QuasarRoleMapper>();
        builder.Services.AddSingleton<QuasarBrowserSessions>();
        builder.Services.AddAuthentication(QuasarAuthSchemes.Cookie).AddCookie(QuasarAuthSchemes.Cookie, options =>
        {
            Quasar.Program.ConfigureDashboardLogin(options);
            options.Cookie.Name = "Quasar.Auth";
            options.CookieManager = new HostedPanelCookieManager();
            options.LoginPath = "/login";
        }).AddSteam(options => options.SignInScheme = QuasarAuthSchemes.Cookie);
        builder.Services.AddAuthorization();
        builder.Services.AddAntiforgery();
        await using var app = builder.Build();
        app.Use((context, next) =>
        {
            context.Connection.RemoteIpAddress = IPAddress.Loopback;
            context.Connection.LocalPort = 8080;
            return HostedPanelRequest.InvokeAsync(context, next, new());
        });
        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapGet("/api/plugin/download", (HttpContext context) => Results.Json(new { pathBase = context.Request.PathBase.Value })).RequireAuthorization();
        Quasar.Program.MapAuthenticationEndpoints(app, new());
        app.MapGet("/fixture-signin", async context =>
            await context.SignInAsync(QuasarAuthSchemes.Cookie, new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "verified-steam-fixture")], "Steam"))));
        var bindings = new List<QuasarBrowserSession>();
        app.MapGet("/fixture-bind", async (HttpContext http, QuasarBrowserSessions sessions) =>
        {
            bindings.Add(await sessions.BindHttpSessionAsync(http));
            return Results.NoContent();
        }).RequireAuthorization();
        await app.StartAsync();
        using var client = app.GetTestClient();
        client.BaseAddress = new Uri("https://dash.se-servers.net");
        client.DefaultRequestHeaders.Add(HostedPanelRequest.Header, state.LocalProxySecret);
        using var unauthorized = await client.GetAsync("/my-server-panel/api/plugin/download");
        Assert.Equal(HttpStatusCode.Redirect, unauthorized.StatusCode);
        Assert.Equal("/my-server-panel/login", unauthorized.Headers.Location!.AbsolutePath);
        using var steam = await client.GetAsync("/my-server-panel/login");
        Assert.Equal("steamcommunity.com", steam.Headers.Location!.Host);
        Assert.Contains(Uri.EscapeDataString("https://dash.se-servers.net/my-server-panel/signin-steam"), steam.Headers.Location.Query);
        using var signin = await client.GetAsync("/my-server-panel/fixture-signin");
        client.DefaultRequestHeaders.Add("Cookie", signin.Headers.GetValues("Set-Cookie").First(x => x.StartsWith("Quasar.Auth.")).Split(';')[0]);
        var data = await client.GetFromJsonAsync<System.Text.Json.JsonElement>("/my-server-panel/api/plugin/download");
        Assert.Equal("/my-server-panel", data.GetProperty("pathBase").GetString());
        using var otherLogin = app.GetTestClient();
        otherLogin.BaseAddress = client.BaseAddress;
        otherLogin.DefaultRequestHeaders.Add(HostedPanelRequest.Header, state.LocalProxySecret);
        using var independent = await otherLogin.GetAsync("/my-server-panel/fixture-signin");
        otherLogin.DefaultRequestHeaders.Add("Cookie", independent.Headers.GetValues("Set-Cookie").First(x => x.StartsWith("Quasar.Auth.")).Split(';')[0]);
        Assert.Equal(HttpStatusCode.NoContent, (await client.GetAsync("/my-server-panel/fixture-bind")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.GetAsync("/my-server-panel/fixture-bind")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await otherLogin.GetAsync("/my-server-panel/fixture-bind")).StatusCode);
        Assert.Same(bindings[0], bindings[1]);
        Assert.NotSame(bindings[0], bindings[2]);
        using var logoutGet = await client.GetAsync("/my-server-panel/logout");
        Assert.Equal(HttpStatusCode.MethodNotAllowed, logoutGet.StatusCode);
        using var missingToken = await client.PostAsync("/my-server-panel/logout", new FormUrlEncodedContent([]));
        Assert.Equal(HttpStatusCode.BadRequest, missingToken.StatusCode);
        using var denied = await client.GetAsync("/my-server-panel/access-denied");
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        string html = await denied.Content.ReadAsStringAsync();
        Assert.Contains("action=\"/my-server-panel/logout\"", html);
        Assert.Contains("href=\"/my-server-panel/Quasar.png\"", html);
        var token = System.Text.RegularExpressions.Regex.Match(html, "name=\"(?<name>__RequestVerificationToken)\" value=\"(?<token>[^\"]+)\"");
        Assert.True(token.Success);
        string cookie = client.DefaultRequestHeaders.GetValues("Cookie").Single() + "; "
            + denied.Headers.GetValues("Set-Cookie").Single(value => value.Contains("Antiforgery", StringComparison.Ordinal)).Split(';')[0];
        client.DefaultRequestHeaders.Remove("Cookie"); client.DefaultRequestHeaders.Add("Cookie", cookie);
        using var logout = await client.PostAsync("/my-server-panel/logout", new FormUrlEncodedContent([
            new(token.Groups["name"].Value, WebUtility.HtmlDecode(token.Groups["token"].Value))]));
        Assert.Equal(HttpStatusCode.Redirect, logout.StatusCode);
        Assert.Equal("/my-server-panel/", new Uri(client.BaseAddress, logout.Headers.Location!).AbsolutePath);
        Assert.True(bindings[0].Revoked.IsCancellationRequested);
        Assert.False(bindings[2].Revoked.IsCancellationRequested);
        // TestServer's client deliberately retains the original HTTP cookie on this request.
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/my-server-panel/api/plugin/download")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await otherLogin.GetAsync("/my-server-panel/api/plugin/download")).StatusCode);


    }

    [Fact]
    public async Task LoginAndLogoutSurviveWorkerReplacementAndStayWithinThePanel()
    {
        async Task<WebApplication> StartWorkerAsync()
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Services.AddSingleton(new QuasarAuthOptions());
            builder.Services.AddSingleton<RbacConfigCatalog>();
            builder.Services.AddSingleton<QuasarRoleMapper>();
            builder.Services.AddSingleton<QuasarBrowserSessions>();
            builder.Services.AddDataProtection().SetApplicationName("QuasarLoginTest")
                .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(root, "keys")));
            builder.Services.AddAuthentication(QuasarAuthSchemes.Cookie)
                .AddCookie(QuasarAuthSchemes.Cookie, Quasar.Program.ConfigureDashboardLogin);
            builder.Services.AddAuthorization();
            var app = builder.Build();
            app.UsePathBase("/panel");
            app.UseRouting();
            app.UseAuthentication();
            app.UseAuthorization();
            app.MapPost("/fixture/login", (HttpContext http, QuasarRoleMapper roles) =>
                http.SignInAsync(QuasarAuthSchemes.Cookie, roles.CreateSteamPrincipal("76561198000000001")));
            app.MapPost("/fixture/logout", (HttpContext http) => http.SignOutAsync(QuasarAuthSchemes.Cookie)).RequireAuthorization();
            app.MapGet("/account", () => Results.Ok()).RequireAuthorization();
            await app.StartAsync();
            return app;
        }

        string cookie;
        await using (var first = await StartWorkerAsync())
        {
            using var browser = first.GetTestClient();
            using var login = await browser.PostAsync("/panel/fixture/login", null);
            cookie = Assert.Single(login.Headers.GetValues("Set-Cookie")).Split(';')[0];
        }
        await using (var second = await StartWorkerAsync())
        {
            using var browser = second.GetTestClient();
            browser.DefaultRequestHeaders.Add("Cookie", cookie);
            Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/panel/account")).StatusCode);
            Assert.Equal(HttpStatusCode.Redirect, (await browser.GetAsync("/account")).StatusCode);
            (await browser.PostAsync("/panel/fixture/logout", null)).EnsureSuccessStatusCode();
        }
        await using var third = await StartWorkerAsync();
        using var signedOut = third.GetTestClient();
        signedOut.DefaultRequestHeaders.Add("Cookie", cookie);
        Assert.Equal(HttpStatusCode.Redirect, (await signedOut.GetAsync("/panel/account")).StatusCode);
    }

    [Fact]
    public async Task SelfDeployedInstallationCanResumeItsExistingHostedIdentity()
    {
        var state = State with { Choice = "self", EnrollmentId = "existing-enrollment", BrowserUrl = HostedPanelState.Origin + "/account" };
        await HostedPanelState.SaveAsync(state);
        var services = new ServiceCollection();
        services.AddHttpClient("Nebula");
        await using var provider = services.BuildServiceProvider();
        using var service = new HostedPanelService(provider.GetRequiredService<IHttpClientFactory>(), NullLogger<HostedPanelService>.Instance);
        Assert.Equal(state.BrowserUrl, await service.EnrollAsync(state.PanelId));
        Assert.Equal("hosted", HostedPanelState.Read()!.Choice);
        Assert.Equal(state.Credential, HostedPanelState.Read()!.Credential);
        Assert.Equal(state.InstallationSecret, HostedPanelState.Read()!.InstallationSecret);
        if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(HostedPanelState.StatePath));
    }

    [Fact]
    public async Task CorruptHostedStateCannotPreventLocalQuasarOrOverwriteThePrivateBackup()
    {
        Directory.CreateDirectory(HostedPanelState.DirectoryPath);
        await File.WriteAllTextAsync(HostedPanelState.StatePath, "{broken");
        Assert.Null(HostedPanelState.Read());
        var context = new DefaultHttpContext();
        bool reached = false;
        await HostedPanelRequest.InvokeAsync(context, _ => { reached = true; return Task.CompletedTask; }, new());
        Assert.True(reached);
        Assert.Equal("{broken", await File.ReadAllTextAsync(HostedPanelState.StatePath));
    }

    [Fact]
    public void HostAndAgentControlAddressesRetainOnlyTheApprovedHostedPrefix()
    {
        var address = QuasarControlAddress.Parse("https://dash.se-servers.net/my-server-panel/");
        Assert.Equal("/my-server-panel/api/v1/hosts/one/connect", QuasarControlAddress.Endpoint(address, "api/v1/hosts/one/connect").AbsolutePath);
        Assert.Equal("wss://dash.se-servers.net/my-server-panel/api/v1/hosts/one/connect",
            Host::Quasar.Host.HostConnection.Url(address, "one", "connect").AbsoluteUri);
        Assert.Equal("/my-server-panel/ws/agent", QuasarControlAddress.Endpoint(address, "ws/agent").AbsolutePath);
        Assert.Throws<ArgumentException>(() => QuasarControlAddress.Parse("https://dash.se-servers.net/my-server-panel/../other/"));
        Assert.Throws<ArgumentException>(() => QuasarControlAddress.Parse("https://other.example/my-server-panel/"));
        Assert.Throws<ArgumentException>(() => QuasarControlAddress.Parse("https://dash.se-servers.net/my%2Fpanel/"));
    }

    [Fact]
    public void FrpcRegistrationAndFreshWorkerHealthAreRequiredForReadiness()
    {
        using var good = System.Text.Json.JsonDocument.Parse("{\"http\":[{\"name\":\"quasar\",\"status\":\"running\"}]}");
        Assert.True(Connector.Registered(good.RootElement));
        using var starting = System.Text.Json.JsonDocument.Parse("{\"http\":[{\"name\":\"quasar\",\"status\":\"wait start\"}]}");
        Assert.False(Connector.Registered(starting.RootElement));
        Assert.False(new HostedConnectorHealth("my-server-panel", true, true, DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeSeconds()).Ready);
        Assert.False(new HostedConnectorHealth("my-server-panel", true, false, DateTimeOffset.UtcNow.ToUnixTimeSeconds()).Ready);
    }

    private static DefaultHttpContext Request(HostedPanelState state)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Loopback;
        context.Connection.LocalPort = 8080;
        context.Request.Host = new("127.0.0.1:8080");
        context.Request.Scheme = "http";
        context.Request.Path = "/my-server-panel/api/backup/download";
        context.Request.Headers[HostedPanelRequest.Header] = state.LocalProxySecret;
        return context;
    }
    private sealed class Navigation : NavigationManager
    {
        public Navigation() => Initialize("https://dash.test/my-panel/", "https://dash.test/my-panel/settings/security");
        protected override void NavigateToCore(string uri, bool forceLoad) { }
    }
    public void Dispose() { cache.SetValue(null, previous); if (Directory.Exists(root)) Directory.Delete(root, true); }
}
