using Microsoft.Extensions.Configuration;
using Quasar.Services;
using Xunit;

namespace Quasar.Tests;

public sealed class HeadlessModeTests
{
    [Fact]
    public void WorkerFlagMapsToQuasarConfiguration()
    {
        Assert.Equal(
            ["--Quasar:Headless=true", "--urls=http://127.0.0.1:8081"],
            Program.NormalizeWorkerArguments(["--headless", "--urls=http://127.0.0.1:8081"]));
        Assert.Equal(
            ["--Quasar:Headless=false"],
            Program.NormalizeWorkerArguments(["--no-headless"]));
    }

    [Fact]
    public void ConfigurationEnablesHeadlessMode()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Quasar:Headless"] = "true",
            })
            .Build();

        Assert.True(WebServiceOptions.Create(configuration).Headless);
    }
}
