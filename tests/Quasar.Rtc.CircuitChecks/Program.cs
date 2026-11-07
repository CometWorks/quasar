using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using Magnetar.Protocol.Runtime;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.FileProviders;
using Quasar.Hosting;
using Quasar.Rtc.CircuitChecks.Components;
using Quasar.Services.Auth;
using Quasar.Services.Plugins;

const string prefix = "/circuit-panel", user = "76561198000000001";
var project = Path.GetFullPath("../../../", AppContext.BaseDirectory);
var root = Path.Combine(Path.GetTempPath(), "quasar-circuit-" + Guid.NewGuid().ToString("N"));
var cache = typeof(MagnetarPaths).GetField("_cachedQuasarDirectory", BindingFlags.Static | BindingFlags.NonPublic)!;
var previous = cache.GetValue(null); cache.SetValue(null, root);
try
{
    var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.Logging.AddConsole().SetMinimumLevel(LogLevel.Warning);
    builder.WebHost.UseUrls("http://127.0.0.1:0").UseStaticWebAssets();
    builder.Services.AddRazorComponents().AddInteractiveServerComponents(options => options.DisconnectedCircuitRetentionPeriod = TimeSpan.FromSeconds(20));
    builder.Services.AddCascadingAuthenticationState();
    builder.Services.AddSingleton<RbacConfigCatalog>(); builder.Services.AddSingleton(new QuasarAuthOptions()); builder.Services.AddSingleton<QuasarRoleMapper>();
    builder.Services.AddAuthorization(options => typeof(Quasar.Program).GetMethod("ConfigureAuthorizationPolicies", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [options]));
    builder.Services.AddSingleton<IAuthorizationPolicyProvider, QuasarAuthorizationPolicyProvider>();
    builder.Services.AddAuthentication("test").AddCookie("test", options => {
        options.Events.OnRedirectToLogin = c => QuasarSdkEndpoints.WriteAuthorizationErrorAsync(c.HttpContext, 401);
        options.Events.OnRedirectToAccessDenied = c => QuasarSdkEndpoints.WriteAuthorizationErrorAsync(c.HttpContext, 403);
    });
    builder.Services.AddSingleton<QuasarBrowserSessions>(); builder.Services.AddSingleton<QuasarTransferRegistry>();
    builder.Services.AddScoped<QuasarOperationService>(); builder.Services.AddScoped<QuasarHttpOperations>();
    builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider(); builder.Services.AddAntiforgery();
    builder.Services.AddQuasarRtc(builder.Configuration);
    builder.Services.PostConfigure<QuasarRtcOptions>(options => {
        options.StunUrls = Environment.GetEnvironmentVariable("QUASAR_RTC_TEST_STUN") is { Length: > 0 } stun ? [stun] : [];
        options.PeerExecutable = Environment.GetEnvironmentVariable("QUASAR_RTC_TEST_PEER")!;
    });
    var manager = Environment.GetEnvironmentVariable("NEBULA_RTC_TEST_MANAGER");
    builder.Services.AddHttpClient("Nebula").ConfigurePrimaryHttpMessageHandler(() => manager is null ? new Leases() : new FixtureManager(new Uri(manager)));
    await HostedPanelState.SaveAsync(new HostedPanelState { Choice = "hosted", PanelId = prefix[1..], Credential = Environment.GetEnvironmentVariable("NEBULA_RTC_TEST_CREDENTIAL") ?? prefix[1..] + "." + new string('a',64) });
    await using var app = builder.Build();
    await app.Services.GetRequiredService<RbacConfigCatalog>().UpdateAsync(config => { config.SubjectRoleMappings = [new() { Subject = user, Roles = [QuasarRoles.Admin] }, new() { Subject = "76561198000000002", Roles = [QuasarRoles.Admin] }]; return Task.CompletedTask; });
    var hosted = typeof(Quasar.Program).Assembly.GetType("Quasar.Services.HostedPanelRequest")!.GetMethod("InvokeAsync")!.CreateDelegate<Func<HttpContext, RequestDelegate, QuasarAuthOptions, Task>>();
    app.Use((http, next) => { http.Request.Headers["X-Quasar-Hosted"] = HostedPanelState.Read()!.LocalProxySecret; return hosted(http, next, new QuasarAuthOptions()); });
    app.UsePathBase(prefix);
    app.UseStaticFiles(new StaticFileOptions { FileProvider = new PhysicalFileProvider(Path.GetFullPath("../../Quasar/wwwroot", project)) });
    var packages = Environment.GetEnvironmentVariable("NUGET_PACKAGES") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget/packages");
    var framework = Path.Combine(packages, "microsoft.aspnetcore.app.internal.assets", Environment.Version.ToString(3), "_framework");
    app.UseStaticFiles(new StaticFileOptions { RequestPath = "/_framework", FileProvider = new PhysicalFileProvider(framework) });
    app.Use(async (http, next) => {
        if (http.Request.Path == "/_quasar/sdk/v1/rtc/attempts" && http.Request.Method == "POST")
        {
            await Task.Delay(250, http.RequestAborted);
            if (manager is not null)
            {
                // Deterministic initial direct failure; the subsequent pair uses
                // only real TURN candidates and production manager grants.
                http.Request.EnableBuffering();
                using var offer = await System.Text.Json.JsonDocument.ParseAsync(http.Request.Body);
                http.Request.Body.Position = 0;
                if (!offer.RootElement.TryGetProperty("relayTicket", out _)) { http.Response.StatusCode = 503; return; }
            }
        }
        await next(http);
    });
    app.UseRouting(); app.UseAuthentication(); app.UseAuthorization(); app.UseAntiforgery();
    app.MapQuasarSdk(); app.MapQuasarRtc();
    app.MapGet("/Components/Layout/ReconnectModal.razor.js", () => Results.File(Path.GetFullPath("../../Quasar/Components/Layout/ReconnectModal.razor.js", project), "text/javascript"));
    app.MapGet("/fixture/reconnect.css", () => Results.File(Path.GetFullPath("../../Quasar/Components/Layout/ReconnectModal.razor.css", project), "text/css"));
    app.MapRazorComponents<App>().AddInteractiveServerRenderMode().RequireAuthorization();
    app.MapGet("/fixture/login", async (HttpContext http, QuasarRoleMapper roles, RbacConfigCatalog rbac) => { if (!roles.GetSteamRoles(user).Contains(QuasarRoles.Admin)) await rbac.UpdateAsync(config => { config.SubjectRoleMappings = [new() { Subject = user, Roles = [QuasarRoles.Admin] }, new() { Subject = "76561198000000002", Roles = [QuasarRoles.Admin] }]; return Task.CompletedTask; }); await http.SignInAsync("test", roles.CreateSteamPrincipal(user), new AuthenticationProperties { ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(5) }); return Results.Redirect(prefix + "/"); });
    app.MapPost("/fixture/viewer", async (RbacConfigCatalog rbac) => {
        await rbac.UpdateAsync(config => { config.SubjectRoleMappings = [new() { Subject = user, Roles = [QuasarRoles.Viewer] }, new() { Subject = "76561198000000002", Roles = [QuasarRoles.Admin] }]; return Task.CompletedTask; }); return Results.NoContent();
    }).RequireAuthorization(QuasarPolicyNames.CanManageSecurity);
    await app.StartAsync();
    var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
    var info = new ProcessStartInfo("node") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
    info.ArgumentList.Add("--test"); info.ArgumentList.Add(Path.Combine(project, "browser.test.mjs"));
    info.Environment["QUASAR_CIRCUIT_URL"] = address + prefix + "/";
    using var node = Process.Start(info)!; var stdout = node.StandardOutput.BaseStream.CopyToAsync(Console.OpenStandardOutput()); var stderr = node.StandardError.BaseStream.CopyToAsync(Console.OpenStandardError());
    await node.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(3)); await Task.WhenAll(stdout, stderr);
    await app.StopAsync();
    if (node.ExitCode != 0) throw new Exception("Real Blazor circuit checks failed.");
}
finally { cache.SetValue(null, previous); try { Directory.Delete(root, true); } catch (IOException) { } }
sealed class Leases : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        var path = request.RequestUri!.AbsolutePath.TrimEnd('/');
        return Task.FromResult(new HttpResponseMessage(request.Method == HttpMethod.Delete ? HttpStatusCode.NoContent : HttpStatusCode.OK) { Content = JsonContent.Create(new {
            id = path.EndsWith("/leases") ? Guid.NewGuid().ToString("N") : path.Split('/')[^2], version = 1, grant = path.EndsWith("/leases") ? new string('A',64) : null, validForSeconds = path.EndsWith("/leases") ? 30 : 120 }) });
    }
}

sealed class FixtureManager(Uri manager) : DelegatingHandler(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false })
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        request.RequestUri = new Uri(manager, request.RequestUri!.PathAndQuery);
        return base.SendAsync(request, token);
    }
}
