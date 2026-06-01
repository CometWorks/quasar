using Quasar.Components;
using Quasar.Models;
using Quasar.Services;
using Quasar.Services.Analytics;
using Quasar.Services.Discord;
using Quasar.Services.PluginSdk;
using Magnetar.Protocol.Runtime;
using Microsoft.Extensions.FileProviders;
using MudBlazor;
using MudBlazor.Services;
using NLog;

namespace Quasar;

public class Program
{
    public static void Main(string[] args)
    {
        try
        {
            var builder = WebApplication.CreateBuilder(args);
            if (ShouldUseSourceStaticWebAssets())
                builder.WebHost.UseStaticWebAssets();

            var webServiceOptions = WebServiceOptions.Create(builder.Configuration);
            var managedRuntimeOptions = ManagedRuntimeOptions.Create(builder.Configuration);

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
            builder.Services.AddHttpClient();
            builder.Services.AddLocalStorageServices();
            builder.Services.AddMudServices(configuration =>
            {
                configuration.SnackbarConfiguration.PositionClass = Defaults.Classes.Position.BottomRight;
            });
            builder.Services.AddSingleton(webServiceOptions);
            builder.Services.AddSingleton(managedRuntimeOptions);
            builder.Services.AddSingleton<KnownPlayerCatalog>();
            builder.Services.AddSingleton<MetricsStoreService>();
            builder.Services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<MetricsStoreService>());
            builder.Services.AddSingleton<AgentRegistry>();
            builder.Services.AddSingleton<EntityService>();
            builder.Services.AddSingleton<QuasarConfigProfileCatalog>();
            builder.Services.AddSingleton<QuasarWorldTemplateCatalog>();
            builder.Services.AddSingleton<QuasarPluginCatalogService>();
            builder.Services.AddSingleton<QuasarWorkshopModResolver>();
            builder.Services.AddSingleton<ManagedDedicatedServerRuntimeResolver>();
            builder.Services.AddSingleton<DedicatedServerInstanceCatalog>();
            builder.Services.AddSingleton<DedicatedServerSupervisor>();
            builder.Services.AddSingleton<DedicatedServerRuntimePreparer>();
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

            app.MapPost("/api/internal/drain", (HttpContext context, DedicatedServerSupervisor supervisor, IHostApplicationLifetime lifetime) =>
            {
                var expectedToken = context.RequestServices.GetRequiredService<WebServiceOptions>().LauncherToken;
                if (string.IsNullOrWhiteSpace(expectedToken) ||
                    !string.Equals(context.Request.Headers["X-Quasar-Launcher-Token"], expectedToken, StringComparison.Ordinal))
                {
                    return Results.Unauthorized();
                }

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

            app.MapRazorComponents<App>()
                .AddInteractiveServerRenderMode();

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
}
