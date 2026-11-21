using Quasar.Models;
using Quasar.Services;
using Xunit;

namespace Quasar.Tests;

public sealed class DedicatedServerLaunchArgumentsTests
{
    [Theory]
    [InlineData(true, "-consent accept")]
    [InlineData(false, "-consent deny")]
    [InlineData(null, "-consent deny")]
    public void BuildLaunchArgumentsPassesConsentAsValue(bool? consent, string expectedFlag)
    {
        var arguments = Build(CreateDefinition(), consent);

        Assert.Contains(expectedFlag, arguments, StringComparison.Ordinal);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(arguments, @"(?<!\S)-consent(?!\S)"));
        Assert.DoesNotContain("-noconsent", arguments, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("-withdraw-consent", arguments, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildLaunchArgumentsNeverEmitsGitHubToken()
    {
        var definition = CreateDefinition();
        definition.LaunchArguments = "-verbose -github-token ghp_userSuppliedSecret -consent withdraw";

        var arguments = Build(definition, dataHandlingConsent: true, gitHubToken: "ghp_configuredSecret");

        Assert.DoesNotContain("-github-token", arguments, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ghp_userSuppliedSecret", arguments, StringComparison.Ordinal);
        Assert.DoesNotContain("ghp_configuredSecret", arguments, StringComparison.Ordinal);
        Assert.DoesNotContain("withdraw", arguments, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("-verbose", arguments, StringComparison.Ordinal);
        Assert.Contains("-consent accept", arguments, StringComparison.Ordinal);
    }

    // LEGACY-MAGNETAR-COMPAT: delete these two tests together with the Legacy style in the
    // first 2027 Quasar release.
    [Theory]
    [InlineData(true, "-consent")]
    [InlineData(false, "-noconsent")]
    [InlineData(null, "-noconsent")]
    public void LegacyStyleUsesBareConsentFlags(bool? consent, string expectedFlag)
    {
        var arguments = Build(CreateDefinition(), consent, MagnetarLaunchArgumentStyle.Legacy);

        Assert.Contains(expectedFlag, arguments.Split(' '));
        Assert.DoesNotContain(" accept", arguments, StringComparison.Ordinal);
        Assert.DoesNotContain(" deny", arguments, StringComparison.Ordinal);
    }

    [Fact]
    public void LegacyStylePassesConfiguredGitHubTokenOnCommandLine()
    {
        var definition = CreateDefinition();
        definition.LaunchArguments = "-github-token ghp_userSuppliedSecret";

        var withToken = Build(definition, true, MagnetarLaunchArgumentStyle.Legacy, "ghp_configuredSecret");
        var withoutToken = Build(definition, true, MagnetarLaunchArgumentStyle.Legacy, string.Empty);

        Assert.Contains("-github-token \"ghp_configuredSecret\"", withToken, StringComparison.Ordinal);
        Assert.DoesNotContain("ghp_userSuppliedSecret", withToken, StringComparison.Ordinal);
        Assert.DoesNotContain("-github-token", withoutToken, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("-consent accept", "")]
    [InlineData("-consent deny -verbose", "-verbose")]
    [InlineData("-verbose -consent withdraw", "-verbose")]
    [InlineData("-consent \"accept\"", "")]
    [InlineData("-consent", "")]
    [InlineData("-consent -verbose", "-verbose")]
    [InlineData("-noconsent", "")]
    [InlineData("-noconsent -verbose", "-verbose")]
    [InlineData("-withdraw-consent -verbose", "-verbose")]
    [InlineData("-CONSENT Deny", "")]
    public void StripManagedArgumentsRemovesEveryConsentForm(string input, string expected)
    {
        Assert.Equal(expected, DedicatedServerRuntimePreparer.StripManagedArguments(input));
    }

    [Theory]
    [InlineData("-github-token ghp_secret", "")]
    [InlineData("-github-token \"ghp secret\" -verbose", "-verbose")]
    [InlineData("-verbose -github-token ghp_secret", "-verbose")]
    [InlineData("-github-token", "")]
    [InlineData("-GitHub-Token ghp_secret -consent accept -verbose", "-verbose")]
    public void StripManagedArgumentsScrubsGitHubToken(string input, string expected)
    {
        var sanitized = DedicatedServerRuntimePreparer.StripManagedArguments(input);

        Assert.Equal(expected, sanitized);
        Assert.DoesNotContain("ghp", sanitized, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void StripManagedArgumentsKeepsUnrelatedOptionsThatMerelyContainTheWords()
    {
        Assert.Equal(
            "-consentful -myconsent -github-tokens x",
            DedicatedServerRuntimePreparer.StripManagedArguments("-consentful -myconsent -github-tokens x"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ClonedDefinitionKeepsTheImplicitModSetting(bool disable)
    {
        var definition = CreateDefinition();
        definition.DisableImplicitMagnetarModLoad = disable;

        var arguments = Build(definition.Clone(), dataHandlingConsent: true);

        Assert.Equal(disable, arguments.Split(' ').Contains("-noimplicitmod"));
    }

    [Theory]
    [InlineData(3, 200, "2.4.3.11", "-logMaxAgeDays 3 -logMaxSizeMB 200")]
    [InlineData(0, 1024, "2.4.4.0", "-logMaxAgeDays 0")]
    [InlineData(14, 0, "2.4.3.11", "-logMaxSizeMB 0")]
    [InlineData(14, 1024, "2.4.3.11", "")]
    [InlineData(3, 200, "2.4.3.10", "")]
    [InlineData(3, 200, null, "")]
    public void BuildLaunchArgumentsPassesLogRetentionOnlyWhenSetAndSupported(
        int maxAgeDays, int maxSizeMB, string? magnetarVersion, string expected)
    {
        var definition = CreateDefinition();
        definition.LogMaxAgeDays = maxAgeDays;
        definition.LogMaxSizeMB = maxSizeMB;
        definition.LaunchArguments = "-logMaxAgeDays 7 -logmaxsizemb=50 -verbose";

        var arguments = Build(
            definition.Clone(),
            dataHandlingConsent: true,
            magnetarVersion: magnetarVersion is null ? null : Version.Parse(magnetarVersion));

        var emitted = System.Text.RegularExpressions.Regex.Matches(arguments, @"-logmax\w+ \d+", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            .Select(match => match.Value);
        Assert.Equal(expected, string.Join(" ", emitted));
        Assert.DoesNotContain("=50", arguments, StringComparison.Ordinal);
        Assert.Contains("-verbose", arguments, StringComparison.Ordinal);
    }

    [Fact]
    public void LogRetentionDefaultsToMagnetarsAndRejectsNegatives()
    {
        var fresh = System.Text.Json.JsonSerializer.Deserialize<DedicatedServerDefinition>("{}")!;
        Assert.Equal((14, 1024), (fresh.LogMaxAgeDays, fresh.LogMaxSizeMB));

        var negative = CreateDefinition();
        negative.LogMaxAgeDays = -1;
        negative.LogMaxSizeMB = -5;
        var normalized = DedicatedServerCatalog.Normalize(negative);
        Assert.Equal((14, 1024), (normalized.LogMaxAgeDays, normalized.LogMaxSizeMB));

        var off = CreateDefinition();
        off.LogMaxAgeDays = 0;
        off.LogMaxSizeMB = 0;
        normalized = DedicatedServerCatalog.Normalize(off);
        Assert.Equal((0, 0), (normalized.LogMaxAgeDays, normalized.LogMaxSizeMB));
    }

    [Theory]
    [InlineData("MagnetarInterim.bin", "MagnetarInterim.dll")]
    [InlineData("MagnetarInterim", "MagnetarInterim.dll")]
    [InlineData("MagnetarLegacy.exe", "MagnetarLegacy.exe")]
    public void MagnetarVersionIsReadFromTheManagedAssembly(string launcher, string versionFile)
    {
        var directory = Directory.CreateTempSubdirectory("quasar-magnetar-version-");
        try
        {
            // Any assembly with a version resource stands in for the Magnetar one.
            var source = typeof(DedicatedServerLaunchArgumentsTests).Assembly.Location;
            File.Copy(source, Path.Combine(directory.FullName, versionFile));
            if (launcher != versionFile)
                File.WriteAllText(Path.Combine(directory.FullName, launcher), "native apphost");
            var expected = System.Diagnostics.FileVersionInfo.GetVersionInfo(source);

            var version = MagnetarLogRetention.TryReadMagnetarVersion(Path.Combine(directory.FullName, launcher));

            Assert.Equal(new Version(expected.FileMajorPart, expected.FileMinorPart, expected.FileBuildPart, expected.FilePrivatePart), version);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void MagnetarVersionIsUnknownWithoutTheAssembly()
    {
        Assert.Null(MagnetarLogRetention.TryReadMagnetarVersion(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "MagnetarInterim.bin")));
        Assert.Null(MagnetarLogRetention.TryReadMagnetarVersion(""));
    }

    private static string Build(
        DedicatedServerDefinition definition,
        bool? dataHandlingConsent,
        MagnetarLaunchArgumentStyle style = MagnetarLaunchArgumentStyle.Current,
        string gitHubToken = "",
        Version? magnetarVersion = null)
    {
        return DedicatedServerRuntimePreparer.BuildLaunchArguments(
            definition,
            "/data/ds",
            "/data/magnetar",
            "/opt/ds64",
            "/data/ds/Saves/World",
            "/data/ds/SpaceEngineers-Dedicated.cfg",
            new WebServiceOptions(),
            dataHandlingConsent,
            style,
            gitHubToken,
            magnetarVersion);
    }

    private static DedicatedServerDefinition CreateDefinition() => new()
    {
        UniqueName = "test",
        DisplayName = "Test",
    };
}
