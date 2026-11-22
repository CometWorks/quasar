using System.IO.Compression;
using System.Text;
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
        services.AddSingleton<ExistingServerImportService>();
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
        const string zipName = "selective-server.zip";
        using (var archive = ZipFile.Open(Path.Combine(Environment.GetEnvironmentVariable("QUASAR_INSTALL_DIR")!, zipName), ZipArchiveMode.Create))
        {
            void Entry(string path, byte[] bytes)
            {
                using var destination = archive.CreateEntry(path, CompressionLevel.NoCompression).Open();
                destination.Write(bytes);
            }
            Entry("SpaceEngineers-Dedicated.cfg", Encoding.UTF8.GetBytes("<MyConfigDedicated><ServerName>Selective ZIP fixture</ServerName></MyConfigDedicated>"));
            Entry("Saves/Primary/Sandbox.sbc", Encoding.UTF8.GetBytes("<MyObjectBuilder_Checkpoint />"));
            Entry("Saves/Primary/Planet.vx2", new byte[256 * 1024]);
            Entry("Saves/Primary/Backup/old/Planet.vx2", new byte[2 * 1024 * 1024]);
            Entry("DedicatedServer64/engine.dll", new byte[2 * 1024 * 1024]);
        }
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
