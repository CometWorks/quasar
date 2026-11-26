using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Quasar.Ui.Components;
using Xunit;

namespace Quasar.Tests.Ui;

public sealed class QuasarNavigationTests
{
    private static readonly QuasarTabItem[] ThreeTabs =
    [
        new("one", "One", builder => builder.AddContent(0, "One content")),
        new("two", "Two", builder => builder.AddContent(0, "Two content")),
        new("three", "Three", builder => builder.AddContent(0, "Three content")),
    ];

    [Fact]
    public void TabsRenderTheActivePanelAndSwitchOnClick()
    {
        var active = "one";
        using var context = new QuasarBunitContext();
        var changed = EventCallback.Factory.Create<string?>(this, next => active = next);

        using var cut = context.Render<QuasarTabs>(parameters => parameters
            .Add(p => p.Tabs, ThreeTabs[..2])
            .Add(p => p.ActiveTab, active)
            .Add(p => p.ActiveTabChanged, changed));

        Assert.Equal(2, cut.FindAll("[role=tab]").Count);
        Assert.Equal("true", cut.FindAll("[role=tab]")[0].GetAttribute("aria-selected"));
        Assert.Contains("One content", cut.Find("[role=tabpanel]").TextContent, StringComparison.Ordinal);

        cut.FindAll("[role=tab]")[1].Click();

        Assert.Equal("two", active);
        Assert.Contains("Two content", cut.Find("[role=tabpanel]").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void OnlyTheSelectedTabPointsAtARenderedPanel()
    {
        using var context = new QuasarBunitContext();
        using var cut = context.Render<QuasarTabs>(parameters => parameters
            .Add(p => p.Tabs, ThreeTabs));

        var tabs = cut.FindAll("[role=tab]");
        var panel = cut.Find("[role=tabpanel]");

        Assert.Equal(panel.Id, tabs[0].GetAttribute("aria-controls"));
        Assert.Null(tabs[1].GetAttribute("aria-controls"));
        Assert.Equal(tabs[0].Id, panel.GetAttribute("aria-labelledby"));
        Assert.Equal("0", tabs[0].GetAttribute("tabindex"));
        Assert.Equal("-1", tabs[1].GetAttribute("tabindex"));
    }

    [Theory]
    [InlineData("ArrowRight", "two")]
    [InlineData("End", "three")]
    public void TabsMoveWithTheKeyboard(string key, string expected)
    {
        var active = "one";
        using var context = new QuasarBunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var changed = EventCallback.Factory.Create<string?>(this, next => active = next);

        using var cut = context.Render<QuasarTabs>(parameters => parameters
            .Add(p => p.Tabs, ThreeTabs)
            .Add(p => p.ActiveTab, active)
            .Add(p => p.ActiveTabChanged, changed));

        cut.Find("[role=tablist]").KeyDown(new KeyboardEventArgs { Key = key });

        Assert.Equal(expected, active);
    }

    [Theory]
    [InlineData("ArrowLeft", "three")]
    [InlineData("Home", "one")]
    public void TabsWrapAndJumpToTheStart(string key, string expected)
    {
        var active = "one";
        using var context = new QuasarBunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var changed = EventCallback.Factory.Create<string?>(this, next => active = next);

        using var cut = context.Render<QuasarTabs>(parameters => parameters
            .Add(p => p.Tabs, ThreeTabs)
            .Add(p => p.ActiveTab, active)
            .Add(p => p.ActiveTabChanged, changed));

        cut.Find("[role=tablist]").KeyDown(new KeyboardEventArgs { Key = key });

        Assert.Equal(expected, active);
    }

    [Fact]
    public void VerticalArrowsDoNotChangeTheTab()
    {
        var changes = 0;
        using var context = new QuasarBunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;

        using var cut = context.Render<QuasarTabs>(parameters => parameters
            .Add(p => p.Tabs, ThreeTabs)
            .Add(p => p.ActiveTabChanged, EventCallback.Factory.Create<string?>(this, _ => changes++)));

        cut.Find("[role=tablist]").KeyDown(new KeyboardEventArgs { Key = "ArrowDown" });
        cut.Find("[role=tablist]").KeyDown(new KeyboardEventArgs { Key = "ArrowUp" });

        Assert.Equal(0, changes);
    }

    [Fact]
    public void ActiveTabFallsBackToTheFirstWhenTheIdIsUnknown()
    {
        using var context = new QuasarBunitContext();
        using var cut = context.Render<QuasarTabs>(parameters => parameters
            .Add(p => p.Tabs, ThreeTabs)
            .Add(p => p.ActiveTab, "missing"));

        Assert.Contains("One content", cut.Find("[role=tabpanel]").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void DisclosureTogglesOpenState()
    {
        var open = true;
        using var context = new QuasarBunitContext();
        var changed = EventCallback.Factory.Create<bool>(this, next => open = next);

        using var cut = context.Render<QuasarDisclosure>(parameters => parameters
            .Add(p => p.Summary, "Advanced")
            .Add(p => p.Open, open)
            .Add(p => p.OpenChanged, changed));

        Assert.True(cut.Find("details").HasAttribute("open"));

        cut.Find("summary").Click();

        Assert.False(open);
        Assert.False(cut.Find("details").HasAttribute("open"));
    }

    [Fact]
    public void MenuWiresPopoverAnchorAndMenuItems()
    {
        using var context = new QuasarBunitContext();

        using var cut = context.Render<QuasarMenu>(parameters => parameters
            .Add(p => p.Label, "Actions")
            .Add(p => p.ChildContent, builder =>
            {
                builder.OpenComponent<QuasarMenuItem>(0);
                builder.AddContent(1, "Edit");
                builder.CloseComponent();
            }));

        var trigger = cut.Find("button[aria-haspopup=menu]");
        var popover = cut.Find("[popover]");

        Assert.Equal(popover.Id, trigger.GetAttribute("popovertarget"));
        Assert.Equal(popover.Id, trigger.GetAttribute("aria-controls"));
        Assert.Equal("false", trigger.GetAttribute("aria-expanded"));
        Assert.Equal("Actions", popover.GetAttribute("aria-label"));
        Assert.True(popover.HasAttribute("data-quasar-menu"));

        // The trigger names an anchor and the popover positions against it, so
        // the menu opens beside the trigger and not centred in the viewport.
        var anchor = trigger.GetAttribute("style")!;
        Assert.StartsWith("anchor-name:--quasar-menu-", anchor, StringComparison.Ordinal);
        Assert.Equal(
            "position-anchor:" + anchor["anchor-name:".Length..],
            popover.GetAttribute("style"));

        var item = cut.Find(".quasar-menu-item");
        Assert.Equal("menuitem", item.GetAttribute("role"));
        Assert.Equal("-1", item.GetAttribute("tabindex"));
        Assert.Equal(popover.Id, item.GetAttribute("popovertarget"));
    }

    [Fact]
    public void MenuItemClickRaisesOnClick()
    {
        var clicks = 0;
        using var context = new QuasarBunitContext();

        using var cut = context.Render<QuasarMenu>(parameters => parameters
            .Add(p => p.Label, "Actions")
            .Add(p => p.ChildContent, builder =>
            {
                builder.OpenComponent<QuasarMenuItem>(0);
                builder.AddAttribute(1, "OnClick", EventCallback.Factory.Create(this, () => clicks++));
                builder.AddAttribute(2, "ChildContent", (RenderFragment)(inner => inner.AddContent(0, "Edit")));
                builder.CloseComponent();
            }));

        cut.Find(".quasar-menu-item").Click();

        Assert.Equal(1, clicks);
    }

    [Fact]
    public void TwoMenusGetDistinctIdsAndAnchors()
    {
        using var context = new QuasarBunitContext();
        using var first = context.Render<QuasarMenu>(parameters => parameters.Add(p => p.Label, "One"));
        using var second = context.Render<QuasarMenu>(parameters => parameters.Add(p => p.Label, "Two"));

        Assert.NotEqual(first.Find("[popover]").Id, second.Find("[popover]").Id);
        Assert.NotEqual(
            first.Find("button[aria-haspopup=menu]").GetAttribute("style"),
            second.Find("button[aria-haspopup=menu]").GetAttribute("style"));
    }
}
