using Bunit;
using Microsoft.AspNetCore.Components;
using Quasar.Ui.Components;
using Xunit;

namespace Quasar.Tests.Ui;

public sealed class QuasarChoiceTests
{
    [Fact]
    public void SelectBindsByOptionIndex()
    {
        var value = "a";
        using var context = new BunitContext();
        var changed = EventCallback.Factory.Create<string>(this, next => value = next);

        using var cut = context.Render<QuasarSelect<string>>(parameters => parameters
            .Add(p => p.Value, value)
            .Add(p => p.ValueChanged, changed)
            .Add(p => p.Items, new[]
            {
                new QuasarOption<string>("a", "Alpha"),
                new QuasarOption<string>("b", "Bravo"),
            }));

        Assert.Equal(2, cut.FindAll("option").Count);
        Assert.True(cut.FindAll("option")[0].HasAttribute("selected"));

        cut.Find("select").TriggerEvent("onchange", new ChangeEventArgs { Value = "1" });

        Assert.Equal("b", value);
    }

    [Fact]
    public void CheckboxRaisesBooleanChange()
    {
        var value = false;
        using var context = new BunitContext();
        var changed = EventCallback.Factory.Create<bool>(this, next => value = next);

        using var cut = context.Render<QuasarCheckbox>(parameters => parameters
            .Add(p => p.Value, value)
            .Add(p => p.ValueChanged, changed)
            .Add(p => p.Label, "Enabled"));

        cut.Find("input").TriggerEvent("onchange", new ChangeEventArgs { Value = true });

        Assert.True(value);
    }

    [Fact]
    public void RadioGroupRendersAndSelectsOptions()
    {
        var value = "a";
        using var context = new BunitContext();
        var changed = EventCallback.Factory.Create<string>(this, next => value = next);

        using var cut = context.Render<QuasarRadioGroup<string>>(parameters => parameters
            .Add(p => p.Value, value)
            .Add(p => p.ValueChanged, changed)
            .Add(p => p.Label, "Profile")
            .Add(p => p.Items, new[]
            {
                new QuasarOption<string>("a", "Alpha"),
                new QuasarOption<string>("b", "Bravo"),
                new QuasarOption<string>("c", "Charlie"),
            }));

        var radios = cut.FindAll("input[type=radio]");
        Assert.Equal(3, radios.Count);

        radios[1].TriggerEvent("onchange", new ChangeEventArgs());

        Assert.Equal("b", value);
    }

    [Fact]
    public void SwitchUsesTheSwitchRole()
    {
        using var context = new BunitContext();
        using var cut = context.Render<QuasarSwitch>(parameters => parameters
            .Add(p => p.Label, "Auto restart"));

        Assert.Equal("switch", cut.Find("input").GetAttribute("role"));
    }

    [Fact]
    public void SelectFollowsAProgrammaticValueChange()
    {
        var items = new[]
        {
            new QuasarOption<string>("a", "Alpha"),
            new QuasarOption<string>("b", "Bravo"),
        };

        using var context = new BunitContext();
        using var cut = context.Render<QuasarSelect<string>>(parameters => parameters
            .Add(p => p.Value, "a")
            .Add(p => p.Items, items));

        cut.Find("select").Change("1");
        cut.Render(parameters => parameters
            .Add(p => p.Value, "a")
            .Add(p => p.Items, items));

        Assert.True(cut.FindAll("option")[0].HasAttribute("selected"));
    }

    [Fact]
    public void SelectDoesNotRebuildItsOptionListForEveryOption()
    {
        var enumerations = 0;

        IEnumerable<QuasarOption<int>> Items()
        {
            enumerations++;
            for (var index = 0; index < 50; index++)
                yield return new QuasarOption<int>(index, "Option " + index);
        }

        using var context = new BunitContext();
        using var cut = context.Render<QuasarSelect<int>>(parameters => parameters
            .Add(p => p.Value, 10)
            .Add(p => p.Items, Items()));

        Assert.Equal(50, cut.FindAll("option").Count);
        Assert.Equal(1, enumerations);
    }

    [Fact]
    public void DisabledOptionIsRenderedDisabled()
    {
        using var context = new BunitContext();
        using var cut = context.Render<QuasarSelect<string>>(parameters => parameters
            .Add(p => p.Value, "a")
            .Add(p => p.Items, new[]
            {
                new QuasarOption<string>("a", "Alpha"),
                new QuasarOption<string>("b", "Bravo") { Disabled = true },
            }));

        Assert.False(cut.FindAll("option")[0].HasAttribute("disabled"));
        Assert.True(cut.FindAll("option")[1].HasAttribute("disabled"));
    }

    [Fact]
    public void ReadOnlyChoiceControlsRenderDisabled()
    {
        using var context = new BunitContext();
        var items = new[] { new QuasarOption<string>("a", "Alpha") };

        using var select = context.Render<QuasarSelect<string>>(parameters => parameters
            .Add(p => p.Items, items)
            .Add(p => p.ReadOnly, true));
        Assert.True(select.Find("select").HasAttribute("disabled"));

        using var checkbox = context.Render<QuasarCheckbox>(parameters => parameters
            .Add(p => p.ReadOnly, true));
        Assert.True(checkbox.Find("input").HasAttribute("disabled"));

        using var toggle = context.Render<QuasarSwitch>(parameters => parameters
            .Add(p => p.ReadOnly, true));
        Assert.True(toggle.Find("input").HasAttribute("disabled"));

        using var radios = context.Render<QuasarRadioGroup<string>>(parameters => parameters
            .Add(p => p.Items, items)
            .Add(p => p.ReadOnly, true));
        Assert.True(radios.Find("input[type=radio]").HasAttribute("disabled"));
    }

    [Fact]
    public void RequiredCheckboxMustBeChecked()
    {
        using var context = new BunitContext();
        using var cut = context.Render<QuasarCheckbox>(parameters => parameters
            .Add(p => p.Label, "I agree")
            .Add(p => p.Required, true));

        cut.Find("input").TriggerEvent("onchange", new ChangeEventArgs { Value = false });

        cut.WaitForAssertion(() =>
            Assert.Contains("This field is required.", cut.Markup, StringComparison.Ordinal));

        cut.Find("input").TriggerEvent("onchange", new ChangeEventArgs { Value = true });

        cut.WaitForAssertion(() =>
            Assert.DoesNotContain("This field is required.", cut.Markup, StringComparison.Ordinal));
    }

    [Fact]
    public void RadioGroupIsALabelledRadiogroupThatCarriesTheInvalidState()
    {
        using var context = new BunitContext();
        using var cut = context.Render<QuasarRadioGroup<string>>(parameters => parameters
            .Add(p => p.Label, "Profile")
            .Add(p => p.Required, true)
            .Add(p => p.Items, new[] { new QuasarOption<string>("a", "Alpha") }));

        var group = cut.Find("fieldset");
        Assert.Equal("radiogroup", group.GetAttribute("role"));
        Assert.Contains("Profile", cut.Find("legend").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void RadioGroupForwardsAdditionalAttributesToTheGroup()
    {
        using var context = new BunitContext();
        using var cut = context.Render<QuasarRadioGroup<string>>(parameters => parameters
            .Add(p => p.Items, new[] { new QuasarOption<string>("a", "Alpha") })
            .AddUnmatched("data-testid", "profile"));

        Assert.Equal("profile", cut.Find("fieldset").GetAttribute("data-testid"));
    }
}
