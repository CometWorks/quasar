using Quasar.Client.Formatting;
using Xunit;

namespace Quasar.Tests;

public sealed class NumberTextTests
{
    [Fact]
    public void DisplayUsesTheViewerCultureAndInvariantStaysInvariant()
    {
        Assert.Equal(0.98.ToString("0.00", System.Globalization.CultureInfo.CurrentCulture), NumberText.Display(0.98, "0.00"));
        Assert.Equal("0.98", NumberText.Invariant(0.98, "0.00"));
    }

    [Fact]
    public void UserParsingAcceptsTheCurrentCultureAndInvariantFallback()
    {
        var local = 0.98.ToString("0.00", System.Globalization.CultureInfo.CurrentCulture);
        Assert.True(NumberText.TryParseUser(local, out var parsed));
        Assert.Equal(0.98, parsed, 2);

        // Values copied from configs and logs use the invariant format.
        Assert.True(NumberText.TryParseUser("0.98", out parsed));
        Assert.Equal(0.98, parsed, 2);
    }
}
