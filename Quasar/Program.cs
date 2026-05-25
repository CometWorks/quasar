using Quasar.Components;
using Quasar.Models;
using Quasar.Services;
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

            QuasarLoggingConfigurator.Configure(builder, webServiceOptions);

            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ASPNETCORE_URLS")))
                builder.WebHost.UseUrls(webServiceOptions.ListenUrl);

            builder.Services.AddRazorComponents()
                .AddInteractiveServerComponents();
            builder.Services.AddHttpClient();
            builder.Services.AddLocalStorageServices();
            builder.Services.AddMudServices(configuration =>
            {
                configuration.SnackbarConfiguration.PositionClass = Defaults.Classes.Position.BottomRight;
            });
            builder.Services.AddSingleton(webServiceOptions);
            builder.Services.AddSingleton<AgentRegistry>();
            builder.Services.AddSingleton<QuasarConfigProfileCatalog>();
            builder.Services.AddSingleton<QuasarPluginCatalogService>();
            builder.Services.AddSingleton<QuasarWorkshopModResolver>();
            builder.Services.AddSingleton<DedicatedServerInstanceCatalog>();
            builder.Services.AddSingleton<DedicatedServerSupervisor>();
            builder.Services.AddSingleton<DedicatedServerRuntimePreparer>();
            builder.Services.AddSingleton<WebServiceState>();
            builder.Services.AddSingleton<AgentSocketHandler>();
            builder.Services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<DedicatedServerSupervisor>());
            builder.Services.AddHostedService<WebServiceManifestHostedService>();
            builder.Services.AddScoped<ThemePreferenceService>();

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
                baseUrl = state.CurrentManifest.BaseUrl,
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

            app.Map("/ws/agent", async (HttpContext context, AgentSocketHandler socketHandler) =>
            {
                await socketHandler.HandleAsync(context);
            });

            app.MapStaticAssets();
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
}
