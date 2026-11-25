using Quasar.Services.Plugins;
using Xunit;

namespace Quasar.Tests.Ui;

public sealed class QuasarNavIconSanitizerTests
{
    [Theory]
    [InlineData("<path d=\"M0 0h24v24H0z\" fill=\"none\"/><path d=\"M3 18h18v-2H3v2z\"/>")]
    [InlineData("<g><circle cx=\"12\" cy=\"12\" r=\"10\" fill=\"currentColor\"/></g>")]
    [InlineData("<rect fill=\"none\" height=\"24\" width=\"24\"/>")]
    [InlineData("<path d=\"M0 0h24v24H0z\" fill=\"url(#gradient)\"/>")]
    [InlineData("extension")]
    [InlineData("")]
    public void AcceptsValidFragmentsAndLegacyNames(string icon)
    {
        var safe = QuasarNavIconSanitizer.Sanitize(icon, out var replaced);

        Assert.False(replaced);
        Assert.Equal(icon, safe);
    }

    [Fact]
    public void MissingIconBecomesEmptyText()
    {
        var safe = QuasarNavIconSanitizer.Sanitize(null, out var replaced);

        Assert.False(replaced);
        Assert.Equal(string.Empty, safe);
    }

    [Theory]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("<path d=\"M0 0h24v24H0z\" onclick=\"alert(1)\"/>")]
    [InlineData("<path d=\"M0 0h24v24H0z\" ONLOAD=\"alert(1)\"/>")]
    [InlineData("<image href=\"https://example.test/track\"/>")]
    [InlineData("<path d=\"M0 0h24v24H0z\" fill=\"url(https://example.test/paint)\"/>")]
    [InlineData("<path d=\"M0 0h24v24H0z\" style=\"background:url(https://example.test/x)\"/>")]
    [InlineData("<foreignObject><div>not svg</div></foreignObject>")]
    [InlineData("<path d=\"M0 0h24v24H0z\"><script>alert(1)</script></path>")]
    [InlineData("<path d=\"M0 0h24v24H0z\"")]
    public void RejectsUnsafeOrMalformedFragments(string icon)
    {
        var safe = QuasarNavIconSanitizer.Sanitize(icon, out var replaced);

        Assert.True(replaced);
        Assert.Equal(QuasarNavIconSanitizer.FallbackIcon, safe);
        Assert.True(QuasarNavIconSanitizer.IsSafeSvgFragment(safe));
    }

    [Theory]
    [InlineData("<path d=\"M0 0h24v24H0z\" fill=\"javascript:alert(1)\"/>")]
    [InlineData("<path d=\"M0 0h24v24H0z\" fill=\"data:image/png;base64,AAAA\"/>")]
    [InlineData("<!--<script>alert(1)</script>-->")]
    public void RejectsScriptUrlsInAttributes(string icon)
    {
        Assert.False(QuasarNavIconSanitizer.IsSafeSvgFragment(icon));
    }
}
