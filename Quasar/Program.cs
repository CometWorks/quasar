using Quasar.Components;
using Quasar.Models;
using Quasar.Services;
using Quasar.Services.Analytics;
using Quasar.Services.Auth;
using Quasar.Services.Backup;
using Quasar.Services.Discord;
using Quasar.Services.PluginSdk;
using Quasar.Services.Updates;
using AspNet.Security.OpenId.Steam;
using Magnetar.Protocol.Runtime;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.FileProviders;
using MudBlazor;
using MudBlazor.Services;
using NLog;
using System.Net;
using System.Runtime.InteropServices;
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
            var updateOptions = QuasarUpdateOptions.Create(builder.Configuration);
            var authOptions = QuasarAuthOptions.Create(builder.Configuration);
            var analyticsStoreOptions = AnalyticsStoreOptions.Create(builder.Configuration);

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
            builder.Services.Configure<HostOptions>(options =>
            {
                options.ShutdownTimeout = TimeSpan.FromMinutes(30);
            });
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
                AddRolePolicy(options, QuasarPolicyNames.CanEditServers, QuasarRoles.Editor, QuasarRoles.Admin);
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
            builder.Services.AddSingleton(updateOptions);
            builder.Services.AddSingleton(authOptions);
            builder.Services.AddSingleton<RbacConfigCatalog>();
            builder.Services.AddSingleton(analyticsStoreOptions);
            builder.Services.AddSingleton<QuasarRoleMapper>();
            builder.Services.AddSingleton<TrustedNetworkEvaluator>();
            builder.Services.AddSingleton<QuasarAuthSettingsService>();
            builder.Services.AddSingleton<KnownPlayerCatalog>();
            builder.Services.AddSingleton<MetricsStoreService>();
            builder.Services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<MetricsStoreService>());
            builder.Services.AddSingleton<AnalyticsSeriesService>();
            builder.Services.AddSingleton<ProfilerStoreService>();
            builder.Services.AddSingleton<AgentRegistry>();
            builder.Services.AddSingleton<EntityService>();
            builder.Services.AddSingleton<QuasarConfigProfileCatalog>();
            builder.Services.AddSingleton<QuasarDevFolderCatalog>();
            builder.Services.AddSingleton<QuasarWorldTemplateCatalog>();
            builder.Services.AddScoped<WorldTemplateImportLocationService>();
            builder.Services.AddSingleton<QuasarPluginCatalogService>();
            builder.Services.AddSingleton<PluginCatalogRefreshService>();
            builder.Services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<PluginCatalogRefreshService>());
            builder.Services.AddSingleton<SteamWorkshopCredentialsCatalog>();
            builder.Services.AddSingleton<QuasarWorkshopModResolver>();
            builder.Services.AddSingleton<ManagedDedicatedServerRuntimeResolver>();
            builder.Services.AddSingleton<ManagedRuntimeWarmupService>();
            builder.Services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<ManagedRuntimeWarmupService>());
            builder.Services.AddSingleton<DedicatedServerCatalog>();
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
            builder.Services.AddSingleton<DiscordSimSpeedAlertService>();
            builder.Services.AddSingleton<DiscordLogRelayService>();
            builder.Services.AddSingleton<DiscordAnalyticsExportService>();
            builder.Services.AddSingleton<DiscordBotService>();
            builder.Services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<DiscordBotService>());
            builder.Services.AddSingleton<BrandingService>();
            builder.Services.AddScoped<ThemePreferenceService>();
            builder.Services.AddSingleton<QuasarShutdownService>();
            builder.Services.AddSingleton<QuasarUpdateService>();
            builder.Services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<QuasarUpdateService>());
            builder.Services.AddSingleton<QuasarBackupSettingsService>();
            builder.Services.AddSingleton<ServerRestoreCoordinator>();
            builder.Services.AddSingleton<QuasarBackupService>();
            builder.Services.AddSingleton<AutomaticBackupService>();
            builder.Services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<AutomaticBackupService>());

            var app = builder.Build();

            if (!app.Environment.IsDevelopment())
                app.UseExceptionHandler("/Error");

            app.UseForwardedHeaders(CreateForwardedHeadersOptions(authOptions));
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

            app.MapGet("/api/health", (WebServiceState state, DedicatedServerCatalog catalog) => Results.Json(new
            {
                status = "ok",
                state.Options.WorkerId,
                state.Options.HostId,
                state.Options.HostName,
                state.Options.Version,
                baseUrl = string.IsNullOrWhiteSpace(state.CurrentManifest.BaseUrl)
                    ? state.Options.BaseUrl
                    : state.CurrentManifest.BaseUrl,
                connectedAgents = state.Registry.GetAgents().Count(agent => agent.IsConnected),
                configuredServers = catalog.GetServers().Count,
                runningServers = state.Supervisor.GetSnapshots().Count(snapshot =>
                    snapshot.State is DedicatedServerProcessState.Starting
                        or DedicatedServerProcessState.Running
                        or DedicatedServerProcessState.Restarting
                        or DedicatedServerProcessState.Stopping),
            }));

            app.MapGet("/api/discovery", (WebServiceState state) =>
                Results.Json(state.CurrentManifest));

            // Analytics chart data, fetched directly by the browser (uPlot) instead of being pushed
            // through the Blazor SignalR circuit. Averaged down to maxPoints per series server-side.
            var analyticsSeries = app.MapGet("/api/analytics/series", (HttpContext context, AnalyticsSeriesService seriesService) =>
            {
                var query = context.Request.Query;
                _ = long.TryParse(query["from"], out var fromUnix);
                _ = long.TryParse(query["to"], out var toUnix);
                _ = int.TryParse(query["maxPoints"], out var maxPoints);
                var servers = query["servers"].Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!).ToArray();
                var metrics = query["metrics"].Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!).ToArray();
                return Results.Json(seriesService.Build(fromUnix, toUnix, servers, metrics, maxPoints));
            });
            if (authOptions.Enabled)
                analyticsSeries.RequireAuthorization(QuasarPolicyNames.CanView);

            var serverLogDownload = app.MapGet("/api/servers/{uniqueName}/logs/server/download", (string uniqueName, DedicatedServerCatalog catalog) =>
                DownloadLogFile(ResolveLatestDedicatedServerLogPath(uniqueName, catalog)));

            var magnetarLogDownload = app.MapGet("/api/servers/{uniqueName}/logs/magnetar/download", (string uniqueName, DedicatedServerCatalog catalog) =>
                DownloadLogFile(ResolveMagnetarInfoLogPath(uniqueName, catalog)));

            var discordLogDownload = app.MapGet("/api/discord/log/download", (WebServiceOptions options) =>
                DownloadLogFile(QuasarLoggingConfigurator.ResolveDiscordLogPath(options)));

            if (authOptions.Enabled)
            {
                serverLogDownload.RequireAuthorization(QuasarPolicyNames.CanView);
                magnetarLogDownload.RequireAuthorization(QuasarPolicyNames.CanView);
                discordLogDownload.RequireAuthorization(QuasarPolicyNames.CanManageDiscord);
            }

            // Generates a fresh configuration backup and streams it as a download.
            var backupDownload = app.MapGet("/api/backup/download", (QuasarBackupService backup) =>
            {
                var archive = backup.CreateBackup(DateTimeOffset.Now);
                return Results.File(archive.Content, "application/zip", archive.FileName);
            });

            // Downloads an existing backup ZIP from the Backups directory by file name.
            var backupDownloadByName = app.MapGet("/api/backup/download/{name}", (string name, QuasarBackupService backup) =>
            {
                var path = backup.ResolveBackupPath(name);
                return path is null
                    ? Results.NotFound()
                    : Results.File(path, "application/zip", name);
            });

            if (authOptions.Enabled)
            {
                backupDownload.RequireAuthorization(QuasarPolicyNames.CanManageSecurity);
                backupDownloadByName.RequireAuthorization(QuasarPolicyNames.CanManageSecurity);
            }

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

            app.MapGet("/access-denied", () => Results.Content(
                    CreateAccessDeniedHtml(),
                    "text/html",
                    statusCode: StatusCodes.Status403Forbidden))
                .AllowAnonymous();

            app.MapPost("/api/internal/drain", (HttpContext context, DedicatedServerSupervisor supervisor, QuasarShutdownService shutdownService, IHostApplicationLifetime lifetime, TrustedNetworkEvaluator trustedNetworkEvaluator) =>
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

                var stopServers = bool.TryParse(context.Request.Query["stopServers"], out var parsedStopServers) && parsedStopServers;
                if (!stopServers)
                    supervisor.BeginLauncherDrain();

                _ = Task.Run(async () =>
                {
                    try
                    {
                        if (delaySeconds > 0)
                            await Task.Delay(TimeSpan.FromSeconds(delaySeconds));

                        if (stopServers)
                            await shutdownService.ShutdownAsync(cancellationToken: CancellationToken.None);
                        else
                            lifetime.StopApplication();
                    }
                    catch
                    {
                    }
                    finally
                    {
                        if (stopServers)
                            lifetime.StopApplication();
                    }
                });

                return Results.Ok(new
                {
                    status = "draining",
                    delaySeconds,
                    stopServers,
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

            using var gracefulShutdownSignals = RegisterGracefulShutdownSignals(app.Services);
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

        yield return MagnetarPaths.GetQuasarDirectory();
    }

    private static IDisposable RegisterGracefulShutdownSignals(IServiceProvider services)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return EmptyDisposable.Shared;

        var shutdownService = services.GetRequiredService<QuasarShutdownService>();
        var lifetime = services.GetRequiredService<IHostApplicationLifetime>();
        var options = services.GetRequiredService<WebServiceOptions>();
        var logger = services.GetRequiredService<ILogger<Program>>();
        var started = 0;

        void HandleSignal(PosixSignalContext context)
        {
            context.Cancel = true;
            if (Interlocked.Exchange(ref started, 1) != 0)
                return;

            _ = Task.Run(async () =>
            {
                try
                {
                    // When preserving managed servers, just trigger a graceful host
                    // stop and let the preserve-aware DedicatedServerSupervisor.StopAsync
                    // leave the detached Magnetars running. Only stop the servers when
                    // the policy says to take them down with Quasar.
                    if (options.PreserveManagedServersOnShutdown)
                        lifetime.StopApplication();
                    else
                        await shutdownService.ShutdownAsync(cancellationToken: CancellationToken.None);
                }
                catch (Exception exception)
                {
                    logger.LogError(exception, "Graceful Quasar signal shutdown failed.");
                    lifetime.StopApplication();
                }
            });
        }

        return new CompositeDisposable(
            PosixSignalRegistration.Create(PosixSignal.SIGINT, HandleSignal),
            PosixSignalRegistration.Create(PosixSignal.SIGTERM, HandleSignal));
    }

    private static void AddRolePolicy(AuthorizationOptions options, string policyName, params string[] roles)
    {
        options.AddPolicy(policyName, policy => policy.RequireRole(roles));
    }

    private static ForwardedHeadersOptions CreateForwardedHeadersOptions(QuasarAuthOptions authOptions)
    {
        var options = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor |
                ForwardedHeaders.XForwardedProto |
                ForwardedHeaders.XForwardedHost,
            ForwardLimit = 1,
        };

        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
        options.KnownProxies.Add(IPAddress.Loopback);
        options.KnownProxies.Add(IPAddress.IPv6Loopback);

        foreach (var proxy in authOptions.TrustedNetworkBypass.TrustedProxies)
            AddTrustedProxy(options, proxy);

        return options;
    }

    private static void AddTrustedProxy(ForwardedHeadersOptions options, string value)
    {
        var slashIndex = value.IndexOf('/');
        if (slashIndex < 0)
        {
            if (IPAddress.TryParse(value, out var address))
                options.KnownProxies.Add(address);
            return;
        }

        var addressPart = value[..slashIndex];
        var prefixPart = value[(slashIndex + 1)..];
        if (!IPAddress.TryParse(addressPart, out var prefix) ||
            !int.TryParse(prefixPart, out var prefixLength))
        {
            return;
        }

        var maxPrefixLength = prefix.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128;
        if (prefixLength < 0 || prefixLength > maxPrefixLength)
            return;

        options.KnownIPNetworks.Add(new System.Net.IPNetwork(prefix, prefixLength));
    }

    private static IResult DownloadLogFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return Results.NotFound();

        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Results.File(stream, "text/plain; charset=utf-8", Path.GetFileName(path));
    }

    private static string? ResolveLatestDedicatedServerLogPath(string uniqueName, DedicatedServerCatalog catalog)
    {
        var server = catalog.GetServer(uniqueName);
        if (server is null)
            return null;

        var appDataPath = string.IsNullOrWhiteSpace(server.DedicatedServerAppDataPath)
            ? MagnetarPaths.GetQuasarServerDedicatedServerAppDataDirectory(uniqueName)
            : server.DedicatedServerAppDataPath.Trim();

        if (!Directory.Exists(appDataPath))
            return null;

        return Directory.EnumerateFiles(appDataPath, "SpaceEngineersDedicated*.log", SearchOption.TopDirectoryOnly)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    private static string? ResolveMagnetarInfoLogPath(string uniqueName, DedicatedServerCatalog catalog)
    {
        var server = catalog.GetServer(uniqueName);
        if (server is null)
            return null;

        var appDataPath = string.IsNullOrWhiteSpace(server.MagnetarAppDataPath)
            ? MagnetarPaths.GetQuasarServerMagnetarAppDataDirectory(uniqueName)
            : server.MagnetarAppDataPath.Trim();

        return Path.Combine(appDataPath, "info.log");
    }

    private sealed class CompositeDisposable(params IDisposable[] disposables) : IDisposable
    {
        public void Dispose()
        {
            foreach (var disposable in disposables)
                disposable.Dispose();
        }
    }

    private sealed class EmptyDisposable : IDisposable
    {
        public static readonly EmptyDisposable Shared = new();

        public void Dispose()
        {
        }
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
        <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>Access denied - Quasar</title>
            <link rel="icon" type="image/png" href="/Quasar.png">
            <style>
                :root {
                    color-scheme: light dark;
                    --bg: #f6f7fb;
                    --surface: #ffffff;
                    --surface-soft: #f2f5f9;
                    --text: #20242a;
                    --muted: #5f6b7a;
                    --line: #dce2ea;
                    --primary: #3668d8;
                    --primary-hover: #2854b9;
                    --danger: #c23b3b;
                    --danger-soft: #f8e8e8;
                    --shadow: 0 20px 48px rgba(28, 39, 57, 0.14);
                }

                @media (prefers-color-scheme: dark) {
                    :root {
                        --bg: #101418;
                        --surface: #171d24;
                        --surface-soft: #202832;
                        --text: #eef2f6;
                        --muted: #a6b0bd;
                        --line: #303a46;
                        --primary: #83a8ff;
                        --primary-hover: #a2bdff;
                        --danger: #ff8a8a;
                        --danger-soft: rgba(255, 138, 138, 0.12);
                        --shadow: 0 20px 48px rgba(0, 0, 0, 0.32);
                    }
                }

                * {
                    box-sizing: border-box;
                }

                html,
                body {
                    margin: 0;
                    min-height: 100%;
                }

                body {
                    min-height: 100vh;
                    display: grid;
                    place-items: center;
                    padding: 2rem;
                    background: linear-gradient(135deg, var(--bg), var(--surface-soft));
                    color: var(--text);
                    font-family: "Roboto", "Helvetica", "Arial", sans-serif;
                    line-height: 1.5;
                }

                main {
                    width: min(100%, 48rem);
                    background: var(--surface);
                    border: 1px solid var(--line);
                    border-radius: 8px;
                    box-shadow: var(--shadow);
                    overflow: hidden;
                }

                .brand {
                    display: flex;
                    align-items: center;
                    gap: 1rem;
                    padding: 1.25rem 1.5rem;
                    border-bottom: 1px solid var(--line);
                }

                .brand img {
                    width: 48px;
                    height: 48px;
                    object-fit: contain;
                }

                .brand strong {
                    display: block;
                    font-size: 1.05rem;
                    font-weight: 700;
                }

                .brand span {
                    color: var(--muted);
                    font-size: 0.9rem;
                }

                .content {
                    display: grid;
                    gap: 1.5rem;
                    padding: clamp(1.5rem, 4vw, 2.5rem);
                }

                .status {
                    display: inline-flex;
                    width: max-content;
                    align-items: center;
                    gap: 0.5rem;
                    padding: 0.35rem 0.65rem;
                    border: 1px solid rgba(194, 59, 59, 0.28);
                    border-radius: 999px;
                    background: var(--danger-soft);
                    color: var(--danger);
                    font-size: 0.82rem;
                    font-weight: 700;
                    letter-spacing: 0.03em;
                    text-transform: uppercase;
                }

                h1 {
                    margin: 0;
                    max-width: 38rem;
                    font-size: clamp(2rem, 5vw, 3.25rem);
                    line-height: 1.05;
                    letter-spacing: 0;
                }

                p {
                    margin: 0;
                    max-width: 42rem;
                    color: var(--muted);
                    font-size: 1rem;
                }

                .steps {
                    margin: 0;
                    padding: 1rem 1rem 1rem 2.25rem;
                    border: 1px solid var(--line);
                    border-radius: 8px;
                    background: var(--surface-soft);
                    color: var(--muted);
                }

                .steps li + li {
                    margin-top: 0.35rem;
                }

                .actions {
                    display: flex;
                    flex-wrap: wrap;
                    gap: 0.75rem;
                    padding-top: 0.25rem;
                }

                a {
                    color: inherit;
                }

                .button {
                    display: inline-flex;
                    align-items: center;
                    justify-content: center;
                    min-height: 2.75rem;
                    padding: 0.65rem 1rem;
                    border-radius: 6px;
                    border: 1px solid var(--line);
                    font-weight: 700;
                    text-decoration: none;
                }

                .button-primary {
                    border-color: var(--primary);
                    background: var(--primary);
                    color: #ffffff;
                }

                .button-primary:hover,
                .button-primary:focus-visible {
                    border-color: var(--primary-hover);
                    background: var(--primary-hover);
                }

                .button-secondary:hover,
                .button-secondary:focus-visible {
                    border-color: var(--primary);
                    color: var(--primary);
                }

                @media (max-width: 520px) {
                    body {
                        padding: 1rem;
                        place-items: stretch;
                    }

                    main {
                        align-self: center;
                    }

                    .brand {
                        padding: 1rem;
                    }

                    .brand img {
                        width: 40px;
                        height: 40px;
                    }

                    .actions,
                    .button {
                        width: 100%;
                    }
                }
            </style>
        </head>
        <body>
            <main>
                <section class="brand" aria-label="Quasar">
                    <img src="/Quasar.png" alt="Quasar logo">
                    <div>
                        <strong>Quasar</strong>
                        <span>Space Engineers server supervisor</span>
                    </div>
                </section>

                <section class="content">
                    <span class="status">403 access denied</span>
                    <h1>You are signed in, but this account has no Quasar role.</h1>
                    <p>
                        Quasar accepted the Steam login, then blocked this session because the account is not mapped
                        to a viewer, editor, or admin role.
                    </p>
                    <ol class="steps">
                        <li>Ask an administrator to add your SteamID to Quasar security settings.</li>
                        <li>Sign out, then sign back in after the role has been assigned.</li>
                    </ol>
                    <div class="actions" aria-label="Access denied actions">
                        <a class="button button-primary" href="/logout">Sign out</a>
                        <a class="button button-secondary" href="/login?forceSteam=true">Retry Steam sign-in</a>
                    </div>
                </section>
            </main>
        </body>
        </html>
        """;
}
