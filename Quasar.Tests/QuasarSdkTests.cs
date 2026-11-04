using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using Bunit;
using Magnetar.Protocol.Runtime;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MudBlazor.Services;
using Quasar.Components.Shared;
using Quasar.Plugin.Abstractions.Operations;
using Quasar.Services.Auth;
using Quasar.Services.Plugins;
using Xunit;

namespace Quasar.Tests;

[Collection("Exact server creation")]
public sealed partial class QuasarSdkTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "quasar-sdk-" + Guid.NewGuid().ToString("N"));
    private readonly FieldInfo _cache = typeof(MagnetarPaths).GetField("_cachedQuasarDirectory", BindingFlags.NonPublic | BindingFlags.Static)!;
    private readonly object? _previous;
    private readonly RbacConfigCatalog _rbac;
    private const string Prefix = "/my-server-panel";
    private const string Sdk = Prefix + QuasarSdkEndpoints.Prefix;

    public QuasarSdkTests()
    {
        _previous = _cache.GetValue(null);
        _cache.SetValue(null, _root);
        _rbac = new(NullLogger<RbacConfigCatalog>.Instance);
    }

    private async Task<WebApplication> StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(_rbac);
        builder.Services.AddSingleton(new QuasarAuthOptions());
        builder.Services.AddSingleton<QuasarRoleMapper>();
        builder.Services.AddAuthorization(Program.ConfigureAuthorizationPolicies);
        builder.Services.AddAuthorization(options => options.AddPolicy("Guarded", policy =>
            policy.RequireRole(QuasarRoles.Admin).RequireClaim("resource-access", "yes")));
        builder.Services.AddSingleton<IAuthorizationPolicyProvider, QuasarAuthorizationPolicyProvider>();
        builder.Services.AddScoped<QuasarOperationService>();
        builder.Services.AddSingleton<QuasarTransferRegistry>();
        builder.Services.AddSingleton<QuasarBrowserSessions>();
        builder.Services.AddScoped<QuasarHttpOperations>();
        builder.Services.AddScoped<IQuasarHttpOperations>(services => services.GetRequiredService<QuasarHttpOperations>());
        builder.Services.AddScoped<IQuasarOperationDispatcher>(services => services.GetRequiredService<QuasarOperationService>());
        builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
        builder.Services.AddAntiforgery();
        builder.Services.AddAuthentication("test").AddCookie("test", options =>
        {
            options.Events.OnRedirectToLogin = context => QuasarSdkEndpoints.WriteAuthorizationErrorAsync(context.HttpContext, 401);
            options.Events.OnRedirectToAccessDenied = context => QuasarSdkEndpoints.WriteAuthorizationErrorAsync(context.HttpContext, 403);
        });
        builder.Services.AddQuasarOperation<JsonElement, object>("example.plugin", "read", [QuasarPolicyNames.CanView],
            (context, input, token) => Task.FromResult<object>(new { answer = 42 }));
        builder.Services.AddQuasarOperation<JsonElement, object>("example.plugin", "restricted", [QuasarPolicyNames.CanManageSecurity],
            (context, input, token) => Task.FromResult<object>(new { secret = "management detail" }));
        builder.Services.AddQuasarOperation<JsonElement, object>("example.plugin", "unconfigured", [],
            (context, input, token) => throw new Exception("An unconfigured handler must not execute."));
        RegisterTransfers(builder.Services);
        var app = builder.Build();
        app.UsePathBase(Prefix);
        app.UseRouting();
        app.UseAuthentication();
        app.Use((context, next) =>
        {
            if (context.Request.Headers.TryGetValue("X-Test-Role", out var role))
                context.User = new ClaimsPrincipal(new ClaimsIdentity([
                    new Claim(ClaimTypes.NameIdentifier, "test-user"), new Claim(ClaimTypes.Role, role.ToString()),
                ], "test"));
            return next(context);
        });
        app.UseAuthorization();
        app.MapQuasarSdk();
        foreach (var prefix in new[] { "/_quasar/plugins/example.plugin", "/_content/Example" })
        {
            app.MapQuasarOperationRead(prefix + "/api/read", "example.plugin", "read");
            app.MapQuasarOperationRead(prefix + "/api/restricted", "example.plugin", "restricted");
        }
        await app.StartAsync();
        return app;
    }

    [Fact]
    public async Task ProtectedDefaultsPrefixAndLegacyAliasesShareAuthorization()
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();
        using (var response = await client.GetAsync(Sdk + "/session"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Null(response.Headers.Location);
            Assert.True(response.Headers.CacheControl?.NoStore);
        }
        client.DefaultRequestHeaders.Add("X-Test-Role", QuasarRoles.Viewer);
        using (var result = await InvokeAsync(client, "read"))
        {
            Assert.Equal(HttpStatusCode.OK, result.StatusCode);
            Assert.Equal(42, (await result.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("answer").GetInt32());
            Assert.True(result.Headers.CacheControl?.NoStore);
        }
        using (var denied = await InvokeAsync(client, "restricted")) Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using (var denied = await InvokeAsync(client, "unconfigured")) Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using (var missing = await InvokeAsync(client, "missing")) Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        foreach (var alias in new[] { "/_quasar/plugins/example.plugin", "/_content/Example" })
        {
            using var read = await client.GetAsync(Prefix + alias + "/api/read");
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);
            using var denied = await client.GetAsync(Prefix + alias + "/api/restricted");
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        }
    }

    [Fact]
    public async Task CsrfJsonAndSizeLimitsApplyBeforeInvocation()
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Test-Role", QuasarRoles.Admin);
        using (var noCsrf = await client.PostAsJsonAsync(Sdk + "/operations/example.plugin/read", new { }))
            Assert.Equal(HttpStatusCode.BadRequest, noCsrf.StatusCode);
        using (var malformed = await InvokeAsync(client, "read", "{")) Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        using (var oversized = await InvokeAsync(client, "read", "{\"x\":\"" + new string('x', 65536) + "\"}"))
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, oversized.StatusCode);
        using (var tooDeep = await InvokeAsync(client, "read", new string('[', 34) + "0" + new string(']', 34)))
            Assert.Equal(HttpStatusCode.BadRequest, tooDeep.StatusCode);
    }

    [Fact]
    public async Task RuleChangesAffectSdkAndEveryAliasAndCannotBeSelfGranted()
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Test-Role", QuasarRoles.Viewer);
        using (var denied = await client.GetAsync(Sdk + "/settings")) Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using (var denied = await client.PostAsJsonAsync(Sdk + "/settings/example.plugin/restricted", new { requiredPolicies = new[] { QuasarPolicyNames.CanView } }))
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        await using var scope = app.Services.CreateAsyncScope();
        var operations = scope.ServiceProvider.GetRequiredService<QuasarOperationService>();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => operations.SaveRuleAsync(User(QuasarRoles.Viewer),
            "example.plugin", "restricted", new() { RequiredPolicies = [QuasarPolicyNames.CanView] }));
        await operations.SaveRuleAsync(User(QuasarRoles.Admin), "example.plugin", "restricted",
            new() { RequiredPolicies = [QuasarPolicyNames.CanView] });
        using (var allowed = await InvokeAsync(client, "restricted")) Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        foreach (var alias in new[] { "/_quasar/plugins/example.plugin", "/_content/Example" })
        {
            using var allowed = await client.GetAsync(Prefix + alias + "/api/restricted");
            Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        }
        await operations.SaveRuleAsync(User(QuasarRoles.Admin), "example.plugin", "restricted",
            new() { Enabled = false, RequiredPolicies = [QuasarPolicyNames.CanView] });
        using (var denied = await InvokeAsync(client, "restricted")) Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using (var denied = await client.GetAsync(Prefix + "/_content/Example/api/restricted")) Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        await operations.SaveRuleAsync(User(QuasarRoles.Admin), "example.plugin", "restricted", null);
        using (var denied = await InvokeAsync(client, "restricted")) Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        await Assert.ThrowsAsync<ArgumentException>(() => operations.SaveRuleAsync(User(QuasarRoles.Admin), "example.plugin", "read", new()));
        await Assert.ThrowsAsync<ArgumentException>(() => operations.SaveRuleAsync(User(QuasarRoles.Admin), "example.plugin", "read",
            new() { RequiredPolicies = ["not-a-policy"] }));
        Assert.Empty(_rbac.GetConfig().OperationOverrides);
        client.DefaultRequestHeaders.Remove("X-Test-Role");
        client.DefaultRequestHeaders.Add("X-Test-Role", QuasarRoles.Admin);
        using (var saved = await SendSdkAsync(client, "/settings/example.plugin/read", "{\"enabled\":false,\"requiredPolicies\":[\"CanView\"]}"))
            Assert.Equal(HttpStatusCode.NoContent, saved.StatusCode);
        using (var denied = await InvokeAsync(client, "read")) Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using (var invalid = await SendSdkAsync(client, "/settings/example.plugin/read", "{\"requiredPolicies\":[]}"))
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        using (var reset = await SendSdkAsync(client, "/settings/example.plugin/read", "null")) Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);
        Assert.Empty(_rbac.GetConfig().OperationOverrides);
    }

    [Fact]
    public async Task RuntimeRoleOverridesAreNotCachedAndPreserveOtherRequirements()
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Test-Role", QuasarRoles.Viewer);
        await using var scope = app.Services.CreateAsyncScope();
        var operations = scope.ServiceProvider.GetRequiredService<QuasarOperationService>();
        await operations.SavePolicyRolesAsync(User(QuasarRoles.Admin), QuasarPolicyNames.CanView, [QuasarRoles.Admin]);
        using (var denied = await InvokeAsync(client, "read")) Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        await operations.SavePolicyRolesAsync(User(QuasarRoles.Admin), QuasarPolicyNames.CanView, [QuasarRoles.Viewer]);
        using (var allowed = await InvokeAsync(client, "read")) Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        await operations.SavePolicyRolesAsync(User(QuasarRoles.Admin), QuasarPolicyNames.CanView, []);
        using (var denied = await InvokeAsync(client, "read")) Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        await Assert.ThrowsAsync<ArgumentException>(() => operations.SavePolicyRolesAsync(User(QuasarRoles.Admin), QuasarPolicyNames.CanManageSecurity, []));
        await Assert.ThrowsAsync<ArgumentException>(() => operations.SavePolicyRolesAsync(User(QuasarRoles.Admin), QuasarPolicyNames.ClusterQuery, [QuasarRoles.Viewer]));
        await operations.SavePolicyRolesAsync(User(QuasarRoles.Admin), "Guarded", [QuasarRoles.Viewer]);
        var authorization = scope.ServiceProvider.GetRequiredService<IAuthorizationService>();
        Assert.False((await authorization.AuthorizeAsync(User(QuasarRoles.Viewer), null, "Guarded")).Succeeded);
        var permitted = User(QuasarRoles.Viewer);
        ((ClaimsIdentity)permitted.Identity!).AddClaim(new Claim("resource-access", "yes"));
        Assert.True((await authorization.AuthorizeAsync(permitted, null, "Guarded")).Succeeded);
    }

    [Fact]
    public async Task InvalidStoredRulesFailClosedAndConcurrentEditsArePreserved()
    {
        await using var app = await StartAsync();
        await using var scope = app.Services.CreateAsyncScope();
        var operations = scope.ServiceProvider.GetRequiredService<QuasarOperationService>();
        await Task.WhenAll(new[] { "read", "restricted" }.Select(id => operations.SaveRuleAsync(User(QuasarRoles.Admin), "example.plugin", id,
            new() { RequiredPolicies = [QuasarPolicyNames.CanView] })).Append(_rbac.UpdateAsync(config =>
            {
                config.SubjectRoleMappings.Add(new() { Subject = "76561198000000001", Roles = [QuasarRoles.Viewer] });
                return Task.CompletedTask;
            })));
        Assert.Equal(2, _rbac.GetConfig().OperationOverrides.Count);
        Assert.Contains(_rbac.GetConfig().SubjectRoleMappings, mapping => mapping.Subject == "76561198000000001");
        await _rbac.UpdateAsync(config =>
        {
            config.OperationOverrides["example.plugin/read"] = null;
            config.OperationOverrides["example.plugin/restricted"] = new() { RequiredPolicies = ["unknown"] };
            return Task.CompletedTask;
        });
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Test-Role", QuasarRoles.Admin);
        foreach (var id in new[] { "read", "restricted" })
        {
            using var denied = await InvokeAsync(client, id);
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        }
        var settings = await operations.GetSettingsAsync(User(QuasarRoles.Admin));
        Assert.All(settings.Where(setting => setting.Overridden), setting => Assert.NotNull(setting.Error));
        await File.WriteAllTextAsync(Path.Combine(_root, "rbac.json"), "{");
        using var corrupt = new RbacConfigCatalog(NullLogger<RbacConfigCatalog>.Instance);
        var closed = ActivatorUtilities.CreateInstance<QuasarOperationService>(scope.ServiceProvider, corrupt);
        var result = await closed.DispatchAsync(new(User(QuasarRoles.Admin), scope.ServiceProvider), "example.plugin", "read",
            JsonSerializer.SerializeToElement(new { }), default);
        Assert.Equal(403, result.StatusCode);
        await Assert.ThrowsAsync<InvalidOperationException>(() => corrupt.UpdateAsync(_ => Task.CompletedTask));
    }

    [Fact]
    public async Task DispatchRefreshesStaleSteamRolesAndPropagatesCancellation()
    {
        await using var app = await StartAsync();
        await using var scope = app.Services.CreateAsyncScope();
        var operations = scope.ServiceProvider.GetRequiredService<QuasarOperationService>();
        var user = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, "76561198000000000"),
            new Claim(QuasarClaimTypes.Provider, QuasarAuthSchemes.Steam),
            new Claim(ClaimTypes.Role, QuasarRoles.Admin),
        ], "test"));
        var result = await operations.DispatchAsync(new(user, scope.ServiceProvider), "example.plugin", "restricted", JsonSerializer.SerializeToElement(new { }), default);
        Assert.Equal(403, result.StatusCode);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operations.DispatchAsync(new(User(QuasarRoles.Viewer), scope.ServiceProvider),
            "example.plugin", "read", JsonSerializer.SerializeToElement(new { }), cancelled.Token));
    }

    [Fact]
    public async Task SecurityControlsSaveRulesAndRejectPrivilegesLostAfterRendering()
    {
        await using var app = await StartAsync();
        await using var scope = app.Services.CreateAsyncScope();
        await using var view = new BunitContext();
        view.Services.AddMudServices();
        view.JSInterop.Mode = JSRuntimeMode.Loose;
        var authentication = new CurrentAuthentication { User = User(QuasarRoles.Admin) };
        view.Services.AddSingleton<AuthenticationStateProvider>(authentication);
        view.Services.AddSingleton(scope.ServiceProvider.GetRequiredService<QuasarOperationService>());
        view.Services.AddSingleton(_rbac);
        var page = view.Render<QuasarSdkAuthorization>();
        page.WaitForAssertion(() => Assert.True(page.FindAll("fieldset").Count >= 3));
        await page.InvokeAsync(() => page.FindAll("fieldset").Single(field => field.TextContent.Contains("restricted"))
            .QuerySelector("input[type=text]")!.Change(QuasarPolicyNames.CanView));
        await page.InvokeAsync(() => page.FindAll("fieldset").Single(field => field.TextContent.Contains("restricted"))
            .QuerySelectorAll("button").Single(button => button.TextContent.Contains("Save endpoint rule")).ClickAsync());
        Assert.Equal([QuasarPolicyNames.CanView], _rbac.GetConfig().OperationOverrides["example.plugin/restricted"]!.RequiredPolicies);

        authentication.User = User(QuasarRoles.Viewer);
        await page.InvokeAsync(() => page.FindAll("fieldset").Single(field => field.TextContent.Contains("restricted"))
            .QuerySelector("input[type=checkbox]")!.Change(false));
        await page.InvokeAsync(() => page.FindAll("fieldset").Single(field => field.TextContent.Contains("restricted"))
            .QuerySelectorAll("button").Single(button => button.TextContent.Contains("Save endpoint rule")).ClickAsync());
        Assert.True(_rbac.GetConfig().OperationOverrides["example.plugin/restricted"]!.Enabled);
        Assert.Contains("Security administration permission is required", page.Markup);
    }

    private sealed class CurrentAuthentication : AuthenticationStateProvider
    {
        public required ClaimsPrincipal User { get; set; }
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(User));
    }

    private static ClaimsPrincipal User(string role) => new(new ClaimsIdentity([
        new Claim(ClaimTypes.NameIdentifier, "test-admin"), new Claim(ClaimTypes.Role, role),
    ], "test"));

    private static Task<HttpResponseMessage> InvokeAsync(HttpClient client, string operation, string payload = "{}") =>
        SendSdkAsync(client, "/operations/example.plugin/" + operation, payload);

    private static async Task<HttpResponseMessage> SendSdkAsync(HttpClient client, string path, string payload)
    {
        var tokens = await SessionTokensAsync(client);
        using var request = new HttpRequestMessage(HttpMethod.Post, Sdk + path);
        request.Headers.Add(tokens.GetProperty("headerName").GetString()!, tokens.GetProperty("requestToken").GetString());
        request.Content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
        return await client.SendAsync(request);
    }

    private static async Task<JsonElement> SessionTokensAsync(HttpClient client)
    {
        using var session = await client.GetAsync(Sdk + "/session");
        var cookies = new Dictionary<string, string>(StringComparer.Ordinal);
        if (client.DefaultRequestHeaders.TryGetValues("Cookie", out var existing))
            foreach (var cookie in string.Join(";", existing).Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            { var parts = cookie.Split('=', 2); cookies[parts[0]] = parts[1]; }
        if (session.Headers.TryGetValues("Set-Cookie", out var added))
            foreach (var cookie in added) { var parts = cookie.Split(';')[0].Split('=', 2); cookies[parts[0]] = parts[1]; }
        client.DefaultRequestHeaders.Remove("Cookie");
        client.DefaultRequestHeaders.Add("Cookie", string.Join("; ", cookies.Select(cookie => cookie.Key + "=" + cookie.Value)));
        return await session.Content.ReadFromJsonAsync<JsonElement>();
    }

    public void Dispose()
    {
        _rbac.Dispose();
        _cache.SetValue(null, _previous);
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
