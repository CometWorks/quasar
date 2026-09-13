using Quasar.Services.Plugins;
using Xunit;

namespace Quasar.Tests;

public sealed class QuasarUiPluginStateStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), $"quasar-ui-plugin-state-{Guid.NewGuid():N}");

    [Fact]
    public async Task ImplicitInstallSuppressionSurvivesReloadAndManualClear()
    {
        var statePath = Path.Combine(_directory, "ui-plugins.state.json");
        var store = new QuasarUiPluginStateStore(statePath);

        await store.SetEnabledAsync("cometworks.entityviewer", enabled: false);
        await store.SetImplicitInstallSuppressedAsync("entity-viewer-catalog", suppressed: true);

        var reloaded = new QuasarUiPluginStateStore(statePath);
        var entry = new QuasarUiPluginHubEntry
        {
            CatalogId = "entity-viewer-catalog",
            ImplicitLoading = true,
        };
        Assert.True(reloaded.IsImplicitInstallSuppressed("ENTITY-VIEWER-CATALOG"));
        Assert.False(reloaded.GetState("cometworks.entityviewer").Enabled);
        Assert.False(QuasarUiPluginHubCatalogService.ShouldInstallImplicitPlugin(entry, reloaded));

        await reloaded.SetImplicitInstallSuppressedAsync("entity-viewer-catalog", suppressed: false);

        var cleared = new QuasarUiPluginStateStore(statePath);
        Assert.False(cleared.IsImplicitInstallSuppressed("entity-viewer-catalog"));
        Assert.True(QuasarUiPluginHubCatalogService.ShouldInstallImplicitPlugin(entry, cleared));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }
}
