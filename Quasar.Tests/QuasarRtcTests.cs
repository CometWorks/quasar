using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using Magnetar.Protocol.Runtime;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Quasar.Hosting;
using Quasar.Services;
using Quasar.Services.Auth;
using Quasar.Services.Plugins;
using Xunit;

namespace Quasar.Tests;

[Collection("Exact server creation")]
public sealed partial class QuasarRtcTests : IDisposable
{
    private const string Prefix = "/rtc-test-panel";
    private const string Alice = "76561198000000001";
    private const string Bob = "76561198000000002";
    private readonly string root = Path.Combine(Path.GetTempPath(), "quasar-rtc-" + Guid.NewGuid().ToString("N"));
    private readonly string fixtureKey = Guid.NewGuid().ToString("N");
    private readonly FieldInfo cache = typeof(MagnetarPaths).GetField("_cachedQuasarDirectory", BindingFlags.NonPublic | BindingFlags.Static)!;
    private readonly object? previous;
    private readonly RbacConfigCatalog rbac;

    public QuasarRtcTests()
    {
        previous = cache.GetValue(null);
        cache.SetValue(null, root);
        rbac = new(NullLogger<RbacConfigCatalog>.Instance);
    }

    [Fact]
    public async Task RtcSetupRequiresAuthenticationAntiforgeryAndValidDescriptions()
    {
        await using var app = await StartAsync(network: false);
        using var client = app.GetTestClient();
        var endpoint = Prefix + QuasarSdkEndpoints.Prefix + "/rtc/attempts";
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync(endpoint, new { })).StatusCode);
        client.DefaultRequestHeaders.Add("X-Fixture-Key", fixtureKey);
        using var login = await client.PostAsync(Prefix + "/fixture/login", null);
        var cookies = login.Headers.GetValues("Set-Cookie").Select(cookie => cookie.Split(';')[0]).ToList();
        client.DefaultRequestHeaders.Add("Cookie", string.Join("; ", cookies));
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(endpoint, new { })).StatusCode);
        using var session = await client.GetAsync(Prefix + QuasarSdkEndpoints.Prefix + "/session");
        cookies.AddRange(session.Headers.GetValues("Set-Cookie").Select(cookie => cookie.Split(';')[0]));
        client.DefaultRequestHeaders.Remove("Cookie");
        client.DefaultRequestHeaders.Add("Cookie", string.Join("; ", cookies));
        var proof = await session.Content.ReadFromJsonAsync<JsonElement>();
        client.DefaultRequestHeaders.Add(proof.GetProperty("headerName").GetString()!, proof.GetProperty("requestToken").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(endpoint, new { version = 2, nonce = new string('A', 64), description = new { type = "offer", sdp = "invalid" } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(endpoint, new { version = 1, nonce = new string('A', 64), description = new { type = "offer", sdp = "invalid" } })).StatusCode);
        Assert.Equal(0, app.Services.GetRequiredService<QuasarRtcSessions>().PeerCount);
        var invalid = new QuasarRtcDescription("offer", "m=application 9 UDP/DTLS/SCTP webrtc-datachannel\r\na=fingerprint:sha-256 " + string.Join(':', Enumerable.Repeat("AA", 32))
            + "\r\na=fingerprint:sha-256 " + string.Join(':', Enumerable.Repeat("BB", 32)));
        Assert.Throws<Quasar.Plugin.Abstractions.Operations.QuasarOperationException>(() => invalid.Fingerprint());
    }

    [Fact]
    public async Task IceConfigurationOmitsAbsentStunCredentialsAndRetainsTurnCredentials()
    {
        // Exercise the worker's actual HTTP JSON writer: Pion rejects a present
        // null username before SDP negotiation, even for a STUN-only server.
        using var content = JsonContent.Create(new { iceServers = new[] {
            new QuasarRtcIce(["stun:rtc.example:3478"]),
            new QuasarRtcIce(["turn:rtc.example:3478?transport=tcp"], new string('a', 32), new string('b', 64)),
        } });
        using var document = JsonDocument.Parse(await content.ReadAsStringAsync());
        var servers = document.RootElement.GetProperty("iceServers");
        Assert.Equal("stun:rtc.example:3478", servers[0].GetProperty("urls")[0].GetString());
        Assert.False(servers[0].TryGetProperty("username", out _));
        Assert.False(servers[0].TryGetProperty("credential", out _));
        Assert.Equal(new string('a', 32), servers[1].GetProperty("username").GetString());
        Assert.Equal(new string('b', 64), servers[1].GetProperty("credential").GetString());
    }

    [Fact]
    public async Task PeerIntegrityMismatchFailsBeforeExecution()
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, OperatingSystem.IsWindows() ? "bad-peer.exe" : "bad-peer");
        await File.WriteAllTextAsync(path, "not a peer component");
        await File.WriteAllTextAsync(path + ".json", JsonSerializer.Serialize(new { protocol = 1, runtime = OperatingSystem.IsWindows() ? "win-x64" : "linux-x64", sha256 = new string('0', 64) }));
        await using var process = new QuasarPeerProcess(Microsoft.Extensions.Options.Options.Create(new QuasarRtcOptions { PeerExecutable = path }));
        var error = await Assert.ThrowsAsync<Quasar.Plugin.Abstractions.Operations.QuasarOperationException>(() => process.CreateAsync(Guid.NewGuid().ToString("N"), new("offer", "invalid"), default));
        Assert.Equal(503, error.StatusCode);
        Assert.Contains("integrity", error.Message);
    }

    [RtcPeerFact]
    public async Task HostedCredentialRemovalClosesActivePeerAndReactivationAcceptsFreshPeer()
    {
        var state = new HostedPanelState { Choice = "hosted", PanelId = Prefix[1..], EnrollmentId = "retained-enrollment",
            Credential = Prefix[1..] + "." + new string('a', 64), SetupCompleted = true };
        await HostedPanelState.SaveAsync(state);
        await using var app = await StartAsync(network: true, hosted: true);
        await BrowserAsync(app, "inactive");
        await UntilAsync(() => app.Services.GetRequiredService<QuasarRtcSessions>().PeerCount == 0);
        Assert.Null(HostedPanelState.Read()!.Credential);
        Assert.Equal(state.EnrollmentId, HostedPanelState.Read()!.EnrollmentId);
        await HostedPanelState.SaveAsync(state);
        await BrowserAsync(app, "reactivated");
        await UntilAsync(() => app.Services.GetRequiredService<QuasarRtcSessions>().PeerCount == 0);
    }

    [RtcPeerFact]
    public async Task RealBrowserPeerBindsSessionsLogoutAndHostingExpiry()
    {
        await using (var local = await StartAsync(network: true))
        {
            await BrowserAsync(local, "local");
            Assert.Equal(0, local.Services.GetRequiredService<Manager>().Calls);
            await UntilAsync(() => local.Services.GetRequiredService<QuasarRtcSessions>().PeerCount == 0);
        }
        var fakeTime = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await HostedPanelState.SaveAsync(new HostedPanelState
        {
            Choice = "hosted", PanelId = Prefix[1..], Credential = Prefix[1..] + "." + new string('a', 64),
        });
        await using var hosted = await StartAsync(network: true, fakeTime);
        await BrowserAsync(hosted, "hosted");
        Assert.True(hosted.Services.GetRequiredService<Manager>().Calls >= 2);
        await UntilAsync(() => hosted.Services.GetRequiredService<QuasarRtcSessions>().PeerCount == 0);
    }

    private async Task<WebApplication> StartAsync(bool network, FakeTimeProvider? fakeTime = null, bool hosted = false)
    {
        await rbac.UpdateAsync(config =>
        {
            config.SubjectRoleMappings = [new() { Subject = Alice, Roles = [QuasarRoles.Admin] }, new() { Subject = Bob, Roles = [QuasarRoles.Viewer] }];
            return Task.CompletedTask;
        });
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        if (network) builder.WebHost.UseUrls("http://127.0.0.1:0");
        else builder.WebHost.UseTestServer();
        RegisterDataOperations(builder.Services);
        builder.Services.AddSingleton(rbac);
        builder.Services.AddSingleton(new QuasarAuthOptions());
        builder.Services.AddSingleton<QuasarRoleMapper>();
        builder.Services.AddAuthorization(Program.ConfigureAuthorizationPolicies);
        builder.Services.AddSingleton<IAuthorizationPolicyProvider, QuasarAuthorizationPolicyProvider>();
        builder.Services.AddSingleton<QuasarBrowserSessions>();
        builder.Services.AddSingleton<QuasarTransferRegistry>();
        builder.Services.AddScoped<QuasarOperationService>();
        builder.Services.AddSingleton(new Quasar.Services.Audit.QuasarAuditOptions());
        builder.Services.AddSingleton<Quasar.Services.Audit.QuasarAuditLog>();
        builder.Services.AddScoped<QuasarHttpOperations>();
        builder.Services.AddScoped<Quasar.Plugin.Abstractions.Operations.IQuasarOperationDispatcher>(services => services.GetRequiredService<QuasarOperationService>());
        builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
        builder.Services.AddAntiforgery();
        builder.Services.AddAuthentication("test").AddCookie("test", options =>
        {
            options.Events.OnRedirectToLogin = context => QuasarSdkEndpoints.WriteAuthorizationErrorAsync(context.HttpContext, 401);
            options.Events.OnRedirectToAccessDenied = context => QuasarSdkEndpoints.WriteAuthorizationErrorAsync(context.HttpContext, 403);
        });
        if (fakeTime is not null) builder.Services.AddSingleton<TimeProvider>(fakeTime);
        builder.Services.AddQuasarRtc(builder.Configuration);
        builder.Services.PostConfigure<QuasarRtcOptions>(options =>
        {
            options.StunUrls = Environment.GetEnvironmentVariable("QUASAR_RTC_TEST_STUN") is { Length: > 0 } stun ? [stun] : [];
            options.PeerExecutable = Environment.GetEnvironmentVariable("QUASAR_RTC_TEST_PEER") ?? Path.Combine(root, "absent-peer");
        });
        var manager = new Manager();
        builder.Services.AddSingleton(manager);
        builder.Services.AddHttpClient("Nebula").ConfigurePrimaryHttpMessageHandler(() => manager);
        var app = builder.Build();
        app.Use((context, next) =>
        {
            // Model frpc's private local hop on the server. No connector proof enters JS.
            if (fakeTime is not null || hosted) context.Request.Headers[HostedPanelRequest.Header] = HostedPanelState.Read()!.LocalProxySecret;
            return HostedPanelRequest.InvokeAsync(context, next, new QuasarAuthOptions());
        });
        app.UsePathBase(Prefix);
        app.UseStaticFiles(new StaticFileOptions { FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(
            Path.GetFullPath("../../../../Quasar/wwwroot", AppContext.BaseDirectory)) });
        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapQuasarSdk();
        app.MapQuasarRtc();
        app.MapGet("/", () => Results.Content("<!doctype html><title>Isolated Quasar RTC checks</title>", "text/html"));
        var fixture = app.MapGroup("/fixture");
        fixture.AddEndpointFilter(async (context, next) =>
            context.HttpContext.Request.Headers["X-Fixture-Key"] == fixtureKey ? await next(context) : Results.NotFound());
        fixture.MapPost("/login", async (HttpContext http, QuasarRoleMapper roles) =>
        {
            var user = http.Request.Query["user"] == "bob" ? Bob : Alice;
            await http.SignInAsync("test", roles.CreateSteamPrincipal(user), new AuthenticationProperties { ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(10) });
            return Results.NoContent();
        });
        fixture.MapPost("/logout", async (HttpContext http, IAntiforgery csrf, QuasarBrowserSessions sessions) =>
        { await csrf.ValidateRequestAsync(http); await sessions.RevokeAsync(http); await http.SignOutAsync("test"); return Results.NoContent(); });
        fixture.MapGet("/count", (QuasarRtcSessions peers) => Results.Json(new { count = peers.PeerCount }));
        fixture.MapPost("/deny", async () => { await rbac.UpdateAsync(config => { config.PolicyOverrides[QuasarPolicyNames.CanView] = []; return Task.CompletedTask; }); return Results.NoContent(); });
        fixture.MapPost("/allow", async () => { await rbac.UpdateAsync(config => { config.PolicyOverrides.Remove(QuasarPolicyNames.CanView); return Task.CompletedTask; }); return Results.NoContent(); });
        fixture.MapPost("/file-deny", async () => { await rbac.UpdateAsync(config => { config.OperationOverrides["rtc.fixture/file"] = new() { Enabled = false, RequiredPolicies = [QuasarPolicyNames.CanView] }; return Task.CompletedTask; }); return Results.NoContent(); });
        fixture.MapPost("/file-allow", async () => { await rbac.UpdateAsync(config => { config.OperationOverrides.Remove("rtc.fixture/file"); return Task.CompletedTask; }); return Results.NoContent(); });
        fixture.MapPost("/outage", () => { manager.Outage = true; return Results.NoContent(); });
        fixture.MapPost("/resume", () => { manager.Outage = false; return Results.NoContent(); });
        fixture.MapPost("/replace", async () => { await HostedPanelState.SaveAsync(HostedPanelState.Read()! with { Credential = Prefix[1..] + "." + new string('b', 64) }); return Results.NoContent(); });
        fixture.MapPost("/inactive", async () => { await HostedPanelState.SaveAsync(HostedPanelState.Read()! with { Credential = null }); return Results.NoContent(); });
        fixture.MapPost("/advance", () => { fakeTime!.Advance(TimeSpan.FromSeconds(121)); return Results.NoContent(); });
        fixture.MapPost("/tick", () => { fakeTime!.Advance(TimeSpan.FromSeconds(1)); return Results.NoContent(); });
        await app.StartAsync();
        return app;
    }

    private async Task BrowserAsync(WebApplication app, string mode)
    {
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        var info = new ProcessStartInfo("node") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add("--test");
        info.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, mode == "data" ? "browser-rtc-data.test.mjs" : "browser-rtc.test.mjs"));
        info.Environment["QUASAR_RTC_TEST_URL"] = address + Prefix + "/";
        info.Environment["QUASAR_RTC_TEST_KEY"] = fixtureKey;
        info.Environment["QUASAR_RTC_TEST_MODE"] = mode;
        using var node = Process.Start(info)!;
        var output = node.StandardOutput.ReadToEndAsync();
        var error = node.StandardError.ReadToEndAsync();
        await node.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(4));
        var evidence = (await output) + (await error);
        Console.WriteLine(evidence);
        Assert.True(node.ExitCode == 0, evidence);
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition()) await Task.Delay(50, deadline.Token);
    }

    public void Dispose()
    {
        rbac.Dispose();
        cache.SetValue(null, previous);
        try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch (IOException) { }
    }

    private sealed class Manager : HttpMessageHandler
    {
        public int Calls;
        public bool Outage;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            if (Outage && request.Method != HttpMethod.Delete) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            if (request.Method == HttpMethod.Delete) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            var path = request.RequestUri!.AbsolutePath.TrimEnd('/');
            var id = path.EndsWith("/activate", StringComparison.Ordinal) || path.EndsWith("/renew", StringComparison.Ordinal)
                ? path.Split('/')[^2] : Guid.NewGuid().ToString("N");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = JsonContent.Create(new { id, version = 1, grant = path.EndsWith("/leases", StringComparison.Ordinal) ? new string('A', 64) : null,
                validForSeconds = path.EndsWith("/leases", StringComparison.Ordinal) ? 30 : 120 }) });
        }
    }
}

public sealed class RtcPeerFactAttribute : FactAttribute
{
    public RtcPeerFactAttribute()
    {
        if (!File.Exists(Environment.GetEnvironmentVariable("QUASAR_RTC_TEST_PEER")) || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PLAYWRIGHT_MODULE")))
            Skip = "Build Quasar.Peer and set QUASAR_RTC_TEST_PEER and PLAYWRIGHT_MODULE for the isolated real browser proof.";
    }
}
