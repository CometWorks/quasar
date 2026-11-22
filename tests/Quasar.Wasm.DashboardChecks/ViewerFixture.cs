using System.Text.Json;
using Magnetar.Protocol.Model;
using Magnetar.Protocol.Transport;
using Quasar.Services;
using Quasar.Services.Plugins;

internal static class ViewerFixture
{
    internal static QuasarUiPluginCatalog CreateCatalog(WebApplicationBuilder builder)
    {
        var path = Environment.GetEnvironmentVariable("QUASAR_VIEWER_PACKAGE");
        var fixture = Path.GetFullPath("../../../../Quasar.Wasm.PluginChecks/RouteAdapter", AppContext.BaseDirectory);
        var extra = Environment.GetEnvironmentVariable("QUASAR_TEST_PLUGIN_PACKAGE");
        var driver = Environment.GetEnvironmentVariable("QUASAR_TEST_PLUGIN_DRIVER");
        if (string.IsNullOrWhiteSpace(extra) != string.IsNullOrWhiteSpace(driver))
            throw new ArgumentException("Set QUASAR_TEST_PLUGIN_PACKAGE and QUASAR_TEST_PLUGIN_DRIVER together.");
        if (!string.IsNullOrWhiteSpace(extra) && !File.Exists(Path.Combine(extra, "quasar-plugin.json")))
            throw new FileNotFoundException("Test plugin package manifest missing.", Path.Combine(extra, "quasar-plugin.json"));
        if (!string.IsNullOrWhiteSpace(driver) && !File.Exists(driver))
            throw new FileNotFoundException("Test plugin browser driver missing.", driver);
        var roots = new[] { fixture, Path.Combine(fixture, "../WorkerAdapter"), Path.Combine(fixture, "../CollisionAdapter"), path, extra }.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => Path.GetFullPath(p!));
        builder.Configuration["Quasar:Plugins:Directory"] = string.Join(Path.PathSeparator, roots);
        builder.Configuration["Quasar:Plugins:BuildConfiguration"] = "Release";
        var catalog = QuasarUiPluginCatalog.Create(builder.Configuration, builder.Environment);
        if (!catalog.BrowserPageAssemblies.Any(a => a.PluginId == "quasar.fixture.routes") ||
            (!string.IsNullOrEmpty(path) && !catalog.Extensions.Any(e => e.PluginId == "cometworks.entityviewer")) ||
            (!string.IsNullOrWhiteSpace(extra) && !catalog.LoadedPlugins.Any(p => Path.GetFullPath(p.Context.PluginDirectory) == Path.TrimEndingDirectorySeparator(Path.GetFullPath(extra)))) ||
            catalog.RoutableAssemblies.Any(a => a.GetName().Name is "RouteAdapter" or "Quasar.Wasm.DashboardChecks"))
            throw new InvalidDataException("Unexpected dashboard fixture catalog. " + string.Join(", ", catalog.LoadErrors));
        if (catalog.LoadErrors.Count != 2 ||
            !catalog.LoadErrors.Any(e => e.StartsWith("quasar.fixture.worker: Razor UI requires a browserAssembly", StringComparison.Ordinal)) ||
            !catalog.LoadErrors.Any(e => e.StartsWith("ConflictingBrowserPages: a plugin route conflicts", StringComparison.Ordinal)) ||
            catalog.RoutableAssemblies.Any(a => a.GetName().Name == "ConflictingBrowserPages"))
            throw new InvalidDataException("Unexpected dashboard fixture diagnostics. " + string.Join(", ", catalog.LoadErrors));
        var players = new KnownPlayerCatalog(Microsoft.Extensions.Logging.Abstractions.NullLogger<KnownPlayerCatalog>.Instance);
        builder.Services.AddSingleton(players);
        var registry = new AgentRegistry(players, null!, null!, null!);
        registry.UpsertHello(new() { AgentId = "viewer-node", UniqueName = "viewer-server" }, "viewer-connection", (message, _) =>
        {
            var command = message.Command!;
            var result = new EntityListResult { TotalCount = 1, TotalEntityCount = 1, Entities = [new()
            {
                EntityId = 9007199254740993L, OwnerSteamId = 76561198000000001UL, DisplayName = "Fixture freighter", TypeTag = "Grid", SubType = "LargeShip"
            }] };
            string payload = JsonSerializer.Serialize(result, JsonSerializerOptions.Web);
            if (command.CommandType == ServerCommandType.PluginRequest)
            {
                var request = JsonSerializer.Deserialize<CompanionPluginRequest>(command.Payload, JsonSerializerOptions.Web)!;
                var response = new CompanionPluginResponse { CorrelationId = request.CorrelationId,
                    Success = request.Operation == "get-entity-scene", PayloadJson = """{"mods":[],"entities":[]}""" };
                payload = JsonSerializer.Serialize(response, JsonSerializerOptions.Web);
            }
            registry.UpdateCommandResult(new() { CommandId = command.CommandId, AgentId = "viewer-node", Success = true,
                Payload = payload }, "viewer-connection");
            return Task.CompletedTask;
        });
        builder.Services.AddSingleton(registry);
        builder.Services.AddSingleton<Quasar.Plugin.Abstractions.Companion.IQuasarCompanionChannel, QuasarCompanionChannel>();
        builder.Services.AddSingleton(new EntityService(registry));
        builder.Services.AddSingleton<DedicatedServerCatalog>();
        builder.Services.AddSingleton<ClusterCatalog>();
        if (!string.IsNullOrEmpty(path))
        {
            // Seed a synthetic base-game model and consent only in this disposable installation.
            var install = catalog.LoadedPlugins.Single(plugin => plugin.Id == "cometworks.entityviewer").Context.InstallDirectory;
            var content = Path.Combine(install, "fixture-content", "Content");
            foreach (var part in new[] { "Models", "Data", "Textures" }) Directory.CreateDirectory(Path.Combine(content, part));
            var bytes = new byte[2 * 1024 * 1024];
            for (var i = 0; i < bytes.Length; ++i) bytes[i] = (byte)(i % 251);
            File.WriteAllBytes(Path.Combine(content, "Models", "fixture.mwm"), bytes);
            var settings = Path.Combine(install, "ManagedRuntime", "Tools", "CometWorks.EntityViewer");
            Directory.CreateDirectory(settings);
            File.WriteAllText(Path.Combine(settings, "asset-streaming-settings.json"), JsonSerializer.Serialize(new
            {
                streamingEnabled = true, consentAccepted = true, consentVersion = "server-asset-streaming-v1",
                baseGameSourceMode = "ExternalInstall", baseGameContentPath = content
            }));
        }
        var registered = builder.Services.Count;
        catalog.ConfigurePluginServices(builder.Services);
        // Exercise the installed UI and its worker APIs without polling SteamCMD or recording game replay history.
        foreach (var service in builder.Services.Skip(registered).Where(s => s.ServiceType == typeof(IHostedService)).ToArray())
            builder.Services.Remove(service);
        return catalog;
    }
}
