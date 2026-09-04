using System.Diagnostics;
using Quasar.Services;
using Xunit;

namespace Quasar.Tests;

// LEGACY-MAGNETAR-COMPAT: delete this file together with MagnetarLegacyLaunchCompatibility
// in the first 2027 Quasar release.
public sealed class MagnetarLegacyLaunchCompatibilityTests
{
    [Theory]
    [InlineData("/opt/magnetar/Bin/MagnetarInterim", MagnetarLaunchArgumentStyle.Legacy)]
    [InlineData("/opt/magnetar/MagnetarInterim.bin", MagnetarLaunchArgumentStyle.Current)]
    [InlineData("C:\\Magnetar\\MagnetarInterim.exe", MagnetarLaunchArgumentStyle.Current)]
    [InlineData("C:\\Magnetar\\MagnetarLegacy.exe", MagnetarLaunchArgumentStyle.Current)]
    [InlineData("", MagnetarLaunchArgumentStyle.Current)]
    public void DetectsGenerationFromLauncherNameAndDefaultsToCurrent(string executablePath, MagnetarLaunchArgumentStyle expected)
    {
        // The .exe paths do not exist here, so they exercise the "unknown version" default.
        Assert.Equal(expected, MagnetarLegacyLaunchCompatibility.Detect(executablePath));
    }

    [Fact]
    public void DetectsGenerationFromFileVersionWhenNameIsAmbiguous()
    {
        // Any versioned assembly stands in for a Windows launcher exe: the style must follow
        // its file version relative to the first current release.
        var path = typeof(MagnetarLegacyLaunchCompatibilityTests).Assembly.Location;
        var info = FileVersionInfo.GetVersionInfo(path);
        var version = new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart, info.FilePrivatePart);
        var expected = version < MagnetarLegacyLaunchCompatibility.FirstCurrentVersion
            ? MagnetarLaunchArgumentStyle.Legacy
            : MagnetarLaunchArgumentStyle.Current;

        Assert.Equal(expected, MagnetarLegacyLaunchCompatibility.Detect(path));
    }
}
