using Bunit;
using Bunit.TestDoubles;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Quasar.Client.Components;
using Microsoft.JSInterop;
using Quasar.Client.Clusters;
using Quasar.Services;
using Quasar.Services.Auth;
using MudBlazor.Services;
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
            await using var context = new OperationsBrowserContext();
            context.Services.AddMudServices();
            context.JSInterop.Mode = JSRuntimeMode.Loose;
            var auth = context.AddAuthorization();
            auth.SetAuthorized("viewer");
            context.Services.AddSingleton(catalog);
            context.Services.AddSingleton(new ClusterUpdatePreparationService(catalog, null!, null!, null!,
                null!, null!, null!, null!, null!, NullLogger<ClusterUpdatePreparationService>.Instance));
            context.Services.AddSingleton(new QuasarConfigProfileCatalog(NullLogger<QuasarConfigProfileCatalog>.Instance));
            auth.SetPolicies(QuasarPolicyNames.ClusterQuery, QuasarPolicyNames.ClusterManage);
            context.ComponentFactories.AddStub<SchemaConfigEditor>();
            IRenderedComponent<ClusterPluginConfigurationPanel> Open() =>
                context.Render<ClusterPluginConfigurationPanel>(c => c.Add(x => x.UniqueName, cluster.UniqueName));
            Assert.Contains("No editable plugin configurations", Open().Markup);

            var preparation = new ClusterPreparationRequest(new string('a', 64), """
                {"pluginConfigurations":{"quasar-agent":{"configType":"Primary",
                  "configuration":{"schema":{},"values":{"limit":1}}}}}
                """, "http://gateway.test", []);
            await catalog.RecordPreparationAsync(cluster, preparation, new(null, "revision", []), default);
            auth.SetPolicies(QuasarPolicyNames.ClusterQuery);
            var denied = Open();
            Assert.Empty(denied.FindComponents<Stub<SchemaConfigEditor>>());
            Assert.DoesNotContain("limit", denied.Markup);
            auth.SetPolicies(QuasarPolicyNames.ClusterQuery, QuasarPolicyNames.ClusterManage);
            var editor = Open().FindComponent<Stub<SchemaConfigEditor>>();
            Assert.Contains("\"limit\":1", editor.Instance.Parameters.Get(p => p.ConfigJson));
            var save = editor.Instance.Parameters.Get(p => p.SaveValues)!;
            auth.SetPolicies();
            await Assert.ThrowsAsync<JSException>(() => save("{\"limit\":9}"));
            Assert.Equal(preparation.SpecificationJson, catalog.GetCluster("test")!.Preparation!.SpecificationJson);

            await catalog.RecordPreparationAsync(catalog.GetCluster("test")!, preparation with
                { SpecificationJson = preparation.SpecificationJson.Replace("\"limit\":1", "\"limit\":2") },
                new(null, "next", []), default);
            auth.SetPolicies(QuasarPolicyNames.ClusterQuery, QuasarPolicyNames.ClusterManage);
            Assert.Contains("\"limit\":2", Open().FindComponent<Stub<SchemaConfigEditor>>().Instance.Parameters.Get(p => p.ConfigJson));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
