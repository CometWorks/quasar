using Bunit;
using Quasar.Components.Shared;
using Xunit;

namespace Quasar.Tests.Ui;

public sealed class QuasarIconButtonTests
{
    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,<script>alert(1)</script>")]
    [InlineData("vbscript:msgbox(1)")]
    [InlineData("//evil.example/path")]
    [InlineData("/\\evil.example/path")]
    [InlineData("java\nscript:alert(1)")]
    public void UnsafeHrefIsNotRendered(string href)
    {
        using var context = new QuasarBunitContext();
        using var cut = context.Render<QuasarIconButton>(parameters => parameters
            .Add(p => p.Label, "Open")
            .Add(p => p.Icon, QuasarIcons.OpenInNew)
            .Add(p => p.Href, href));

        Assert.DoesNotContain("<a", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("<button", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("href", cut.Markup, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/servers")]
    [InlineData("servers/alpha")]
    [InlineData("https://example.com/docs")]
    [InlineData("http://example.com/")]
    [InlineData("mailto:admin@example.com")]
    [InlineData("tel:+123456789")]
    public void SafeHrefIsRendered(string href)
    {
        using var context = new QuasarBunitContext();
        using var cut = context.Render<QuasarIconButton>(parameters => parameters
            .Add(p => p.Label, "Open")
            .Add(p => p.Icon, QuasarIcons.OpenInNew)
            .Add(p => p.Href, href));

        Assert.Contains("<a", cut.Markup, StringComparison.Ordinal);
        Assert.Contains($"href=\"{href}\"", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("<button", cut.Markup, StringComparison.Ordinal);
    }
}
