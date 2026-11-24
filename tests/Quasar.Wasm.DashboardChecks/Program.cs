using System.Diagnostics;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Quasar.Components;
using Quasar.Hosting;
using Quasar.Plugin.Abstractions.Operations;
using IQuasarAgentPrincipals = Quasar.Plugin.Abstractions.Security.IQuasarAgentPrincipals;
using Quasar.Plugin.Abstractions.Discord;
using Quasar.Services.Audit;
using Quasar.Services;
using Quasar.Services.Auth;
using Quasar.Services.Plugins;

// The fixture loads plugin DLLs into the default assembly context. On Windows
// their files stay locked until the host exits, so its parent owns cleanup.
if (args is ["--fixture-host"])
    return await RunFixtureAsync();

var root = Directory.CreateTempSubdirectory("quasar-wasm-dashboard-");
try
{
    var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet") { UseShellExecute = false };
    start.ArgumentList.Add(typeof(Program).Assembly.Location);
    start.ArgumentList.Add("--fixture-host");
    start.Environment["QUASAR_INSTALL_DIR"] = root.FullName;
    using var host = Process.Start(start)!;
    await host.WaitForExitAsync();
    return host.ExitCode;
}
finally
{
    root.Delete(recursive: true);
}

static async Task<int> RunFixtureAsync()
{
    var published = Environment.GetEnvironmentVariable("QUASAR_DASHBOARD_PUBLISH");
    var manifest = published is null ? null : Path.GetFullPath(Path.Combine(published, "Quasar.staticwebassets.endpoints.json"));
    var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ApplicationName = typeof(App).Assembly.GetName().Name,
        ContentRootPath = published, WebRootPath = published is null ? null : Path.GetFullPath(Path.Combine(published, "wwwroot")) });
    if (manifest is not null && !File.Exists(manifest)) throw new FileNotFoundException("Published worker manifest missing.", manifest);
    var panel = new HostedPanelState { Choice = "hosted", SetupCompleted = true, PanelId = "fixture-panel", Credential = "fixture-panel." + new string('a', 64) };
    await HostedPanelState.SaveAsync(panel);
    var release = new string('a', 64);
    builder.Configuration["QUASAR_NEBULA_RELEASE_SHA256"] = release;
    builder.Configuration["EnableStaticAssetsDevelopmentIntegrity"] = "true";
    using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    var certificateRequest = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256);
    var names = new SubjectAlternativeNameBuilder();
    names.AddIpAddress(IPAddress.Loopback);
    certificateRequest.CertificateExtensions.Add(names.Build());
    using var generated = certificateRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
    // Schannel needs a named private key to serve TLS. Importing into the
    // current user's key set gives the fixture one for the certificate lifetime.
    using var windowsCertificate = OperatingSystem.IsWindows()
        ? X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pkcs12), null, X509KeyStorageFlags.UserKeySet)
        : null;
    builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0, endpoint => endpoint.UseHttps(windowsCertificate ?? generated)));
    if (published is null) builder.WebHost.UseStaticWebAssets();
    builder.Services.AddHttpClient();
    builder.Services.AddSingleton<HostedPanelService>();
    builder.Services.AddSingleton<DashboardReleaseManifest>();
    builder.Services.AddRazorComponents().AddInteractiveWebAssemblyComponents();
    builder.Services.AddCascadingAuthenticationState();
    builder.Services.AddAuthentication("fixture").AddCookie("fixture", options =>
    {
        options.Cookie.Name = QuasarAuthSchemes.CookieName;
        options.CookieManager = new HostedPanelCookieManager();
        Quasar.Program.ConfigureDashboardLogin(options);
        options.Events.OnRedirectToLogin = c => { c.Response.StatusCode = 401; return Task.CompletedTask; };
    });
    builder.Services.AddAuthorization(options =>
    {
        foreach (var field in typeof(QuasarPolicyNames).GetFields())
            options.AddPolicy((string)field.GetRawConstantValue()!, policy => { policy.RequireAuthenticatedUser(); if (field.Name != nameof(QuasarPolicyNames.CanView)) policy.RequireRole("admin"); });
    });
    builder.Services.AddSingleton<QuasarBrowserSessions>();
    builder.Services.AddSingleton<QuasarTransferRegistry>();
    builder.Services.AddScoped<QuasarOperationService>();
    builder.Services.AddScoped<IQuasarOperationDispatcher>(sp => sp.GetRequiredService<QuasarOperationService>());
    builder.Services.AddScoped<IQuasarAgentOperations>(sp => sp.GetRequiredService<QuasarOperationService>());
    builder.Services.AddSingleton<QuasarAgentPrincipals>();
    builder.Services.AddSingleton<IQuasarAgentPrincipals>(sp => sp.GetRequiredService<QuasarAgentPrincipals>());
    builder.Services.AddSingleton<IQuasarDiscordChannels, OfflineDiscordChannels>();
    builder.Services.AddSingleton(new QuasarAuditOptions());
    builder.Services.AddSingleton<QuasarAuditLog>();
    builder.Services.AddSingleton<IQuasarAuditLog>(sp => sp.GetRequiredService<QuasarAuditLog>());
    builder.Services.AddScoped<QuasarHttpOperations>();
    builder.Services.AddScoped<IQuasarHttpOperations>(sp => sp.GetRequiredService<QuasarHttpOperations>());
    var peer = Environment.GetEnvironmentVariable("QUASAR_RTC_TEST_PEER");
    if (!string.IsNullOrEmpty(peer))
    {
        builder.Services.AddHttpClient("Nebula").ConfigurePrimaryHttpMessageHandler(() => new FixtureLeases());
        builder.Services.AddQuasarRtc(builder.Configuration);
        builder.Services.Configure<QuasarRtcOptions>(o => o.PeerExecutable = peer);
    }
    builder.Services.Configure<QuasarRtcOptions>(o => o.StunUrls = []);
    builder.Services.AddQuasarCoreOperations();
    builder.Services.AddSingleton<RbacConfigCatalog>();
    builder.Services.AddSingleton<QuasarRoleMapper>();
    builder.Services.AddSingleton(new QuasarAuthOptions());
    builder.Services.AddSingleton<QuasarAuthSettingsService>();
    builder.Services.AddSingleton<DashboardAccessChanges>();
    builder.Services.AddSingleton(new WebServiceOptions());
    builder.Services.AddSingleton<BrandingService>();
    builder.Services.AddSingleton<QuasarConfigProfileCatalog>();
    var catalog = ViewerFixture.CreateCatalog(builder);
    builder.Services.AddSingleton(catalog);
    bool checkFleet = Environment.GetEnvironmentVariable("QUASAR_FLEET_CHECKS") == "1";
    if (checkFleet) FleetFixture.Register(builder.Services);

    await using var app = builder.Build();
    if (checkFleet) await FleetFixture.SeedAsync(app);
    var profiles = app.Services.GetRequiredService<QuasarConfigProfileCatalog>();
    await profiles.UpsertAsync(new() { Name = "Alpha default", ConfigProfileId = "alpha" });
    await profiles.UpsertAsync(new() { Name = "Survival test", ConfigProfileId = "survival", RootSettings = new()
    {
        ServerPassword = "fixture-hidden-password", Reserved = [76561198000000003]
    } });
    // This fixture has no push delivery worker. Model a panel with these optional operations disabled.
    await app.Services.GetRequiredService<RbacConfigCatalog>().UpdateAsync(config =>
    {
        foreach (var operation in new[] { "notifications.current", "notifications.push.key" })
            config.OperationOverrides["quasar.core/" + operation] = new QuasarOperationRule { Enabled = false, RequiredPolicies = [QuasarPolicyNames.CanView] };
        return Task.CompletedTask;
    });
    app.Use(async (context, next) =>
    {
        if (context.Request.Path.StartsWithSegments("/_nebula/releases/" + release + "/_framework", out var file))
        {
            // Exercise both a credentialless cache hit and a retired entry in a single real dashboard boot.
            if (context.Request.Headers.ContainsKey("Cookie")) { context.Response.StatusCode = 403; return; }
            if (file.Value!.StartsWith("/Quasar.Client.", StringComparison.Ordinal)) { context.Response.StatusCode = 404; return; }
            // The gateway fixture serves only assets listed in this build.
            if (!app.Services.GetRequiredService<DashboardReleaseManifest>().Hashes.ContainsKey(file.Value.TrimStart('/')))
            { context.Response.StatusCode = 404; return; }
            context.Request.Path = "/_framework" + file;
            context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "public-release-fixture")], "fixture"));

        }
        if (context.Request.Path.StartsWithSegments("/fixture-panel"))
            context.Request.Headers[HostedPanelRequest.Header] = panel.LocalProxySecret;
        await HostedPanelRequest.InvokeAsync(context, next, new());
    });
    app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
    app.UseRouting();
    app.UseAuthentication();
    app.UseAuthorization();
    app.UseAntiforgery();
    catalog.UsePluginStaticAssets(app);
    catalog.ConfigurePluginEndpoints(app);
    app.MapQuasarSdk().MapDashboardAccess();
    if (!string.IsNullOrEmpty(peer)) app.MapQuasarRtc();
    else app.MapPost("/_quasar/sdk/v1/rtc/attempts", () => Results.Problem(statusCode: 503, detail: "The fixture has no direct peer.")).RequireAuthorization();
    app.MapGet("/fixture/login", async (HttpContext c) =>
        await c.SignInAsync("fixture", new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "Fixture admin"), new Claim(ClaimTypes.NameIdentifier, "76561198000000001"), new Claim(QuasarClaimTypes.Provider, QuasarAuthSchemes.Steam)], "fixture"))));
    app.MapPost("/fixture/page-permissions/{enabled:bool}", async (bool enabled, RbacConfigCatalog rbac) =>
        await rbac.UpdateAsync(config =>
        {
            const string key = "quasar.core/dashboard.permission";
            if (enabled) config.OperationOverrides.Remove(key);
            else config.OperationOverrides[key] = new() { Enabled = false, RequiredPolicies = [QuasarPolicyNames.CanView] };
            return Task.CompletedTask;
        })).RequireAuthorization();
    app.MapPost("/fixture/logout", (HttpContext http) => http.SignOutAsync("fixture")).RequireAuthorization();
    if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("QUASAR_TEST_PLUGIN_DRIVER")))
    {
        const string testAdmin = "76561198000000013";
        await app.Services.GetRequiredService<RbacConfigCatalog>().UpdateAsync(config =>
        {
            config.SubjectRoleMappings.Add(new() { Provider = QuasarAuthSchemes.Steam, Subject = testAdmin, Roles = [QuasarRoles.Admin] });
            return Task.CompletedTask;
        });
        app.MapGet("/fixture/plugin-login", (HttpContext c, QuasarRoleMapper roles) =>
            c.SignInAsync("fixture", roles.CreateSteamPrincipal(testAdmin)));
    }
    app.MapDashboardStaticAssets(manifest: manifest);
    app.MapRazorComponents<App>().AddAdditionalAssemblies(catalog.RoutableAssemblies.Append(typeof(Quasar.Client.Components.DashboardRouter).Assembly).ToArray()).AddInteractiveWebAssemblyRenderMode().WithStaticAssets(manifest).RequireAuthorization();
    await app.StartAsync();
    var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
    var runner = new ProcessStartInfo("node") { UseShellExecute = false };
    runner.ArgumentList.Add(Path.GetFullPath("../../../browser.test.mjs", AppContext.BaseDirectory));
    runner.ArgumentList.Add(address);
    using var process = Process.Start(runner)!;
    await process.WaitForExitAsync();
    await app.StopAsync();
    return process.ExitCode;
}

sealed class FixtureLeases : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        var path = request.RequestUri!.AbsolutePath.TrimEnd('/');
        bool create = path.EndsWith("/leases");
        var data = new { id = create ? Guid.NewGuid().ToString("N") : path.Split('/')[^2], version = 1,
            grant = create ? new string('A',64) : null, validForSeconds = create ? 30 : 120 };
        return Task.FromResult(new HttpResponseMessage(request.Method == HttpMethod.Delete ? System.Net.HttpStatusCode.NoContent : System.Net.HttpStatusCode.OK)
        {
            Content = JsonContent.Create(data)
        });
    }
}

sealed class OfflineDiscordChannels : IQuasarDiscordChannels
{
    public Task<bool> PostAsync(string channelId, string text, CancellationToken token) => Task.FromResult(false);
}
