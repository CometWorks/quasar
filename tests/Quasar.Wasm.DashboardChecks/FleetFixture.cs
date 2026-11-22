using Microsoft.AspNetCore.Authentication;
using Quasar.Models;
using Quasar.Services;
using Quasar.Services.Analytics;
using Quasar.Services.Auth;
using Quasar.Services.Backup;

internal static class FleetFixture
{
    private const string Operator = "76561198000000012";

    internal static void Register(IServiceCollection services)
    {
        services.AddSingleton<QuasarWorldTemplateCatalog>();
        services.AddSingleton<DataHandlingConsentCatalog>();
        services.AddSingleton<ServerRestoreCoordinator>();
        services.AddSingleton<ServerWorldImportService>();
        // These services are snapshots only. The fixture never launches a server or installs a runtime.
        services.AddSingleton(sp => new ManagedDedicatedServerRuntimeResolver(sp.GetRequiredService<ILogger<ManagedDedicatedServerRuntimeResolver>>(),
            null!, new(), null!, sp.GetRequiredService<IHostApplicationLifetime>()));
        services.AddSingleton<ManagedRuntimeWarmupService>();
        services.AddSingleton(sp => new DedicatedServerSupervisor(sp.GetRequiredService<DedicatedServerCatalog>(), sp.GetRequiredService<AgentRegistry>(),
            null!, sp.GetRequiredService<ManagedDedicatedServerRuntimeResolver>(), sp.GetRequiredService<ManagedRuntimeWarmupService>(),
            sp.GetRequiredService<WebServiceOptions>(), sp.GetRequiredService<ServerRestoreCoordinator>(), sp.GetRequiredService<ILogger<DedicatedServerSupervisor>>()));
        services.AddSingleton(sp => new ClusterPlayerService(sp.GetRequiredService<ClusterCatalog>(), null!, sp.GetRequiredService<KnownPlayerCatalog>(), sp.GetRequiredService<ILogger<ClusterPlayerService>>()));
        services.AddSingleton<AnalyticsStoreOptions>();
        services.AddSingleton<MetricsStoreService>();
        services.AddSingleton<ProfilerStoreService>();
        services.AddSingleton<PluginStatsStoreService>();
        services.AddSingleton<AnalyticsSeriesService>();
    }

    internal static async Task SeedAsync(WebApplication app)
    {
        await app.Services.GetRequiredService<DataHandlingConsentCatalog>().SaveAsync(false);
        await app.Services.GetRequiredService<DedicatedServerCatalog>().UpsertAsync(new DedicatedServerDefinition
        {
            UniqueName = "fleet-sample", DisplayName = "Europa fleet fixture", ConfigProfileId = "alpha",
            WorldSaveName = "Europa Station", ServerPort = 27991
        });
        var server = app.Services.GetRequiredService<DedicatedServerCatalog>().GetServer("fleet-sample")!;
        var paths = DedicatedServerPathResolver.Resolve(server);
        Directory.CreateDirectory(paths.DedicatedServerAppDataPath);
        await File.WriteAllTextAsync(Path.Combine(paths.DedicatedServerAppDataPath, "SpaceEngineersDedicated.log"), "Fixture server log entry\n");
        Directory.CreateDirectory(paths.WorldSavePath);
        await File.WriteAllTextAsync(Path.Combine(paths.WorldSavePath, "Sandbox.sbc"), "<MyObjectBuilder_Checkpoint />");
        await app.Services.GetRequiredService<RbacConfigCatalog>().UpdateAsync(config =>
        {
            config.SubjectRoleMappings.Add(new() { Provider = QuasarAuthSchemes.Steam, Subject = Operator, Roles = [QuasarRoles.Admin] });
            return Task.CompletedTask;
        });
        app.MapGet("/fixture/fleet-login", (HttpContext http, QuasarRoleMapper mapper) =>
            http.SignInAsync("fixture", mapper.CreateSteamPrincipal(Operator)));
    }
}
