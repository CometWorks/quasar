using System.Diagnostics;
using Quasar.Services;
using Xunit;

namespace Quasar.Tests;

public sealed class DedicatedServerLaunchEnvironmentLogTests
{
    [Fact]
    public void DescribeLaunchEnvironmentMasksPulsarGitHubToken()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "/opt/magnetar/MagnetarInterim.bin",
            Arguments = "-noconsole -daemon -consent accept",
            WorkingDirectory = "/opt/magnetar",
        };
        startInfo.Environment["PULSAR_GITHUB_TOKEN"] = "ghp_verySecretToken";
        startInfo.Environment["QUASAR_UNIQUE_NAME"] = "test";

        var description = DedicatedServerSupervisor.DescribeLaunchEnvironment("test", startInfo);

        Assert.DoesNotContain("ghp_verySecretToken", description, StringComparison.Ordinal);
        Assert.Contains("PULSAR_GITHUB_TOKEN=<redacted>", description, StringComparison.Ordinal);
        Assert.Contains("QUASAR_UNIQUE_NAME=test", description, StringComparison.Ordinal);
        Assert.Contains("Arguments=-noconsole -daemon -consent accept", description, StringComparison.Ordinal);
        Assert.Contains("WorkingDirectory=/opt/magnetar", description, StringComparison.Ordinal);
    }

    [Fact]
    public void DescribeLaunchEnvironmentStillRedactsLegacyTokenArgument()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "MagnetarInterim.exe",
            Arguments = "-github-token \"ghp secret\" -noconsole",
        };

        var description = DedicatedServerSupervisor.DescribeLaunchEnvironment("test", startInfo);

        Assert.DoesNotContain("ghp secret", description, StringComparison.Ordinal);
        Assert.Contains("-github-token <redacted> -noconsole", description, StringComparison.Ordinal);
    }
}
