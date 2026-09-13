using Microsoft.Extensions.Logging.Abstractions;
using Quasar.Services.Plugins;
using Xunit;

namespace Quasar.Tests;

public sealed class QuasarManagedDotNetSdkServiceTests
{
    [Fact]
    public void CurrentBuildSdkOnPathIsPreferred()
    {
        var service = new QuasarManagedDotNetSdkService(
            NullLogger<QuasarManagedDotNetSdkService>.Instance,
            new TestHttpClientFactory());

        var status = service.RefreshStatus();

        Assert.Equal(QuasarDotNetSdkSource.Global, status.Source);
        Assert.Equal("dotnet", status.DotNetExecutablePath);
        Assert.True(status.CanBuildUiPlugins);
    }

    [Fact]
    public async Task ManagedSdkIsNotDownloadedWhenGlobalSdkIsAvailable()
    {
        var httpClientFactory = new TestHttpClientFactory();
        var service = new QuasarManagedDotNetSdkService(
            NullLogger<QuasarManagedDotNetSdkService>.Instance,
            httpClientFactory);

        var result = await service.InstallAsync();

        Assert.True(result.Succeeded);
        Assert.Equal(0, httpClientFactory.CreatedClientCount);
        Assert.Equal(QuasarDotNetSdkSource.Global, service.GetStatus().Source);
    }

    [Fact]
    public void CurrentPlatformDownloadIsPinnedAndChecksummed()
    {
        var asset = QuasarManagedDotNetSdkService.GetDownloadAsset();

        Assert.NotNull(asset);
        Assert.Contains(QuasarManagedDotNetSdkService.PinnedSdkVersion, asset.FileName);
        Assert.Contains(QuasarManagedDotNetSdkService.PinnedSdkVersion, asset.Url.AbsoluteUri);
        Assert.Equal(128, asset.Sha512.Length);
    }

    private sealed class TestHttpClientFactory : IHttpClientFactory
    {
        public int CreatedClientCount { get; private set; }

        public HttpClient CreateClient(string name)
        {
            CreatedClientCount++;
            return new HttpClient();
        }
    }
}
