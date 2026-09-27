using Bunit;
using Bunit.TestDoubles;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Quasar.Components;
using Quasar.Components.Dashboard;
using Quasar.Services;
using Quasar.Services.Auth;
using MudBlazor.Services;
using MudBlazor;
using Xunit;

namespace Quasar.Tests;

public sealed class ClusterPluginConfigurationPanelTests
{
    [Fact]
    public async Task EditorFollowsSavedPreparationAndRejectsUnauthorizedSave()
    {
        string root = Path.Combine(Path.GetTempPath(), "cluster-plugin-panel-" + Guid.NewGuid());
        try
        {
            using var catalog = new ClusterCatalog(NullLogger<ClusterCatalog>.Instance,
                new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                { ["Quasar:ClusterCatalogPath"] = root }).Build());
            var cluster = await catalog.CreateAsync(new("test", "Test", "http://gateway.test", "TEST_TOKEN"), default);
            await using var context = new BunitContext();
            context.Services.AddMudServices();
            context.JSInterop.Mode = JSRuntimeMode.Loose;
            context.AddAuthorization().SetAuthorized("viewer");
            context.Services.AddSingleton(catalog);
            context.Services.AddSingleton(new ClusterDeploymentService(catalog, null!, null!));
            context.Services.AddSingleton(new QuasarConfigProfileCatalog(NullLogger<QuasarConfigProfileCatalog>.Instance));
            context.Services.AddSingleton(new QuasarRoleMapper(new QuasarAuthOptions(), null!));
            context.Services.AddSingleton<QuasarPermissionService>();
            context.ComponentFactories.AddStub<PluginConfigEditor>();
            var panel = context.Render<MudExpansionPanels>(p => p.AddChildContent<ClusterPluginConfigurationPanel>(c => c.Add(x => x.Cluster, cluster)));
            Assert.Contains("No editable plugin configurations", panel.Markup);

            var preparation = new ClusterPreparationRequest(new string('a', 64), """
                {"pluginConfigurations":{"quasar-agent":{"configType":"Primary",
                  "configuration":{"schema":{},"values":{"limit":1}}}}}
                """, "http://gateway.test", []);
            await catalog.RecordPreparationAsync(cluster, preparation, new(null, "revision", []), default);
            await panel.WaitForAssertionAsync(() => Assert.Contains("Quasar Agent", panel.Markup));
            var editor = panel.FindComponent<Stub<PluginConfigEditor>>();
            Assert.Contains("\"limit\":1", editor.Instance.Parameters.Get(p => p.Plugin).ConfigJson);
            var save = editor.Instance.Parameters.Get(p => p.SaveValues)!;
            await Assert.ThrowsAsync<InvalidOperationException>(() => save("{\"limit\":9}"));
            Assert.Equal(preparation, catalog.GetCluster("test")!.Preparation);

            await catalog.RecordPreparationAsync(catalog.GetCluster("test")!, preparation with
                { SpecificationJson = preparation.SpecificationJson.Replace("\"limit\":1", "\"limit\":2") },
                new(null, "next", []), default);
            await panel.WaitForAssertionAsync(() => Assert.Contains("\"limit\":2",
                panel.FindComponent<Stub<PluginConfigEditor>>().Instance.Parameters.Get(p => p.Plugin).ConfigJson));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
