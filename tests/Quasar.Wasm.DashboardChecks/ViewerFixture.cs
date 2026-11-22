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
        builder.Configuration["Quasar:Plugins:Directory"] = string.IsNullOrEmpty(path) ? fixture : string.Join(Path.PathSeparator, fixture, path);
        builder.Configuration["Quasar:Plugins:BuildConfiguration"] = "Release";
        var catalog = QuasarUiPluginCatalog.Create(builder.Configuration, builder.Environment);
        if (catalog.BrowserPageAssemblies.Length != 1 || (!string.IsNullOrEmpty(path) && catalog.Extensions.Count == 0) ||
            catalog.LoadErrors.Count != 1 || !catalog.LoadErrors[0].StartsWith("RouteAdapter: a plugin route conflicts", StringComparison.Ordinal) ||
            catalog.RoutableAssemblies.Any(a => a.GetName().Name == "RouteAdapter"))
            throw new InvalidDataException("Viewer fixture failed to load: " + string.Join(", ", catalog.LoadErrors));
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
