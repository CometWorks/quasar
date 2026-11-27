using Bunit;
using Microsoft.AspNetCore.Components;
using Quasar.Ui;
using Quasar.Ui.Components;
using Xunit;

namespace Quasar.Tests.Ui;

public sealed class QuasarButtonTests
{
    [Fact]
    public void RendersToneVariantAndSizeClasses()
    {
        using var context = new BunitContext();
        using var cut = context.Render<QuasarButton>(parameters => parameters
            .Add(p => p.Tone, QuasarTone.Primary)
            .Add(p => p.Variant, QuasarVariant.Filled)
            .Add(p => p.Size, QuasarSize.Large));

        var button = cut.Find("button");
        Assert.Contains("quasar-tone-primary", button.ClassList, StringComparer.Ordinal);
        Assert.Contains("quasar-variant-filled", button.ClassList, StringComparer.Ordinal);
        Assert.Contains("quasar-size-large", button.ClassList, StringComparer.Ordinal);
    }

    [Fact]
    public void SafeHrefRendersAnAnchorWithoutAButtonRole()
    {
        using var context = new BunitContext();
        using var cut = context.Render<QuasarButton>(parameters => parameters
            .Add(p => p.Href, "/servers"));

        var link = cut.Find("a");
        Assert.Equal("/servers", link.GetAttribute("href"));
        Assert.Null(link.GetAttribute("role"));
        Assert.Null(link.GetAttribute("aria-disabled"));
    }

    [Fact]
    public void UnsafeHrefRendersADisabledLinkWithoutHrefOrHandler()
    {
        var clicks = 0;
        using var context = new BunitContext();
        using var cut = context.Render<QuasarButton>(parameters => parameters
            .Add(p => p.Href, "javascript:alert(1)")
            .Add(p => p.OnClick, EventCallback.Factory.Create(this, () => clicks++))
            .Add(p => p.ChildContent, "Open"));

        var link = cut.Find("a");
        Assert.Null(link.GetAttribute("href"));
        Assert.Equal("true", link.GetAttribute("aria-disabled"));
        Assert.Equal("link", link.GetAttribute("role"));
        Assert.Contains("Open", link.TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain("blazor:onclick", cut.Markup, StringComparison.Ordinal);
        Assert.Equal(0, clicks);
    }

    [Fact]
    public void DisabledLinkHasNoHrefAndRaisesNothing()
    {
        using var context = new BunitContext();
        using var cut = context.Render<QuasarButton>(parameters => parameters
            .Add(p => p.Href, "/servers")
            .Add(p => p.Disabled, true)
            .Add(p => p.OnClick, EventCallback.Factory.Create(this, () => { })));

        var link = cut.Find("a");
        Assert.Null(link.GetAttribute("href"));
        Assert.Equal("true", link.GetAttribute("aria-disabled"));
        Assert.DoesNotContain("blazor:onclick", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void LoadingShowsASpinnerAndDisablesTheButton()
    {
        using var context = new BunitContext();
        using var cut = context.Render<QuasarButton>(parameters => parameters
            .Add(p => p.Loading, true));

        var button = cut.Find("button");
        Assert.True(button.HasAttribute("disabled"));
        Assert.Equal("true", button.GetAttribute("aria-busy"));
        Assert.NotNull(cut.Find(".quasar-button-spinner"));
    }

    [Fact]
    public void LoadingLinkIsNotFollowable()
    {
        using var context = new BunitContext();
        using var cut = context.Render<QuasarButton>(parameters => parameters
            .Add(p => p.Href, "/servers")
            .Add(p => p.Loading, true));

        var link = cut.Find("a");
        Assert.Null(link.GetAttribute("href"));
        Assert.Equal("true", link.GetAttribute("aria-busy"));
        Assert.NotNull(cut.Find(".quasar-button-spinner"));
    }

    [Fact]
    public void ClickRaisesOnClick()
    {
        var clicks = 0;
        using var context = new BunitContext();
        using var cut = context.Render<QuasarButton>(parameters => parameters
            .Add(p => p.OnClick, EventCallback.Factory.Create(this, () => clicks++)));

        cut.Find("button").Click();

        Assert.Equal(1, clicks);
    }

    [Fact]
    public void SubmitTypeIsRendered()
    {
        using var context = new BunitContext();
        using var cut = context.Render<QuasarButton>(parameters => parameters
            .Add(p => p.Type, QuasarButtonType.Submit));

        Assert.Equal("submit", cut.Find("button").GetAttribute("type"));
    }
}
