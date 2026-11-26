using Bunit;
using Microsoft.AspNetCore.Components;
using Quasar.Ui;
using Quasar.Ui.Components;
using Xunit;

namespace Quasar.Tests.Ui;

public sealed class QuasarDisplayTests
{
    [Fact]
    public void TooltipTextIsARealElementLinkedToTheWrapper()
    {
        using var context = new BunitContext();
        using var cut = context.Render<QuasarTooltip>(parameters => parameters
            .Add(p => p.Text, "Copies the value")
            .Add(p => p.ChildContent, builder => builder.AddContent(0, "Hover me")));

        var wrapper = cut.Find(".quasar-tip");
        var text = cut.Find("[role=tooltip]");

        Assert.Equal("Copies the value", text.TextContent);
        Assert.Equal(text.Id, wrapper.GetAttribute("aria-describedby"));
        Assert.Null(wrapper.GetAttribute("title"));
        Assert.StartsWith("anchor-name:--quasar-tip-", wrapper.GetAttribute("style"), StringComparison.Ordinal);
        Assert.StartsWith("position-anchor:--quasar-tip-", text.GetAttribute("style"), StringComparison.Ordinal);
    }

    [Fact]
    public void ChipActionIsAToggleOnlyWhenSelectedIsSet()
    {
        using var context = new BunitContext();

        using var action = context.Render<QuasarChip>(parameters => parameters
            .Add(p => p.OnClick, EventCallback.Factory.Create(this, () => { }))
            .Add(p => p.ChildContent, "Open"));
        Assert.Null(action.Find(".quasar-chip-action").GetAttribute("aria-pressed"));

        using var on = context.Render<QuasarChip>(parameters => parameters
            .Add(p => p.OnClick, EventCallback.Factory.Create(this, () => { }))
            .Add(p => p.Selected, true)
            .Add(p => p.ChildContent, "Filter"));
        Assert.Equal("true", on.Find(".quasar-chip-action").GetAttribute("aria-pressed"));

        using var off = context.Render<QuasarChip>(parameters => parameters
            .Add(p => p.OnClick, EventCallback.Factory.Create(this, () => { }))
            .Add(p => p.Selected, false)
            .Add(p => p.ChildContent, "Filter"));
        Assert.Equal("false", off.Find(".quasar-chip-action").GetAttribute("aria-pressed"));
    }

    [Fact]
    public void ChipCloseButtonHasAnAccessibleNameAndRaisesOnClose()
    {
        var closed = 0;
        using var context = new BunitContext();
        using var cut = context.Render<QuasarChip>(parameters => parameters
            .Add(p => p.OnClose, EventCallback.Factory.Create(this, () => closed++))
            .Add(p => p.CloseLabel, "Remove filter")
            .Add(p => p.Tone, QuasarTone.Warning)
            .Add(p => p.ChildContent, "Region"));

        var close = cut.Find(".quasar-chip-close");
        Assert.Equal("Remove filter", close.GetAttribute("aria-label"));
        close.Click();

        Assert.Equal(1, closed);
        Assert.Contains("quasar-tone-warning", cut.Find(".quasar-chip").ClassList, StringComparer.Ordinal);
    }

    [Fact]
    public void ListItemRendersAButtonAnAnchorOrText()
    {
        using var context = new BunitContext();

        using var button = context.Render<QuasarListItem>(parameters => parameters
            .Add(p => p.OnClick, EventCallback.Factory.Create(this, () => { }))
            .Add(p => p.ChildContent, "Click"));
        Assert.NotNull(button.Find("button.quasar-list-item"));

        using var link = context.Render<QuasarListItem>(parameters => parameters
            .Add(p => p.Href, "/servers")
            .Add(p => p.Selected, true)
            .Add(p => p.ChildContent, "Servers"));
        Assert.Equal("page", link.Find("a.quasar-list-item").GetAttribute("aria-current"));

        using var unsafeLink = context.Render<QuasarListItem>(parameters => parameters
            .Add(p => p.Href, "javascript:alert(1)")
            .Add(p => p.ChildContent, "Bad"));
        Assert.Empty(unsafeLink.FindAll("a"));
        Assert.NotNull(unsafeLink.Find("span.quasar-list-item"));
    }
}
