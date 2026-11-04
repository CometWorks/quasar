using Quasar.Components;
using Quasar.Hosting;
using Quasar.Models;
using Quasar.Networking;
using Quasar.Services;
using Quasar.Services.Analytics;
using Quasar.Services.Auth;
using Quasar.Services.ServerList;
using Quasar.Services.Backup;
using Quasar.Services.Discord;
using Quasar.Services.PluginSdk;
using Quasar.Services.Plugins;
using Quasar.Services.Updates;
using Quasar.Updates;
using System.Diagnostics;
using Quasar.Plugin.Abstractions.Companion;
using AspNet.Security.OpenId.Steam;
using Magnetar.Protocol.Runtime;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Hosting.Server;
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
        try { QuasarServiceInstallation.Load(applyEnvironment: true); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException or ArgumentException)
        {
            Console.Error.WriteLine("Cannot read this installation: " + exception.Message + " Use an elevated Windows shell or the configured Linux service account; repair installation metadata if needed.");
            Environment.ExitCode = 3;
            return;
        }
        if (QuasarApplicationEntry.TryRunCommand(args, out var exitCode)) { Environment.ExitCode = exitCode; return; }
        try { QuasarUpgradePath.EnsureAllowed(QuasarReleaseVersion.GetEntryAssemblyVersion(), MagnetarPaths.GetQuasarDirectory()); }
        catch (InvalidOperationException exception) { Console.Error.WriteLine(exception.Message); Environment.ExitCode = 3; return; }
        if (args.Any(arg => arg is "--foreground" or "--console"))
        {
            Environment.SetEnvironmentVariable("QUASAR_MODE", "Console");
            Environment.SetEnvironmentVariable("QUASAR_OPEN_BROWSER_ON_START", "true");
        }
        args = args.Where(arg => arg is not ("serve" or "ensure-running" or "--service" or "--quiet" or "--foreground" or "--console")).ToArray();
        QuasarDiagnosticLoggerProvider? diagnosticCapture = null;
        try
        {
            string[] workerArgs = NormalizeWorkerArguments(args);
            var builder = WebApplication.CreateBuilder(workerArgs);
            if (OperatingSystem.IsWindows() && QuasarServiceInstallation.Current is { } serviceInstallation)
                {
                builder.Services.AddWindowsService(options => options.ServiceName = serviceInstallation.MainService);
                builder.Services.Configure<Microsoft.Extensions.Logging.EventLog.EventLogSettings>(options => { if (OperatingSystem.IsWindows()) options.SourceName = serviceInstallation.MainService; });
                }
            Quasar.ClusterDeployment.ClusterContentStore.RootDirectory = MagnetarPaths.GetQuasarManagedRuntimeToolsDirectory();
            AddDeploymentConfigurationSources(builder.Configuration, builder.Environment.EnvironmentName, workerArgs);
            MachineEndpointSecurity.DisableAutomaticForwarding(builder.Configuration);

            var webServiceOptions = WebServiceOptions.Create(builder.Configuration);
            if (!webServiceOptions.Headless && ShouldUseSourceStaticWebAssets())
                builder.WebHost.UseStaticWebAssets();

            var managedRuntimeOptions = ManagedRuntimeOptions.Create(builder.Configuration);
            var updateOptions = QuasarUpdateOptions.Create(builder.Configuration);
            var authOptions = QuasarAuthOptions.Create(builder.Configuration);
            PublicAccessSecurity.ValidateStartup(
                authOptions,
                webServiceOptions,
                Environment.GetEnvironmentVariable("ASPNETCORE_URLS"));
            var machinePolicy = MachineEndpointSecurity.Create(builder.Configuration, webServiceOptions);
            var analyticsStoreOptions = AnalyticsStoreOptions.Create(builder.Configuration);
            var runtimePluginCatalog = QuasarUiPluginCatalog.Create(
                builder.Configuration, builder.Environment, loadPluginAssemblies: !webServiceOptions.Headless);
            QuasarUiPluginCatalog? uiPluginCatalog = webServiceOptions.Headless ? null : runtimePluginCatalog;

            QuasarLoggingConfigurator.Configure(builder, webServiceOptions);

            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ASPNETCORE_URLS")))
            {
                if (ShouldListenOnAnyInterface(webServiceOptions.Host))
                    builder.WebHost.ConfigureKestrel(options => options.ListenAnyIP(webServiceOptions.Port));
                else
                    builder.WebHost.UseUrls(webServiceOptions.ListenUrl);
            }

            if (!webServiceOptions.Headless)
            {
                builder.Services.AddRazorComponents()
                    .AddInteractiveServerComponents();
                builder.Services.AddCascadingAuthenticationState();
                builder.Services.AddMudServices(configuration =>
                {
                    configuration.SnackbarConfiguration.PositionClass = Defaults.Classes.Position.BottomStart;
                    configuration.SnackbarConfiguration.PreventDuplicates = true;
                    configuration.SnackbarConfiguration.NewestOnTop = true;
                });
            }
            builder.Services.Configure<HostOptions>(options =>
            {
                options.ShutdownTimeout = TimeSpan.FromMinutes(30);
            });
            builder.Services.AddAuthentication(options =>
                {
                    options.DefaultScheme = QuasarAuthSchemes.Cookie;
                    options.DefaultChallengeScheme = QuasarAuthSchemes.Cookie;
                })
                .AddCookie(QuasarAuthSchemes.Cookie, options =>
                {
                    options.LoginPath = "/login";
                    options.LogoutPath = "/logout";
                    options.AccessDeniedPath = "/access-denied";
                    options.Cookie.Name = "Quasar.Auth";
                    options.CookieManager = new HostedPanelCookieManager();
                    options.Cookie.HttpOnly = true;
                    options.Cookie.SameSite = SameSiteMode.Lax;
                    options.Cookie.SecurePolicy = authOptions.RequireHttpsForPublicAccess
                        ? CookieSecurePolicy.Always
                        : CookieSecurePolicy.SameAsRequest;
                    options.SlidingExpiration = true;
                    options.ExpireTimeSpan = TimeSpan.FromHours(12);
                    options.Events.OnRedirectToLogin = context =>
                        context.Request.Path.StartsWithSegments("/_quasar/sdk")
                            ? QuasarSdkEndpoints.WriteAuthorizationErrorAsync(context.HttpContext, 401)
                            : IsClusterApi(context.Request.Path)
                            ? ClusterApi.WriteAuthorizationErrorAsync(context.HttpContext, 401,
                                "authentication_required", "A valid Quasar credential is required.")
                            : Redirect(context.Response, context.RedirectUri);
                    options.Events.OnRedirectToAccessDenied = context =>
                        context.Request.Path.StartsWithSegments("/_quasar/sdk")
                            ? QuasarSdkEndpoints.WriteAuthorizationErrorAsync(context.HttpContext, 403)
                            : IsClusterApi(context.Request.Path)
                            ? ClusterApi.WriteAuthorizationErrorAsync(context.HttpContext, 403,
                                "scope_forbidden", "The credential cannot access this cluster operation.")
                            : Redirect(context.Response, context.RedirectUri);
                    options.Events.OnValidatePrincipal = context =>
                    {
                        if (context.Principal is null)
                            return Task.CompletedTask;

                        var roleMapper = context.HttpContext.RequestServices.GetRequiredService<QuasarRoleMapper>();
                        var refreshed = roleMapper.RefreshRoles(context.Principal);
                        if (!HaveSameRoles(context.Principal, refreshed))
                        {
                            context.ReplacePrincipal(refreshed);
                            context.ShouldRenew = true;
                        }

                        return Task.CompletedTask;
                    };
                })
                .AddSteam(options =>
                {
                    options.SignInScheme = QuasarAuthSchemes.Cookie;
                    options.CorrelationCookie.SameSite = SameSiteMode.Lax;
                    options.CorrelationCookie.SecurePolicy = authOptions.RequireHttpsForPublicAccess
                        ? CookieSecurePolicy.Always
                        : CookieSecurePolicy.SameAsRequest;

                    options.Events.OnAuthenticated = async context =>
                    {
                        var roleMapper = context.HttpContext.RequestServices.GetRequiredService<QuasarRoleMapper>();
                        var identity = context.Identity;
                        if (identity is null)
                        {
                            context.Ticket = null!;
                            return;
                        }

                        var steamId = ExtractSteamId(context.Identifier) ??
                                      ExtractSteamId(new ClaimsPrincipal(identity));
                        if (steamId is null)
                        {
                            context.Ticket = null!;
                            return;
                        }

                        if (!roleMapper.IsSteamIdAllowed(steamId))
                        {
                            context.Ticket = null!;
                            return;
                        }

                        AddOrReplaceClaim(identity, ClaimTypes.NameIdentifier, steamId);
                        AddOrReplaceClaim(identity, ClaimTypes.Name, steamId);
                        AddOrReplaceClaim(identity, QuasarClaimTypes.Provider, QuasarAuthSchemes.Steam);
                        AddOrReplaceClaim(identity, QuasarClaimTypes.SteamId, steamId);
                        AddOrReplaceClaim(identity, QuasarClaimTypes.SteamProfileUrl, $"https://steamcommunity.com/profiles/{steamId}");

                        var profileClient = context.HttpContext.RequestServices.GetRequiredService<SteamProfileClient>();
                        var displayName = await profileClient.GetDisplayNameAsync(steamId, context.HttpContext.RequestAborted);
                        if (!string.IsNullOrWhiteSpace(displayName))
                            AddOrReplaceClaim(identity, QuasarClaimTypes.SteamDisplayName, displayName);

                        foreach (var role in roleMapper.GetSteamRoles(steamId))
                            identity.AddClaim(new Claim(ClaimTypes.Role, role));
                    };
                });
            builder.Services.AddAuthorization(ConfigureAuthorizationPolicies);
            builder.Services.AddSingleton<IAuthorizationPolicyProvider, QuasarAuthorizationPolicyProvider>();
            builder.Services.AddScoped<QuasarOperationService>();
            builder.Services.AddSingleton<QuasarTransferRegistry>();
            builder.Services.AddSingleton<QuasarBrowserSessions>();
            builder.Services.AddQuasarRtc(builder.Configuration);
            builder.Services.AddScoped<QuasarHttpOperations>();
            builder.Services.AddScoped<Quasar.Plugin.Abstractions.Operations.IQuasarHttpOperations>(services => services.GetRequiredService<QuasarHttpOperations>());
            builder.Services.AddScoped<Quasar.Plugin.Abstractions.Operations.IQuasarOperationDispatcher>(services =>
                services.GetRequiredService<QuasarOperationService>());
            builder.Services.AddQuasarCoreOperations();
            var hostedState = HostedPanelState.Read() ?? new HostedPanelState();
            builder.Services.AddAntiforgery(options => options.Cookie.Name = ".Quasar.Csrf." + hostedState.LocalProxySecret[..16]);
            builder.Services.AddHttpClient("Nebula", http => { http.Timeout = TimeSpan.FromSeconds(15); http.MaxResponseContentBufferSize = 64 * 1024; })
                .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
            builder.Services.AddSingleton<HostedPanelService>();
            builder.Services.AddHostedService(provider => provider.GetRequiredService<HostedPanelService>());
            var dataProtectionKeyringDirectory = MagnetarPaths.GetQuasarDataProtectionKeyringDirectory();
            Directory.CreateDirectory(dataProtectionKeyringDirectory);
            builder.Services.AddDataProtection()
                .SetApplicationName("Quasar")
                .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionKeyringDirectory));
            builder.Services.AddHttpClient(string.Empty)
                .AddHttpMessageHandler(serviceProvider =>
                {
                    var logger = serviceProvider.GetRequiredService<ILogger<GitHubRetryHandler>>();
                    return new GitHubRetryHandler((message, exception) =>
                        logger.LogWarning(exception, "{Message}", message));
                });
            builder.Services.AddHttpClient<SteamProfileClient>(client =>
                {
                    client.Timeout = TimeSpan.FromSeconds(5);
                    client.MaxResponseContentBufferSize = 128 * 1024;
                })
                .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
            builder.Services.AddHttpClient<ClusterGatewayClient>(client =>
                client.Timeout = Timeout.InfiniteTimeSpan);
            builder.Services.AddHttpClient<ClusterHostClient>(client =>
                client.Timeout = Timeout.InfiniteTimeSpan)
                .ConfigurePrimaryHttpMessageHandler(services => new SocketsHttpHandler
                { ConnectCallback = services.GetRequiredService<ClusterHostTunnels>().OpenAsync, UseProxy = false,
                    AllowAutoRedirect = false, MaxConnectionsPerServer = 8, PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1) });
            builder.Services.AddSingleton<ClusterCredentialStore>();
            builder.Services.AddSingleton<ClusterHostCatalog>();
            builder.Services.AddSingleton<ClusterHostTunnels>();
            builder.Services.AddSingleton<ClusterHostInstaller>();
            builder.Services.AddSingleton<ClusterLocalHost>();
            builder.Services.AddHostedService(provider => provider.GetRequiredService<ClusterLocalHost>());
            builder.Services.AddSingleton<ClusterSetupService>();
            builder.Services.AddSingleton<ClusterUpdatePreparationService>();
            builder.Services.AddSingleton(webServiceOptions);
            builder.Services.AddSingleton(managedRuntimeOptions);
            builder.Services.AddSingleton(updateOptions);
            builder.Services.AddSingleton(authOptions);
            builder.Services.AddSingleton<DataHandlingConsentCatalog>();
            builder.Services.AddSingleton<ServerListSettingsCatalog>();
            builder.Services.AddHttpClient("ServerList", client => client.Timeout = TimeSpan.FromSeconds(15))
                .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
            builder.Services.AddHostedService<ServerListPublisher>();
            builder.Services.AddHttpClient("DiagnosticsBackoffice", client => client.Timeout = TimeSpan.FromMinutes(10))
                .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
            builder.Services.AddSingleton(_ => Quasar.Diagnostics.DiagnosticStorage.Create(
                Path.Combine(MagnetarPaths.GetQuasarDirectory(), "Diagnostics"), "quasar-local",
                Environment.GetEnvironmentVariable("QUASAR_DIAGNOSTICS_DIRECTORY")
                    ?? builder.Configuration["Quasar:Diagnostics:StorageDirectory"]));
            builder.Services.AddSingleton(services => diagnosticCapture = new QuasarDiagnosticLoggerProvider(
                services.GetRequiredService<Quasar.Diagnostics.DiagnosticCollector>()));
            builder.Services.AddSingleton<ILoggerProvider>(services => services.GetRequiredService<QuasarDiagnosticLoggerProvider>());
            builder.Logging.AddFilter<QuasarDiagnosticLoggerProvider>(null, Microsoft.Extensions.Logging.LogLevel.Error);
            builder.Services.AddSingleton<DiagnosticsUplinkService>();
            builder.Services.AddHostedService(services => services.GetRequiredService<DiagnosticsUplinkService>());
            builder.Services.AddSingleton<ClusterBetaNoticeCatalog>();
            builder.Services.AddSingleton<RbacConfigCatalog>();
            builder.Services.AddSingleton(analyticsStoreOptions);
            builder.Services.AddSingleton<QuasarRoleMapper>();
            builder.Services.AddScoped<QuasarPermissionService>();
            builder.Services.AddSingleton<TrustedNetworkEvaluator>();
            builder.Services.AddSingleton<ServicePrincipalAuthenticator>();
            builder.Services.AddSingleton<QuasarAuthSettingsService>();
            builder.Services.AddSingleton<KnownPlayerCatalog>();
            builder.Services.AddSingleton<MetricsStoreService>();
            builder.Services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<MetricsStoreService>());
            builder.Services.AddSingleton<AnalyticsSeriesService>();
            builder.Services.AddSingleton<ProfilerStoreService>();
            builder.Services.AddSingleton<PluginStatsStoreService>();
            builder.Services.AddSingleton<AgentRegistry>();
            builder.Services.AddSingleton<EntityService>();
            builder.Services.AddSingleton<IQuasarCompanionChannel, QuasarCompanionChannel>();
            builder.Services.AddSingleton<QuasarConfigProfileCatalog>();
            builder.Services.AddSingleton<QuasarDevFolderCatalog>();
            builder.Services.AddSingleton<QuasarPluginHubCatalog>();
            builder.Services.AddSingleton<QuasarWorldTemplateCatalog>();
            builder.Services.AddSingleton<QuasarPluginCatalogService>();
            builder.Services.AddSingleton(runtimePluginCatalog);
            builder.Services.AddSingleton<PluginCatalogRefreshService>();
            builder.Services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<PluginCatalogRefreshService>());
            builder.Services.AddSingleton<SteamWorkshopCredentialsCatalog>();
            builder.Services.AddSingleton<GitHubUpdateCredentialsCatalog>();
            builder.Services.AddSingleton<QuasarWorkshopModResolver>();
            builder.Services.AddSingleton<ClusterWorkshopModStager>();
            builder.Services.AddSingleton<ManagedDedicatedServerRuntimeResolver>();
            builder.Services.AddSingleton<ManagedRuntimeWarmupService>();
            builder.Services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<ManagedRuntimeWarmupService>());
            builder.Services.AddSingleton<DedicatedServerCatalog>();
            builder.Services.AddSingleton<ExistingServerImportService>();
            builder.Services.AddSingleton<ClusterCatalog>();
            builder.Services.AddSingleton<ClusterPlayerService>();
            builder.Services.AddHostedService(provider => provider.GetRequiredService<ClusterPlayerService>());
            builder.Services.AddSingleton<ClusterPackageService>();
            builder.Services.AddSingleton<ClusterReleaseMonitor>();
            builder.Services.AddHostedService(provider => provider.GetRequiredService<ClusterReleaseMonitor>());
            builder.Services.AddSingleton<ClusterContentMonitor>();
            builder.Services.AddHostedService(provider => provider.GetRequiredService<ClusterContentMonitor>());
            builder.Services.AddSingleton<ClusterDependencyService>();
            builder.Services.AddHostedService<ClusterStorageMaintenance>();
            builder.Services.AddSingleton<ClusterOperationStore>();
            builder.Services.AddHostedService<ClusterOperationReconciler>();
            builder.Services.AddSingleton<ClusterCommandService>();
            builder.Services.AddSingleton<ClusterDeploymentService>();
            builder.Services.AddSingleton<ClusterPurgeService>();
            builder.Services.AddSingleton<ClusterConversionService>();
            builder.Services.AddSingleton<ClusterBackupService>();
            builder.Services.AddSingleton<ClusterUpdateService>();
            builder.Services.AddHostedService(provider => provider.GetRequiredService<ClusterUpdateService>());
            builder.Services.AddSingleton<ClusterFleetService>();
            builder.Services.AddSingleton<ClusterReconciler>();
            builder.Services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<ClusterReconciler>());
            builder.Services.AddSingleton<DedicatedServerSupervisor>();
            builder.Services.AddSingleton<DedicatedServerRuntimePreparer>();
            builder.Services.AddSingleton<FileBrowserService>();
            builder.Services.AddSingleton<WebServiceState>();
            builder.Services.AddSingleton<PluginLogStream>();
            builder.Services.AddSingleton<PluginConfigService>();
            builder.Services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<PluginConfigService>());
            builder.Services.AddSingleton<AgentSocketAuthenticator>();
            builder.Services.AddSingleton<AgentSocketHandler>();
            builder.Services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<DedicatedServerSupervisor>());
            builder.Services.AddHostedService<WebServiceManifestHostedService>();
            builder.Services.AddSingleton<DiscordOptionsCatalog>();
            builder.Services.AddSingleton<DiscordRateLimiter>();
            builder.Services.AddSingleton<DeathMessagesCatalog>();
            builder.Services.AddSingleton<DiscordClusterBridge>();
            builder.Services.AddSingleton<DiscordCommandDispatcher>();
            builder.Services.AddSingleton<DiscordCommandRouter>();
            builder.Services.AddSingleton<DiscordChatRelayService>();
            builder.Services.AddSingleton<DiscordDeathRelayService>();
            builder.Services.AddSingleton<DiscordStatusRelayService>();
            builder.Services.AddSingleton<DiscordSimSpeedAlertService>();
            builder.Services.AddSingleton<DiscordLogRelayService>();
            builder.Services.AddSingleton<DiscordAnalyticsExportService>();
            builder.Services.AddSingleton<DiscordBotService>();
            builder.Services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<DiscordBotService>());
            builder.Services.AddSingleton<QuasarShutdownService>();
            builder.Services.AddSingleton<QuasarUpdateService>();
            builder.Services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<QuasarUpdateService>());
            ConfigureNotificationServices(builder.Services, webServiceOptions);
            builder.Services.AddSingleton<QuasarBackupSettingsService>();
            builder.Services.AddSingleton<ServerRestoreCoordinator>();
            builder.Services.AddSingleton<QuasarBackupService>();
            builder.Services.AddSingleton<AutomaticBackupService>();
            builder.Services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<AutomaticBackupService>());
            if (uiPluginCatalog != null)
            {
                builder.Services.AddScoped<BrowserStorageService>();
                builder.Services.AddScoped<WorldTemplateImportLocationService>();
                builder.Services.AddScoped<ServerManagementActions>();
                builder.Services.AddSingleton<QuasarUiPluginStateStore>();
                builder.Services.AddSingleton<QuasarManagedDotNetSdkService>();
                builder.Services.AddSingleton<QuasarUiPluginHubCatalogService>();
                builder.Services.AddSingleton<QuasarUiPluginHubRefreshService>();
                builder.Services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<QuasarUiPluginHubRefreshService>());
                builder.Services.AddSingleton<BrandingService>();
                builder.Services.AddScoped<ThemePreferenceService>();
                uiPluginCatalog.ConfigurePluginServices(builder.Services);
            }

            var app = builder.Build();
            app.Lifetime.ApplicationStopping.Register(app.Services.GetRequiredService<QuasarDiagnosticLoggerProvider>().MarkExpectedStop);
            StopWebServerOnStopping(app);
            _ = app.Services.GetRequiredService<RbacConfigCatalog>();
            using (var sdkScope = app.Services.CreateScope())
                _ = sdkScope.ServiceProvider.GetRequiredService<QuasarOperationService>();

            if (!webServiceOptions.Headless && !app.Environment.IsDevelopment())
                app.UseExceptionHandler("/Error");

            app.Use((context, next) => HostedPanelRequest.InvokeAsync(context, next, authOptions));
            app.Use((context, next) => MachineEndpointSecurity.InvokeAsync(context, next, machinePolicy));
            var forwarding = CreateForwardedHeadersOptions(authOptions);
            app.Use((context, next) =>
            {
                context.RequestServices.GetRequiredService<TrustedNetworkEvaluator>()
                    .CaptureOriginalRequest(context, forwarding);
                return next(context);
            });
            app.UseForwardedHeaders(forwarding);
            app.Use((context, next) =>
            {
                if (HostedPanelRequest.IsHosted(context))
                {
                    context.Request.Scheme = "https";
                    context.Request.Host = new HostString("dash.se-servers.net");
                }
                return next(context);
            });
            app.Use((context, next) => PublicAccessSecurity.InvokeAsync(context, next, authOptions));
            if (!webServiceOptions.Headless)
                app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
            app.UseRouting();
            app.UseWebSockets(new WebSocketOptions
            {
                KeepAliveInterval = TimeSpan.FromSeconds(30),
            });
            app.UseAuthentication();
            app.Use(async (context, next) =>
            {
                var servicePrincipals = context.RequestServices.GetRequiredService<ServicePrincipalAuthenticator>();
                bool hasBearerAuthorization = servicePrincipals.HasBearerAuthorization(context.Request);
                if (authOptions.Enabled && hasBearerAuthorization)
                    servicePrincipals.TryAuthenticate(context);
                if (authOptions.Enabled &&
                    !HostedPanelRequest.IsHosted(context) &&
                    !hasBearerAuthorization &&
                    context.User.Identity?.IsAuthenticated != true &&
                    context.RequestServices.GetRequiredService<TrustedNetworkEvaluator>().IsTrusted(context))
                {
                    context.User = context.RequestServices.GetRequiredService<QuasarRoleMapper>()
                        .CreateTrustedNetworkPrincipal();
                }

                await next(context);
            });
            app.UseAuthorization();
            if (!webServiceOptions.Headless)
                app.UseAntiforgery();

            app.MapWebServiceProbes(authOptions);
            app.MapQuasarSdk();
            app.MapQuasarRtc();
            app.MapClusterApi(authOptions);
            app.MapClusterHostEnrollmentApi();

            app.MapQuasarCoreOperations();

            // Chartable plugin-statistics metrics discovered at runtime from what plugins have
            // published (provider/group/field), so the Analytics page can offer them as panels.
            var pluginStatsCatalog = app.MapGet("/api/analytics/plugin-stats/catalog", (HttpContext context, PluginStatsStoreService store) =>
            {
                var servers = context.Request.Query["servers"].Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!).ToArray();
                return Results.Json(store.GetCatalog(servers));
            });
            if (authOptions.Enabled)
                pluginStatsCatalog.RequireAuthorization(QuasarPolicyNames.CanView);

            // Generates a fresh configuration backup and streams it as a download.
            var backupDownload = app.MapGet("/api/backup/download", (QuasarBackupService backup) =>
            {
                var archive = backup.CreateBackup(DateTimeOffset.Now);
                return Results.File(archive.Content, "application/zip", archive.FileName);
            });

            // Downloads an existing backup ZIP from the configured backup directory by file name.
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

            if (!webServiceOptions.Headless)
                MapAuthenticationEndpoints(app, authOptions);

            app.MapGet("/api/internal/update-health", (HttpContext context, WebServiceOptions options) =>
            {
                if (!QuasarApplicationEntry.IsUpdaterAuthorized(context, options.LauncherToken)) return Results.Unauthorized();
                return Results.Json(new { pid = Environment.ProcessId, version = options.Version,
                    started = Process.GetCurrentProcess().StartTime.ToUniversalTime() });
            }).AllowAnonymous();

            app.MapPost("/api/internal/drain", async (HttpContext context, DedicatedServerSupervisor supervisor, QuasarShutdownService shutdownService, IHostApplicationLifetime lifetime) =>
            {
                var expectedToken = context.RequestServices.GetRequiredService<WebServiceOptions>().LauncherToken;
                if (string.IsNullOrWhiteSpace(expectedToken) ||
                    !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                        System.Text.Encoding.UTF8.GetBytes(context.Request.Headers["X-Quasar-Launcher-Token"].ToString()),
                        System.Text.Encoding.UTF8.GetBytes(expectedToken)))
                {
                    return Results.Unauthorized();
                }

                var delaySeconds = 0;
                if (int.TryParse(context.Request.Query["delaySeconds"], out var parsedDelay))
                    delaySeconds = Math.Max(0, parsedDelay);

                var stopServers = bool.TryParse(context.Request.Query["stopServers"], out var parsedStopServers) && parsedStopServers;
                if (!stopServers)
                    await supervisor.BeginLauncherDrainAsync();

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

            if (uiPluginCatalog != null)
            {
                app.MapStaticAssets();

                // Runtime-uploaded branding assets live in the Quasar install root
                // so web-service updates do not replace custom logos or favicons.
                var brandingAssetsDirectory = MagnetarPaths.GetQuasarBrandingDirectory();
                Directory.CreateDirectory(brandingAssetsDirectory);
                app.UseStaticFiles(new StaticFileOptions
                {
                    FileProvider = new PhysicalFileProvider(brandingAssetsDirectory),
                    RequestPath = "/branding",
                });
                uiPluginCatalog.UsePluginStaticAssets(app);
                uiPluginCatalog.ConfigurePluginEndpoints(app);

                var razorComponents = app.MapRazorComponents<App>()
                    .AddInteractiveServerRenderMode()
                    .AddAdditionalAssemblies(uiPluginCatalog.RazorAssemblies.ToArray());
                if (authOptions.Enabled)
                    razorComponents.RequireAuthorization(QuasarPolicyNames.CanView);
            }
            else
            {
                app.MapGet("/", (WebServiceOptions options) => Results.Json(new
                {
                    service = WebServiceOptions.SupervisorName,
                    options.Version,
                    mode = "headless",
                    api = "/api",
                    health = "/api/health",
                    readiness = "/api/ready",
                }));
            }

            var startupLogger = app.Services.GetRequiredService<ILogger<Program>>();
            if (uiPluginCatalog?.SafeMode == true)
            {
                startupLogger.LogWarning(
                    "Quasar UI plugin safe mode is enabled. Reasons={Reasons}",
                    string.Join("; ", uiPluginCatalog.SafeModeReasons));
            }
            else if (uiPluginCatalog != null)
            {
                startupLogger.LogInformation(
                    "Quasar UI plugin catalog initialized. PluginCount={PluginCount}.",
                    uiPluginCatalog.LoadedPlugins.Count);
            }

            if (uiPluginCatalog != null)
                foreach (var loadError in uiPluginCatalog.LoadErrors)
                    startupLogger.LogWarning("Quasar UI plugin load failed: {Error}", loadError);

            startupLogger.LogInformation(
                "Quasar {Version} starting. BootstrapVersion={BootstrapVersion}; HostId={HostId}; Headless={Headless}; InstallRoot={InstallRoot}.",
                webServiceOptions.Version,
                string.IsNullOrWhiteSpace(webServiceOptions.BootstrapVersion) ? "none" : webServiceOptions.BootstrapVersion,
                webServiceOptions.HostId,
                webServiceOptions.Headless,
                MagnetarPaths.GetQuasarDirectory());

            foreach (var warning in Quasar.Diagnostics.SystemLimits.Check())
                startupLogger.LogWarning("{Warning}", warning);

            app.Lifetime.ApplicationStarted.Register(() => QuasarApplicationEntry.ConfirmManualUpdate(webServiceOptions.Version));
            using var gracefulShutdownSignals = RegisterGracefulShutdownSignals(app.Services);
            NativeServiceFailures.Observe(app);
            StartHostedConnector(app, webServiceOptions, startupLogger);
            app.Run();
        }
        catch (Exception exception)
        {
            diagnosticCapture?.CaptureFatal(exception);
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

    private static void StartHostedConnector(WebApplication app, WebServiceOptions options, Microsoft.Extensions.Logging.ILogger logger)
    {
        var connector = new Quasar.Bootstrap.HostedPanelConnector(options.Port, logger);
        connector.Start();
        app.Lifetime.ApplicationStopped.Register(() => connector.DisposeAsync().AsTask().GetAwaiter().GetResult());
    }

    internal static void ConfigureNotificationServices(IServiceCollection services, WebServiceOptions options)
    {
        services.AddHttpClient("Announcements", client => client.Timeout = TimeSpan.FromSeconds(30))
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
        services.AddSingleton<AnnouncementService>();
        services.AddHostedService(provider => provider.GetRequiredService<AnnouncementService>());
        if (options.Headless) return;
        services.AddSingleton<PushNotificationService>();
        services.AddScoped<BrowserPushState>();
        services.AddHostedService(provider => provider.GetRequiredService<PushNotificationService>());
    }

    internal static void MapAuthenticationEndpoints(WebApplication app, QuasarAuthOptions authOptions)
    {
        app.MapGet("/login", (HttpContext context) =>
        {
            if (!authOptions.Enabled)
                return Results.Redirect(context.Request.PathBase + "/");

            var forceSteam = bool.TryParse(context.Request.Query["forceSteam"], out var parsedForceSteam) && parsedForceSteam;
            if (context.User.Identity?.IsAuthenticated == true && !forceSteam)
                return Results.Redirect(HostedReturnUrl(context));

            if (!authOptions.Steam.Enabled ||
                !string.Equals(authOptions.DefaultProvider, QuasarAuthSchemes.Steam, StringComparison.OrdinalIgnoreCase))
            {
                return Results.Content(CreateLoginUnavailableHtml(), "text/html");
            }

            return Results.Challenge(new AuthenticationProperties
            {
                RedirectUri = HostedReturnUrl(context),
                IsPersistent = true,
            }, [SteamAuthenticationDefaults.AuthenticationScheme]);
        }).AllowAnonymous();

        app.MapPost("/logout", async (HttpContext context, IAntiforgery antiforgery) =>
        {
            try
            {
                await antiforgery.ValidateRequestAsync(context);
            }
            catch (AntiforgeryValidationException)
            {
                return Results.BadRequest();
            }

            if (context.User.Identity?.IsAuthenticated == true && context.RequestServices.GetService<QuasarBrowserSessions>() is { } sessions)
                await sessions.RevokeAsync(context);
            await context.SignOutAsync(QuasarAuthSchemes.Cookie);
            return Results.Redirect(context.Request.PathBase + "/");
        }).AllowAnonymous();

        app.MapGet("/access-denied", (HttpContext context, IAntiforgery antiforgery) =>
        {
            var tokens = antiforgery.GetAndStoreTokens(context);
            return Results.Content(
                CreateAccessDeniedHtml(tokens.FormFieldName, tokens.RequestToken, context.Request.PathBase.Value),
                "text/html",
                statusCode: StatusCodes.Status403Forbidden);
        }).AllowAnonymous();
    }

    internal static readonly TimeSpan WebServerDrainTimeout = TimeSpan.FromSeconds(5);

    // SignalR closes the Blazor circuits when the application stops, but an open browser tab reconnects at
    // once. Kestrel would wait for that new connection up to the 30 minute ShutdownTimeout meant for stopping
    // servers, so Quasar would not exit while its UI is open. Stop the web server here instead: it stops
    // listening and aborts the connections still open after a short drain. The host's own stop of the
    // server later waits for this one.
    internal static void StopWebServerOnStopping(WebApplication app) =>
        app.Lifetime.ApplicationStopping.Register(() => _ = app.Services.GetRequiredService<IServer>()
            .StopAsync(new CancellationTokenSource(WebServerDrainTimeout).Token));

    internal static string[] NormalizeWorkerArguments(IEnumerable<string> args) => args.Select(argument =>
    {
        if (string.Equals(argument, "--headless", StringComparison.OrdinalIgnoreCase))
            return "--Quasar:Headless=true";
        if (string.Equals(argument, "--no-headless", StringComparison.OrdinalIgnoreCase))
            return "--Quasar:Headless=false";
        const string prefix = "--headless=";
        return argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? "--Quasar:Headless=" + argument[prefix.Length..]
            : argument;
    }).ToArray();

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
        // No reloadOnChange: settings are read once at startup anyway, and the reload watch covers
        // the file's whole folder tree, which for the data directory holds every server (SE1-0195).
        foreach (var directory in GetConfigurationDirectories(AppContext.BaseDirectory, Directory.GetCurrentDirectory(),
                     MagnetarPaths.GetQuasarDirectory()))
        {
            configuration.AddJsonFile(Path.Combine(directory, "appsettings.json"), optional: true, reloadOnChange: false);
            configuration.AddJsonFile(Path.Combine(directory, $"appsettings.{environmentName}.json"), optional: true, reloadOnChange: false);
        }

        configuration.AddEnvironmentVariables();
        if (args.Length > 0)
            configuration.AddCommandLine(args);
    }

    // Folders whose appsettings files the worker reads, lowest priority first. The data directory always
    // comes last, so its appsettings.json wins: Bootstrap reads only that file and health-checks its port.
    internal static IReadOnlyList<string> GetConfigurationDirectories(string baseDirectory, string currentDirectory, string dataDirectory)
    {
        var directories = new List<string> { baseDirectory, currentDirectory, Path.Combine(baseDirectory, "WebService") };
        if (FindSourceProjectDirectory(baseDirectory) is { } project)
            directories.Add(project);
        directories.Add(dataDirectory);
        return directories
            .Select(directory => Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)))
            .Reverse().Distinct(StringComparer.OrdinalIgnoreCase).Reverse().ToArray();
    }

    // A development run from bin/ or a publish folder inside the source tree also reads the project's
    // appsettings.json. Nothing above an installed worker is read.
    private static string? FindSourceProjectDirectory(string baseDirectory)
    {
        var directory = new DirectoryInfo(baseDirectory);
        for (var depth = 0; directory is not null && depth < 8; depth++, directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Quasar.csproj")))
                return directory.FullName;
            if (File.Exists(Path.Combine(directory.FullName, "Quasar", "Quasar.csproj")))
                return Path.Combine(directory.FullName, "Quasar");
        }

        return null;
    }

    private static IDisposable RegisterGracefulShutdownSignals(IServiceProvider services)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return EmptyDisposable.Shared;

        var shutdownService = services.GetRequiredService<QuasarShutdownService>();
        var lifetime = services.GetRequiredService<IHostApplicationLifetime>();
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
                    if (services.GetRequiredService<DedicatedServerSupervisor>().PreserveServersOnShutdown)
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

    internal static void ConfigureAuthorizationPolicies(AuthorizationOptions options)
    {
        options.AddPolicy(QuasarPolicyNames.ClusterQuery, policy => policy.RequireAssertion(context =>
            context.User.Identity?.IsAuthenticated == true && (context.User.IsInRole(QuasarRoles.Viewer)
            || context.User.IsInRole(QuasarRoles.Editor)
            || context.User.IsInRole(QuasarRoles.Admin)
            || context.User.HasClaim(QuasarClaimTypes.Scope, QuasarScopes.ClusterQuery)
            || context.User.HasClaim(QuasarClaimTypes.Scope, QuasarScopes.ClusterManage))));
        options.AddPolicy(QuasarPolicyNames.ClusterManage, policy => policy.RequireAssertion(context =>
            context.User.Identity?.IsAuthenticated == true && (context.User.IsInRole(QuasarRoles.Editor)
            || context.User.IsInRole(QuasarRoles.Admin)
            || context.User.HasClaim(QuasarClaimTypes.Scope, QuasarScopes.ClusterManage))));
        AddRolePolicy(options, QuasarPolicyNames.CanView, QuasarRoles.Viewer, QuasarRoles.Editor, QuasarRoles.Admin);
        AddRolePolicy(options, QuasarPolicyNames.CanEditConfigs, QuasarRoles.Editor, QuasarRoles.Admin);
        AddRolePolicy(options, QuasarPolicyNames.CanEditServers, QuasarRoles.Editor, QuasarRoles.Admin);
        AddRolePolicy(options, QuasarPolicyNames.CanControlServers, QuasarRoles.Editor, QuasarRoles.Admin);
        AddRolePolicy(options, QuasarPolicyNames.CanManageDiscord, QuasarRoles.Editor, QuasarRoles.Admin);
        AddRolePolicy(options, QuasarPolicyNames.CanManageAppearance, QuasarRoles.Editor, QuasarRoles.Admin);
        AddRolePolicy(options, QuasarPolicyNames.CanManageSecurity, QuasarRoles.Admin);
        options.AddPolicy(QuasarPolicyNames.ClusterMaintenance, policy => policy.RequireAuthenticatedUser().RequireRole(QuasarRoles.Admin));
        AddRolePolicy(options, QuasarPolicyNames.CanShutdownQuasar, QuasarRoles.Admin);
    }

    private static void AddRolePolicy(AuthorizationOptions options, string policyName, params string[] roles)
    {
        options.AddPolicy(policyName, policy => policy.RequireRole(roles));
    }

    private static bool HaveSameRoles(ClaimsPrincipal left, ClaimsPrincipal right)
    {
        static HashSet<string> Roles(ClaimsPrincipal principal) => principal.Claims
            .Where(claim => claim.Type == ClaimTypes.Role)
            .Select(claim => claim.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return Roles(left).SetEquals(Roles(right));
    }

    private static string HostedReturnUrl(HttpContext context)
    {
        var path = SanitizeReturnUrl(context.Request.Query["returnUrl"]);
        var prefix = context.Request.PathBase.Value ?? "";
        return prefix.Length > 0 && !new PathString(path.Split('?')[0]).StartsWithSegments(prefix) ? prefix + path : path;
    }

    private static bool IsClusterApi(PathString path) => path.StartsWithSegments("/api/v1/clusters");

    private static Task Redirect(HttpResponse response, string location)
    {
        response.Redirect(location);
        return Task.CompletedTask;
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

    private static string CreateAccessDeniedHtml(string antiforgeryFieldName, string? antiforgeryToken, string? pathBase = null)
    {
        var prefix = WebUtility.HtmlEncode(pathBase ?? string.Empty);
        var field = WebUtility.HtmlEncode(antiforgeryFieldName);
        var token = WebUtility.HtmlEncode(antiforgeryToken ?? string.Empty);
        return $$"""
        <!doctype html>
        <html lang="en">
        <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>Access denied - Quasar</title>
            <link rel="icon" type="image/png" href="{{prefix}}/Quasar.png">
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
                    <img src="{{prefix}}/Quasar.png" alt="Quasar logo">
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
                        <form method="post" action="{{prefix}}/logout">
                            <input type="hidden" name="{{field}}" value="{{token}}">
                            <button class="button button-primary" type="submit">Sign out</button>
                        </form>
                        <a class="button button-secondary" href="{{prefix}}/login?forceSteam=true">Retry Steam sign-in</a>
                    </div>
                </section>
            </main>
        </body>
        </html>
        """;
    }
}
