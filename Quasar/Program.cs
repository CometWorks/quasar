using Quasar.Components;
using Quasar.Models;
using Quasar.Services;
using Quasar.Services.Analytics;
using Quasar.Services.Auth;
using Quasar.Services.Discord;
using Quasar.Services.PluginSdk;
using AspNet.Security.OpenId.Steam;
using Magnetar.Protocol.Runtime;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.FileProviders;
using MudBlazor;
using MudBlazor.Services;
using NLog;
using System.Security.Claims;

namespace Quasar;

public class Program
{
    public static void Main(string[] args)
    {
        try
        {
            var builder = WebApplication.CreateBuilder(args);
            AddDeploymentConfigurationSources(builder.Configuration, builder.Environment.EnvironmentName, args);
            if (ShouldUseSourceStaticWebAssets())
                builder.WebHost.UseStaticWebAssets();

            var webServiceOptions = WebServiceOptions.Create(builder.Configuration);
            var managedRuntimeOptions = ManagedRuntimeOptions.Create(builder.Configuration);
            var authOptions = QuasarAuthOptions.Create(builder.Configuration);

            QuasarLoggingConfigurator.Configure(builder, webServiceOptions);

            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ASPNETCORE_URLS")))
            {
                if (ShouldListenOnAnyInterface(webServiceOptions.Host))
                    builder.WebHost.ConfigureKestrel(options => options.ListenAnyIP(webServiceOptions.Port));
                else
                    builder.WebHost.UseUrls(webServiceOptions.ListenUrl);
            }

            builder.Services.AddRazorComponents()
                .AddInteractiveServerComponents();
            builder.Services.AddCascadingAuthenticationState();
            builder.Services.AddAuthentication(options =>
                {
                    options.DefaultScheme = QuasarAuthSchemes.Cookie;
                    options.DefaultChallengeScheme = SteamAuthenticationDefaults.AuthenticationScheme;
                })
                .AddCookie(QuasarAuthSchemes.Cookie, options =>
                {
                    options.LoginPath = "/login";
                    options.LogoutPath = "/logout";
                    options.AccessDeniedPath = "/access-denied";
                    options.Cookie.Name = "Quasar.Auth";
                    options.Cookie.HttpOnly = true;
                    options.Cookie.SameSite = SameSiteMode.Lax;
                    options.SlidingExpiration = true;
                    options.ExpireTimeSpan = TimeSpan.FromHours(12);
                })
                .AddSteam(options =>
                {
                    options.SignInScheme = QuasarAuthSchemes.Cookie;
                    options.CorrelationCookie.SameSite = SameSiteMode.Lax;
                    options.CorrelationCookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;

                    options.Events.OnAuthenticated = context =>
                    {
                        var roleMapper = context.HttpContext.RequestServices.GetRequiredService<QuasarRoleMapper>();
                        var identity = context.Identity;
                        if (identity is null)
                        {
                            context.Ticket = null!;
                            return Task.CompletedTask;
                        }

                        var steamId = ExtractSteamId(context.Identifier) ??
                                      ExtractSteamId(new ClaimsPrincipal(identity));
                        if (steamId is null)
                        {
                            context.Ticket = null!;
                            return Task.CompletedTask;
                        }

                        if (!roleMapper.IsSteamIdAllowed(steamId))
                        {
                            context.Ticket = null!;
                            return Task.CompletedTask;
                        }

                        AddOrReplaceClaim(identity, ClaimTypes.NameIdentifier, steamId);
                        AddOrReplaceClaim(identity, ClaimTypes.Name, steamId);
                        AddOrReplaceClaim(identity, QuasarClaimTypes.Provider, QuasarAuthSchemes.Steam);
                        AddOrReplaceClaim(identity, QuasarClaimTypes.SteamId, steamId);
                        AddOrReplaceClaim(identity, QuasarClaimTypes.SteamProfileUrl, $"https://steamcommunity.com/profiles/{steamId}");

                        foreach (var role in roleMapper.GetSteamRoles(steamId))
                            identity.AddClaim(new Claim(ClaimTypes.Role, role));

                        return Task.CompletedTask;
                    };
                });
            builder.Services.AddAuthorization(options =>
            {
                AddRolePolicy(options, QuasarPolicyNames.CanView, QuasarRoles.Viewer, QuasarRoles.Editor, QuasarRoles.Admin);
                AddRolePolicy(options, QuasarPolicyNames.CanEditConfigs, QuasarRoles.Editor, QuasarRoles.Admin);
                AddRolePolicy(options, QuasarPolicyNames.CanEditInstances, QuasarRoles.Editor, QuasarRoles.Admin);
                AddRolePolicy(options, QuasarPolicyNames.CanControlServers, QuasarRoles.Editor, QuasarRoles.Admin);
                AddRolePolicy(options, QuasarPolicyNames.CanManageDiscord, QuasarRoles.Editor, QuasarRoles.Admin);
                AddRolePolicy(options, QuasarPolicyNames.CanManageAppearance, QuasarRoles.Editor, QuasarRoles.Admin);
                AddRolePolicy(options, QuasarPolicyNames.CanManageSecurity, QuasarRoles.Admin);
                AddRolePolicy(options, QuasarPolicyNames.CanShutdownQuasar, QuasarRoles.Admin);
            });
            var dataProtectionKeyringDirectory = MagnetarPaths.GetQuasarDataProtectionKeyringDirectory();
            Directory.CreateDirectory(dataProtectionKeyringDirectory);
            builder.Services.AddDataProtection()
                .SetApplicationName("Quasar")
                .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionKeyringDirectory));
            builder.Services.AddHttpClient();
            builder.Services.AddLocalStorageServices();
            builder.Services.AddMudServices(configuration =>
            {
                configuration.SnackbarConfiguration.PositionClass = Defaults.Classes.Position.BottomStart;
                configuration.SnackbarConfiguration.PreventDuplicates = true;
                configuration.SnackbarConfiguration.NewestOnTop = true;
            });
            builder.Services.AddSingleton(webServiceOptions);
            builder.Services.AddSingleton(managedRuntimeOptions);
            builder.Services.AddSingleton(authOptions);
            builder.Services.AddSingleton<RbacConfigCatalog>();
            builder.Services.AddSingleton<QuasarRoleMapper>();
            builder.Services.AddSingleton<TrustedNetworkEvaluator>();
            builder.Services.AddSingleton<KnownPlayerCatalog>();
            builder.Services.AddSingleton<MetricsStoreService>();
            builder.Services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<MetricsStoreService>());
            builder.Services.AddSingleton<AgentRegistry>();
            builder.Services.AddSingleton<EntityService>();
            builder.Services.AddSingleton<QuasarConfigProfileCatalog>();
            builder.Services.AddSingleton<QuasarDevFolderCatalog>();
            builder.Services.AddSingleton<QuasarWorldTemplateCatalog>();
            builder.Services.AddSingleton<QuasarPluginCatalogService>();
            builder.Services.AddSingleton<SteamWorkshopCredentialsCatalog>();
            builder.Services.AddSingleton<QuasarWorkshopModResolver>();
            builder.Services.AddSingleton<ManagedDedicatedServerRuntimeResolver>();
            builder.Services.AddSingleton<ManagedRuntimeWarmupService>();
            builder.Services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<ManagedRuntimeWarmupService>());
            builder.Services.AddSingleton<DedicatedServerInstanceCatalog>();
            builder.Services.AddSingleton<DedicatedServerSupervisor>();
            builder.Services.AddSingleton<DedicatedServerRuntimePreparer>();
            builder.Services.AddSingleton<FileBrowserService>();
            builder.Services.AddSingleton<WebServiceState>();
            builder.Services.AddSingleton<PluginLogStream>();
            builder.Services.AddSingleton<PluginConfigService>();
            builder.Services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<PluginConfigService>());
            builder.Services.AddSingleton<AgentSocketHandler>();
            builder.Services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<DedicatedServerSupervisor>());
            builder.Services.AddHostedService<WebServiceManifestHostedService>();
            builder.Services.AddSingleton<DiscordOptionsCatalog>();
            builder.Services.AddSingleton<DiscordRateLimiter>();
            builder.Services.AddSingleton<DeathMessagesCatalog>();
            builder.Services.AddSingleton<DiscordCommandDispatcher>();
            builder.Services.AddSingleton<DiscordCommandRouter>();
            builder.Services.AddSingleton<DiscordChatRelayService>();
            builder.Services.AddSingleton<DiscordDeathRelayService>();
            builder.Services.AddSingleton<DiscordLogRelayService>();
            builder.Services.AddSingleton<DiscordAnalyticsExportService>();
            builder.Services.AddSingleton<DiscordBotService>();
            builder.Services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<DiscordBotService>());
            builder.Services.AddSingleton<BrandingService>();
            builder.Services.AddScoped<ThemePreferenceService>();
            builder.Services.AddSingleton<QuasarShutdownService>();

            var app = builder.Build();

            if (!app.Environment.IsDevelopment())
                app.UseExceptionHandler("/Error");

            app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
            app.UseWebSockets(new WebSocketOptions
            {
                KeepAliveInterval = TimeSpan.FromSeconds(30),
            });
            app.UseAuthentication();
            app.Use(async (context, next) =>
            {
                if (authOptions.Enabled &&
                    context.User.Identity?.IsAuthenticated != true &&
                    context.RequestServices.GetRequiredService<TrustedNetworkEvaluator>().IsTrusted(context))
                {
                    context.User = context.RequestServices.GetRequiredService<QuasarRoleMapper>()
                        .CreateTrustedNetworkPrincipal();
                }

                await next(context);
            });
            app.UseAuthorization();
            app.UseAntiforgery();

            app.MapGet("/api/health", (WebServiceState state, DedicatedServerInstanceCatalog catalog) => Results.Json(new
            {
                status = "ok",
                state.Options.InstanceId,
                state.Options.NodeId,
                state.Options.NodeName,
                state.Options.Version,
                baseUrl = string.IsNullOrWhiteSpace(state.CurrentManifest.BaseUrl)
                    ? state.Options.BaseUrl
                    : state.CurrentManifest.BaseUrl,
                connectedAgents = state.Registry.GetAgents().Count(agent => agent.IsConnected),
                configuredInstances = catalog.GetInstances().Count,
                runningInstances = state.Supervisor.GetSnapshots().Count(snapshot =>
                    snapshot.State is DedicatedServerInstanceProcessState.Starting
                        or DedicatedServerInstanceProcessState.Running
                        or DedicatedServerInstanceProcessState.Restarting
                        or DedicatedServerInstanceProcessState.Stopping),
            }));

            app.MapGet("/api/discovery", (WebServiceState state) =>
                Results.Json(state.CurrentManifest));

            app.MapGet("/login", (HttpContext context) =>
            {
                if (!authOptions.Enabled)
                    return Results.Redirect("/");

                var forceSteam = bool.TryParse(context.Request.Query["forceSteam"], out var parsedForceSteam) && parsedForceSteam;
                if (context.User.Identity?.IsAuthenticated == true && !forceSteam)
                    return Results.Redirect(SanitizeReturnUrl(context.Request.Query["returnUrl"]));

                if (!authOptions.Steam.Enabled ||
                    !string.Equals(authOptions.DefaultProvider, QuasarAuthSchemes.Steam, StringComparison.OrdinalIgnoreCase))
                {
                    return Results.Content(CreateLoginUnavailableHtml(), "text/html");
                }

                return Results.Challenge(new AuthenticationProperties
                {
                    RedirectUri = SanitizeReturnUrl(context.Request.Query["returnUrl"]),
                    IsPersistent = true,
                }, [SteamAuthenticationDefaults.AuthenticationScheme]);
            }).AllowAnonymous();

            app.MapGet("/logout", async (HttpContext context) =>
            {
                await context.SignOutAsync(QuasarAuthSchemes.Cookie);
                return Results.Redirect("/");
            }).AllowAnonymous();

            app.MapGet("/access-denied", () => Results.Content(CreateAccessDeniedHtml(), "text/html"))
                .AllowAnonymous();

            app.MapPost("/api/internal/drain", (HttpContext context, DedicatedServerSupervisor supervisor, IHostApplicationLifetime lifetime, TrustedNetworkEvaluator trustedNetworkEvaluator) =>
            {
                var expectedToken = context.RequestServices.GetRequiredService<WebServiceOptions>().LauncherToken;
                if (string.IsNullOrWhiteSpace(expectedToken) ||
                    !string.Equals(context.Request.Headers["X-Quasar-Launcher-Token"], expectedToken, StringComparison.Ordinal))
                {
                    return Results.Unauthorized();
                }

                if (authOptions.Enabled && !trustedNetworkEvaluator.IsTrusted(context))
                    return Results.Unauthorized();

                var delaySeconds = 0;
                if (int.TryParse(context.Request.Query["delaySeconds"], out var parsedDelay))
                    delaySeconds = Math.Max(0, parsedDelay);

                supervisor.BeginLauncherDrain();
                _ = Task.Run(async () =>
                {
                    try
                    {
                        if (delaySeconds > 0)
                            await Task.Delay(TimeSpan.FromSeconds(delaySeconds));
                    }
                    catch
                    {
                    }
                    finally
                    {
                        lifetime.StopApplication();
                    }
                });

                return Results.Ok(new
                {
                    status = "draining",
                    delaySeconds,
                });
            });

            app.Map("/ws/agent", async (HttpContext context, AgentSocketHandler socketHandler) =>
            {
                await socketHandler.HandleAsync(context);
            });

            app.MapStaticAssets();

            // Runtime-uploaded branding assets (logos, favicon) live outside the
            // build-time static-asset manifest, so serve them with the classic
            // static-file middleware from the physical branding directory.
            var brandingWebRootPath = string.IsNullOrWhiteSpace(app.Environment.WebRootPath)
                ? Path.Combine(app.Environment.ContentRootPath, "wwwroot")
                : app.Environment.WebRootPath;
            var brandingAssetsDirectory = MagnetarPaths.GetQuasarBrandingDirectory(brandingWebRootPath);
            Directory.CreateDirectory(brandingAssetsDirectory);
            app.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = new PhysicalFileProvider(brandingAssetsDirectory),
                RequestPath = "/branding",
            });

            var razorComponents = app.MapRazorComponents<App>()
                .AddInteractiveServerRenderMode();
            if (authOptions.Enabled)
                razorComponents.RequireAuthorization(QuasarPolicyNames.CanView);

            app.Run();
        }
        catch (Exception exception)
        {
            try
            {
                LogManager.GetCurrentClassLogger().Fatal(exception, "Quasar terminated unexpectedly.");
            }
            catch
            {
            }

            Console.Error.WriteLine($"[Quasar:FATAL] {exception.GetType().Name}: {exception.Message}");
            throw;
        }
        finally
        {
            LogManager.Shutdown();
        }
    }

    private static bool ShouldUseSourceStaticWebAssets()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; directory is not null && depth < 6; depth++, directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Quasar.csproj")))
                return true;
        }

        return false;
    }

    private static bool ShouldListenOnAnyInterface(string host)
    {
        return string.Equals(host, "0.0.0.0", StringComparison.Ordinal) ||
               string.Equals(host, "[::]", StringComparison.Ordinal) ||
               string.Equals(host, "*", StringComparison.Ordinal) ||
               string.Equals(host, "+", StringComparison.Ordinal);
    }

    private static void AddDeploymentConfigurationSources(ConfigurationManager configuration, string environmentName, string[] args)
    {
        foreach (var directory in EnumerateConfigurationDirectories())
        {
            configuration.AddJsonFile(Path.Combine(directory, "appsettings.json"), optional: true, reloadOnChange: true);
            configuration.AddJsonFile(Path.Combine(directory, $"appsettings.{environmentName}.json"), optional: true, reloadOnChange: true);
        }

        configuration.AddEnvironmentVariables();
        if (args.Length > 0)
            configuration.AddCommandLine(args);
    }

    private static IEnumerable<string> EnumerateConfigurationDirectories()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var directory in EnumerateCandidateConfigurationDirectories())
        {
            if (string.IsNullOrWhiteSpace(directory))
                continue;

            var fullPath = Path.GetFullPath(directory);
            if (seen.Add(fullPath))
                yield return fullPath;
        }
    }

    private static IEnumerable<string> EnumerateCandidateConfigurationDirectories()
    {
        yield return AppContext.BaseDirectory;
        yield return Directory.GetCurrentDirectory();
        yield return Path.Combine(AppContext.BaseDirectory, "WebService");

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; directory is not null && depth < 8; depth++, directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "appsettings.json")))
                yield return directory.FullName;

            var sourceQuasar = Path.Combine(directory.FullName, "Quasar");
            if (File.Exists(Path.Combine(sourceQuasar, "appsettings.json")))
                yield return sourceQuasar;
        }
    }

    private static void AddRolePolicy(AuthorizationOptions options, string policyName, params string[] roles)
    {
        options.AddPolicy(policyName, policy => policy.RequireRole(roles));
    }

    private static string SanitizeReturnUrl(string? returnUrl)
    {
        if (string.IsNullOrWhiteSpace(returnUrl))
            return "/";

        if (!returnUrl.StartsWith("/", StringComparison.Ordinal) ||
            returnUrl.StartsWith("//", StringComparison.Ordinal) ||
            returnUrl.Contains('\\'))
        {
            return "/";
        }

        return returnUrl;
    }

    private static string? ExtractSteamId(ClaimsPrincipal? principal)
    {
        if (principal is null)
            return null;

        foreach (var claim in principal.Claims)
        {
            var steamId = ExtractSteamId(claim.Value);
            if (steamId is not null)
                return steamId;
        }

        return null;
    }

    private static string? ExtractSteamId(string? value)
    {
        value = value?.Trim();
        if (string.IsNullOrWhiteSpace(value))
            return null;

        if (value.Length == 17 && value.All(char.IsDigit))
            return value;

        var lastSlash = value.LastIndexOf('/');
        if (lastSlash >= 0 && lastSlash + 1 < value.Length)
        {
            var suffix = value[(lastSlash + 1)..];
            if (suffix.Length == 17 && suffix.All(char.IsDigit))
                return suffix;
        }

        return null;
    }

    private static void AddOrReplaceClaim(ClaimsIdentity identity, string type, string value)
    {
        foreach (var existing in identity.FindAll(type).ToList())
            identity.RemoveClaim(existing);

        identity.AddClaim(new Claim(type, value));
    }

    private static string CreateLoginUnavailableHtml() => """
        <!doctype html>
        <html lang="en">
        <head><meta charset="utf-8"><title>Login unavailable</title></head>
        <body>
        <h1>Login unavailable</h1>
        <p>Steam is not enabled as the active Quasar auth provider.</p>
        </body>
        </html>
        """;

    private static string CreateAccessDeniedHtml() => """
        <!doctype html>
        <html lang="en">
        <head><meta charset="utf-8"><title>Access denied</title></head>
        <body>
        <h1>Access denied</h1>
        <p>Your account authenticated, but Quasar did not grant a role that can view this app.</p>
        <p>Ask an administrator to map your SteamID to viewer, editor, or admin.</p>
        <p><a href="/logout">Sign out</a></p>
        </body>
        </html>
        """;
}
