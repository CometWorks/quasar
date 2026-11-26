using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Quasar.Ui.Components;
using Xunit;

namespace Quasar.Tests.Ui;

public sealed class QuasarTextFieldTests
{
    [Fact]
    public void RendersLabelHintAndRequiredMarker()
    {
        using var context = new BunitContext();
        using var cut = context.Render<QuasarTextField>(parameters => parameters
            .Add(p => p.Label, "Server name")
            .Add(p => p.Hint, "Unique across the fleet.")
            .Add(p => p.Required, true));

        var input = cut.Find("input");
        var label = cut.Find("label");
        Assert.Equal(input.Id, label.GetAttribute("for"));
        Assert.Contains("Server name", label.TextContent, StringComparison.Ordinal);
        Assert.NotNull(cut.Find(".quasar-input-required"));
        Assert.Contains("Unique across the fleet.", cut.Markup, StringComparison.Ordinal);
        Assert.Equal(cut.Find(".quasar-input-hint").Id, input.GetAttribute("aria-describedby"));
    }

    [Fact]
    public void BlurWithEmptyRequiredValueShowsOneMessage()
    {
        using var context = new BunitContext();
        using var cut = context.Render<QuasarTextField>(parameters => parameters
            .Add(p => p.Required, true));

        cut.Find("input").TriggerEvent("onblur", new FocusEventArgs());

        cut.WaitForAssertion(() =>
        {
            var input = cut.Find("input");
            var message = cut.Find(".quasar-input-error");
            Assert.Equal("true", input.GetAttribute("aria-invalid"));
            Assert.Equal(message.Id, input.GetAttribute("aria-describedby"));
            Assert.Contains("This field is required.", message.TextContent, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void ValidationCanReturnSeveralMessages()
    {
        QuasarValidator<string> validator = (_, _) => Task.FromResult(
            QuasarValidationResult.FromErrors("One.", "Two."));

        using var context = new BunitContext();
        using var cut = context.Render<QuasarTextField>(parameters => parameters
            .Add(p => p.Value, "value")
            .Add(p => p.Validation, validator));

        cut.Find("input").TriggerEvent("onblur", new FocusEventArgs());

        cut.WaitForAssertion(() =>
        {
            Assert.Equal(2, cut.FindAll(".quasar-input-errors li").Count);
            Assert.Equal("true", cut.Find("input").GetAttribute("aria-invalid"));
        });
    }

    [Fact]
    public void ChangeRaisesValueChangedAndTracksDirtyState()
    {
        string? value = "Alpha";

        using var context = new BunitContext();
        var changed = EventCallback.Factory.Create<string>(this, next => value = next);
        using var cut = context.Render<QuasarTextField>(parameters => parameters
            .Add(p => p.Value, value)
            .Add(p => p.ValueChanged, changed));

        cut.Instance.CaptureBaseline();
        cut.Find("input").Change("Beta");

        Assert.Equal("Beta", value);
        Assert.True(cut.Instance.IsDirty);
        Assert.True(cut.Instance.IsValid);
    }

    [Fact]
    public void NotImmediateSendsNothingWhileTyping()
    {
        using var context = new BunitContext();
        using var cut = context.Render<QuasarTextField>(parameters => parameters
            .Add(p => p.Value, "Alpha"));

        Assert.DoesNotContain("blazor:oninput", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("blazor:onchange", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void ImmediateCommitsOnlyAfterAPauseInTyping()
    {
        var values = new List<string>();

        using var context = new BunitContext();
        using var cut = context.Render<QuasarTextField>(parameters => parameters
            .Add(p => p.Immediate, true)
            .Add(p => p.ValueChanged, EventCallback.Factory.Create<string>(this, values.Add)));

        var input = cut.Find("input");
        input.Input("a");
        input.Input("ab");
        input.Input("abc");

        Assert.Empty(values);
        Assert.True(cut.Instance.IsValid);

        Eventually.Until(() => values.Count > 0, "the debounced commit");
        Assert.Equal(["abc"], values);
    }

    [Fact]
    public void ImmediateKeepsTypedTextWhenTheParentRendersBeforeTheCommit()
    {
        using var context = new BunitContext();
        using var cut = context.Render<QuasarTextField>(parameters => parameters
            .Add(p => p.Immediate, true)
            .Add(p => p.Value, "start"));

        cut.Find("input").Input("typed");
        cut.Render(parameters => parameters
            .Add(p => p.Immediate, true)
            .Add(p => p.Value, "start"));

        Assert.Equal("typed", cut.Find("input").GetAttribute("value"));
    }

    [Fact]
    public void BlurCommitsAPendingEditImmediately()
    {
        var values = new List<string>();

        using var context = new BunitContext();
        using var cut = context.Render<QuasarTextField>(parameters => parameters
            .Add(p => p.Immediate, true)
            .Add(p => p.ValueChanged, EventCallback.Factory.Create<string>(this, values.Add)));

        cut.Find("input").Input("done");
        cut.Find("input").TriggerEvent("onblur", new FocusEventArgs());

        Assert.Equal(["done"], values);
    }

    [Fact]
    public void FieldWithoutRulesNeverShowsValidationState()
    {
        using var context = new BunitContext();
        using var cut = context.Render<QuasarTextField>(parameters => parameters
            .Add(p => p.Value, "x"));

        cut.Find("input").Change("y");
        cut.Find("input").TriggerEvent("onblur", new FocusEventArgs());

        Assert.False(cut.Instance.IsValidating);
        Assert.True(cut.Instance.IsValid);
        Assert.DoesNotContain("is-validating", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidationRunsOnceForAChangeFollowedByBlur()
    {
        var runs = 0;
        QuasarValidator<string> validator = (_, _) =>
        {
            runs++;
            return Task.FromResult(QuasarValidationResult.Valid);
        };

        using var context = new BunitContext();
        using var cut = context.Render<QuasarTextField>(parameters => parameters
            .Add(p => p.Validation, validator));

        cut.Find("input").Change("x");
        cut.Find("input").TriggerEvent("onblur", new FocusEventArgs());

        cut.WaitForAssertion(() => Assert.Equal(1, runs));
    }

    [Fact]
    public void ReadOnlySetsTheNativeAttribute()
    {
        using var context = new BunitContext();
        using var cut = context.Render<QuasarTextField>(parameters => parameters
            .Add(p => p.ReadOnly, true));

        Assert.True(cut.Find("input").HasAttribute("readonly"));
    }

    [Fact]
    public async Task FormValidationMarksTheFieldInvalid()
    {
        await using var context = new BunitContext();
        using var cut = context.Render<QuasarForm>(parameters => parameters
            .Add(p => p.ChildContent, builder =>
            {
                builder.OpenComponent<QuasarTextField>(0);
                builder.AddAttribute(1, "Required", true);
                builder.CloseComponent();
            }));

        var form = cut.Instance;
        Assert.True(form.IsValid);

        await cut.InvokeAsync(() => form.ValidateAsync());

        await cut.WaitForAssertionAsync(() =>
        {
            Assert.False(form.IsValid);
            Assert.Contains("This field is required.", cut.Markup, StringComparison.Ordinal);
        });
    }
}
