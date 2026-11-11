using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.Extensions.Options;
using Quasar.Services.Auth;

namespace Quasar.Services.Players;

public static class PlayerAuthEndpoints
{
    internal const string DiscordScheme = "PlayerDiscord";
    internal const string ExternalCookie = "PlayerExternal";
    internal const string DiscordCallback = "/player-auth/signin-discord";
    private static readonly HtmlEncoder Html = HtmlEncoder.Default;

    public static IServiceCollection AddPlayerAuthentication(this IServiceCollection services)
    {
        services.AddSingleton<PlayerAccessCatalog>();
        services.AddSingleton<PlayerAuthBrowser>();
        services.AddSingleton<DiscordPlayerRoles>();
        services.AddSingleton<PlayerAccessService>();
        services.AddSingleton<PlayerAccessWorker>();
        services.AddHostedService(p => p.GetRequiredService<PlayerAccessWorker>());
        services.AddHttpClient("PlayerDomainVerification", client =>
        { client.Timeout = TimeSpan.FromSeconds(15); client.MaxResponseContentBufferSize = 4096; })
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.AddAuthentication().AddCookie(ExternalCookie, cookie =>
        {
            cookie.Cookie.Name = ".Quasar.PlayerExternal";
            cookie.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            cookie.Cookie.HttpOnly = true;
            cookie.CookieManager = new HostedPanelCookieManager();
            cookie.ExpireTimeSpan = TimeSpan.FromMinutes(5);
        }).AddOAuth(DiscordScheme, options =>
        {
            options.SignInScheme = ExternalCookie;
            options.CallbackPath = DiscordCallback;
            options.AuthorizationEndpoint = "https://discord.com/oauth2/authorize";
            options.TokenEndpoint = "https://discord.com/api/oauth2/token";
            options.UserInformationEndpoint = "https://discord.com/api/users/@me";
            options.Scope.Add("identify");
            options.UsePkce = true;
            options.SaveTokens = false;
            options.CorrelationCookie.SecurePolicy = CookieSecurePolicy.Always;
            options.Events.OnCreatingTicket = async context =>
            {
                var browser = context.HttpContext.RequestServices.GetRequiredService<PlayerAuthBrowser>();
                if (!browser.Ready || !browser.IsCanonical(context.HttpContext)) throw new InvalidOperationException("Player authentication address changed.");
                var steam = await SteamIdentityAsync(context.HttpContext);
                if (steam is null || !context.Properties.Items.TryGetValue("steam", out var expected) || steam != expected)
                    throw new InvalidOperationException("Steam identity changed during account linking.");
                using var request = new HttpRequestMessage(HttpMethod.Get, context.Options.UserInformationEndpoint);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", context.AccessToken);
                using var response = await context.Backchannel.SendAsync(request, context.HttpContext.RequestAborted);
                response.EnsureSuccessStatusCode();
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(context.HttpContext.RequestAborted));
                var discordId = json.RootElement.GetProperty("id").GetString()!;
                await context.HttpContext.RequestServices.GetRequiredService<QuasarPlayerLinks>()
                    .AuthenticateAsync(steam, discordId, context.HttpContext.RequestAborted);
                context.Identity!.AddClaim(new(ClaimTypes.NameIdentifier, discordId));
            };
            options.Events.OnTicketReceived = context =>
            {
                // The Discord identity is proof for linking only. It never becomes a panel principal.
                context.HandleResponse();
                context.Response.Redirect(context.Properties!.RedirectUri!);
                return Task.CompletedTask;
            };
            options.Events.OnRemoteFailure = context =>
            {
                context.HandleResponse();
                context.Response.Redirect(context.Request.PathBase + PlayerAuthBrowser.Path + "?error=authentication");
                return Task.CompletedTask;
            };
        });
        services.AddOptions<OAuthOptions>(DiscordScheme).Configure<PlayerAccessCatalog>((options, catalog) =>
        {
            var settings = catalog.GetSettings();
            // Handlers validate these lazily. An unconfigured installation must still serve its panel.
            options.ClientId = settings.DiscordClientId.Length == 0 ? "unconfigured" : settings.DiscordClientId;
            options.ClientSecret = settings.DiscordClientSecret.Length == 0 ? "unconfigured" : settings.DiscordClientSecret;
        });
        return services;
    }

    public static void MapPlayerAuthentication(this WebApplication app)
    {
        app.MapGet("/.well-known/quasar-player-auth", (HttpContext context, PlayerAuthBrowser browser) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return browser.ChallengeProof(context.Request.Query["challenge"]) is { } proof
                ? Results.Text(proof) : Results.NotFound();
        }).AllowAnonymous();

        app.MapGet(PlayerAuthBrowser.Path, ShowAsync).AllowAnonymous().RequireRateLimiting("player-auth");
        app.MapPost(PlayerAuthBrowser.Path + "/{action}", ActAsync).AllowAnonymous().RequireRateLimiting("player-auth");
    }

    internal static async Task<string?> SteamIdentityAsync(HttpContext context)
    {
        var authenticated = await context.AuthenticateAsync(QuasarAuthSchemes.Cookie);
        var principal = authenticated.Principal;
        var steam = principal?.FindFirstValue(QuasarClaimTypes.SteamId);
        return authenticated.Succeeded && principal?.FindFirstValue(QuasarClaimTypes.Provider) == QuasarAuthSchemes.Steam
            && RbacConfigCatalog.IsSteamId64(steam ?? "") ? steam : null;
    }

    private static async Task<IResult> ShowAsync(HttpContext context, PlayerAuthBrowser browser,
        PlayerAccessCatalog catalog, PlayerAccessService access, QuasarPlayerLinks links, IAntiforgery antiforgery)
    {
        var steam = await SteamIdentityAsync(context);
        bool hasLink = steam is not null && links.GetLinks().Any(link => link.SteamId == steam);
        if (!browser.Ready)
            return Page("Player authentication unavailable", "<p>This website needs verified public access and Discord OAuth configuration.</p>"
                + (hasLink ? UnlinkForm(context, antiforgery) : ""), context, hasLink ? 200 : 503);
        if (!browser.IsCanonical(context)) return Results.Redirect(browser.PublicUrl + PlayerAuthBrowser.Path);
        var requestKey = context.Request.Query["request"].ToString();
        var request = browser.Find(requestKey);
        if (requestKey.Length != 0 && request is null) return Page("Authorization expired", "<p>Request a new authorization link from the server.</p>", context, 400);
        var target = request?.Target ?? context.Request.Query["server"].ToString();
        var policy = catalog.GetSettings().Policies.FirstOrDefault(p => p.Target == target && p.Enabled);
        var name = access.Targets().FirstOrDefault(t => t.Key == target)?.Name;
        if (policy is null || name is null)
        {
            var items = access.Targets().Where(t => catalog.GetSettings().Policies.Any(p => p.Target == t.Key && p.Enabled))
                .Select(t => $"<li><a href=\"{H(context.Request.PathBase + PlayerAuthBrowser.Path + "?server=" + Uri.EscapeDataString(t.Key))}\">{H(t.Name)}</a></li>");
            return Page("Player authentication", "<p>Choose a server to authenticate and review access requirements.</p><ul>" + string.Concat(items) + "</ul>"
                + (hasLink ? UnlinkForm(context, antiforgery) : ""), context);
        }
        var returnPath = PlayerAuthBrowser.Path + (request is not null ? "?request=" + requestKey : "?server=" + Uri.EscapeDataString(target));
        var body = "<p>" + H(name) + "</p><p style=\"white-space:pre-wrap\">" + H(policy.Instructions) + "</p>";
        if (context.Request.Query.ContainsKey("error")) body += "<p role=\"alert\">Authentication failed or the accounts conflict. Retry using the correct accounts; an administrator can remove a conflicting link.</p>";
        if (steam is null)
            return Page("Authenticate your accounts", body + "<p><a href=\"" + H(context.Request.PathBase + "/login?forceSteam=true&returnUrl=" + Uri.EscapeDataString(returnPath)) + "\">Sign in with Steam</a></p>", context);
        if (request is not null && request.SteamId != steam)
            return Page("Steam account mismatch", "<p>Sign in with the Steam account currently playing on the server.</p>", context, 403);
        var csrf = antiforgery.GetAndStoreTokens(context);
        string Form(string action, string label) => "<form method=\"post\" action=\"" + H(context.Request.PathBase + PlayerAuthBrowser.Path + "/" + action) + "\">"
            + Hidden(csrf.FormFieldName, csrf.RequestToken!) + Hidden("server", target) + Hidden("request", requestKey)
            + "<button type=\"submit\">" + H(label) + "</button></form>";
        body += "<p>Steam: " + H(steam) + "</p>";
        var link = links.FindBySteamId(steam);
        if (link is null) body += Form("discord", "Authenticate Discord and link accounts");
        else
        {
            var decision = await access.EvaluateAsync(policy, steam, context.RequestAborted);
            body += "<p>Discord: " + H(link.DiscordUserId) + "</p><p>" + H(decision.Reason) + "</p>";
            if (decision.Grant.Allowed)
                body += "<p>Promotion: " + H(new[] { "None", "Scripter", "Moderator", "Space Master", "Admin" }[decision.Grant.Promotion])
                    + ". Priority joining: " + (decision.Grant.Priority ? "enabled" : "disabled") + ".</p><p>Authorization stored. Return to the game when the server is ready; allow a moment for the policy to apply.</p>";
            else body += Form("authorize", "Authorize this server");
            body += UnlinkForm(context, antiforgery);
        }
        return Page("Player authentication", body, context);
    }

    private static async Task<IResult> ActAsync(string action, HttpContext context, PlayerAuthBrowser browser,
        PlayerAccessCatalog catalog, QuasarPlayerLinks links, IAntiforgery antiforgery)
    {
        if (action != "unlink" && (!browser.Ready || !browser.IsCanonical(context))) return Results.StatusCode(403);
        try { await antiforgery.ValidateRequestAsync(context); }
        catch (AntiforgeryValidationException) { return Results.BadRequest(); }
        var steam = await SteamIdentityAsync(context);
        if (steam is null) return Results.Unauthorized();
        if (action == "unlink")
        {
            // Revoking one's own link must remain possible if OAuth or public verification is unavailable.
            await links.RemoveAsync(steam, context.RequestAborted);
            return Results.Redirect(context.Request.PathBase + PlayerAuthBrowser.Path);
        }
        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        var key = form["request"].ToString();
        var request = browser.Find(key);
        if (key.Length != 0 && (request is null || request.SteamId != steam)) return Results.StatusCode(403);
        var target = request?.Target ?? form["server"].ToString();
        var policy = catalog.GetSettings().Policies.FirstOrDefault(p => p.Target == target && p.Enabled);
        if (policy is null) return Results.NotFound();
        var access = context.RequestServices.GetRequiredService<PlayerAccessService>();
        if (!access.Targets().Any(t => t.Key == target)) return Results.NotFound();
        var returnPath = context.Request.PathBase + PlayerAuthBrowser.Path
            + (request is not null ? "?request=" + key : "?server=" + Uri.EscapeDataString(target));
        if (action == "discord")
        {
            var properties = new AuthenticationProperties { RedirectUri = returnPath };
            properties.Items["steam"] = steam;
            return Results.Challenge(properties, [DiscordScheme]);
        }
        if (action == "authorize")
        {
            try { await access.AuthorizeAsync(policy, steam, context.RequestAborted); }
            catch (InvalidOperationException error) { return Page("Authorization unavailable", "<p>" + H(error.Message) + "</p>", context, 403); }
            browser.Consume(key);
            returnPath = context.Request.PathBase + PlayerAuthBrowser.Path + "?server=" + Uri.EscapeDataString(target);
        }
        else return Results.NotFound();
        return Results.Redirect(returnPath);
    }

    private static string H(string value) => Html.Encode(value);
    private static string UnlinkForm(HttpContext context, IAntiforgery antiforgery)
    {
        var csrf = antiforgery.GetAndStoreTokens(context);
        return "<form method=\"post\" action=\"" + H(context.Request.PathBase + PlayerAuthBrowser.Path + "/unlink") + "\">"
            + Hidden(csrf.FormFieldName, csrf.RequestToken!) + "<button type=\"submit\">Unlink accounts and revoke linked access</button></form>";
    }
    private static string Hidden(string name, string value) => "<input type=\"hidden\" name=\"" + H(name) + "\" value=\"" + H(value) + "\">";
    private static IResult Page(string title, string body, HttpContext context, int status = 200)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        context.Response.Headers.ContentSecurityPolicy = "default-src 'none'; style-src 'unsafe-inline'; form-action 'self' https://discord.com; frame-ancestors 'none'; base-uri 'none'";
        return Results.Content("<!doctype html><html lang=\"en\"><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">"
            + "<title>" + H(title) + " · Quasar</title><style>body{font:1rem/1.6 system-ui,sans-serif;max-width:45rem;margin:3rem auto;padding:0 1.5rem;color:#e6edf3;background:#0e1726}a{color:#9fceff}button{font:inherit;padding:.65rem 1rem;cursor:pointer}form{margin:1rem 0}</style>"
            + "<main><h1>" + H(title) + "</h1>" + body + "</main></html>", "text/html; charset=utf-8", statusCode: status);
    }
}
