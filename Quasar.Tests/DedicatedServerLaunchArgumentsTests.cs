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

    private static string Build(
        DedicatedServerDefinition definition,
        bool? dataHandlingConsent,
        MagnetarLaunchArgumentStyle style = MagnetarLaunchArgumentStyle.Current,
        string gitHubToken = "")
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
            gitHubToken);
    }

    private static DedicatedServerDefinition CreateDefinition() => new()
    {
        UniqueName = "test",
        DisplayName = "Test",
    };
}
