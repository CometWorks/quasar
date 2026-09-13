using Quasar.Services;
using Xunit;

namespace Quasar.Tests;

public sealed class CorePluginManifestTests
{
    // LEGACY-MAGNETAR-COMPAT: delete these two tests with the Legacy style in the first
    // 2027 Quasar release.
    [Fact]
    public void LegacyMagnetarUsesPrefixedIdsFromLegacyManifests()
    {
        var manifests = QuasarPluginCatalogService.GetLegacyCorePluginManifests(isLinux: true);

        Assert.Collection(
            manifests,
            dotnet =>
            {
                Assert.Equal("se-dotnet-compat", dotnet.PluginId);
                Assert.Equal("Plugins/DotNetCompatLegacyId.xml", dotnet.ManifestFile);
            },
            linux =>
            {
                Assert.Equal("se-linux-compat", linux.PluginId);
                Assert.Equal("Plugins/LinuxCompatLegacyId.xml", linux.ManifestFile);
            });
    }

    [Fact]
    public void LegacyWindowsOnlyNeedsDotNetCompat()
    {
        var manifest = Assert.Single(QuasarPluginCatalogService.GetLegacyCorePluginManifests(isLinux: false));
        Assert.Equal("se-dotnet-compat", manifest.PluginId);
    }

    [Theory]
    [InlineData("dotnet-compat")]
    [InlineData("se-dotnet-compat")]
    public void DotNetCompatIsNeverManuallySelectable(string pluginId)
    {
        Assert.False(QuasarPluginCatalogService.IsManualSelectionAllowed(pluginId));
    }
}
