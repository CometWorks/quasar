using Bunit;
using Microsoft.AspNetCore.Components;
using Quasar.Ui.Components;
using Xunit;

namespace Quasar.Tests.Ui;

public sealed class QuasarNumberFieldTests
{
    [Fact]
    public void ChangeParsesTheInvariantValue()
    {
        var value = 1;

        using var context = new BunitContext();
        using var cut = Render<int>(context, value, next => value = next);

        cut.Find("input").Change("42");

        Assert.Equal(42, value);
    }

    [Fact]
    public void RendersNumericAttributes()
    {
        using var context = new BunitContext();
        using var cut = context.Render<QuasarNumberField<int>>(parameters => parameters
            .Add(p => p.Value, 5)
            .Add(p => p.Min, "1")
            .Add(p => p.Max, "65535")
            .Add(p => p.Step, "1"));

        var input = cut.Find("input");
        Assert.Equal("number", input.GetAttribute("type"));
        Assert.Equal("1", input.GetAttribute("min"));
        Assert.Equal("65535", input.GetAttribute("max"));
        Assert.Equal("1", input.GetAttribute("step"));
    }

    [Fact]
    public void WholeNumberFieldMarksItselfAndStepsByOne()
    {
        using var context = new BunitContext();
        using var cut = context.Render<QuasarNumberField<int>>(parameters => parameters
            .Add(p => p.Value, 5));

        var input = cut.Find("input");
        Assert.True(input.HasAttribute("data-quasar-integer"));
        Assert.Equal("1", input.GetAttribute("step"));
        Assert.Equal("numeric", input.GetAttribute("inputmode"));
    }

    [Fact]
    public void WholeNumberFieldKeepsAStepTheCallerSets()
    {
        using var context = new BunitContext();
        using var cut = context.Render<QuasarNumberField<int>>(parameters => parameters
            .Add(p => p.Value, 5)
            .Add(p => p.Step, "10"));

        Assert.Equal("10", cut.Find("input").GetAttribute("step"));
    }

    [Fact]
    public void EveryWholeNumberTypeIsMarked()
    {
        Assert.True(IsMarkedWholeNumber<sbyte>());
        Assert.True(IsMarkedWholeNumber<byte>());
        Assert.True(IsMarkedWholeNumber<short>());
        Assert.True(IsMarkedWholeNumber<ushort>());
        Assert.True(IsMarkedWholeNumber<int>());
        Assert.True(IsMarkedWholeNumber<uint>());
        Assert.True(IsMarkedWholeNumber<long>());
        Assert.True(IsMarkedWholeNumber<ulong>());
        Assert.True(IsMarkedWholeNumber<int?>());
    }

    [Fact]
    public void DecimalTypesAreNotMarkedAndUseTheDecimalKeyboard()
    {
        Assert.False(IsMarkedWholeNumber<float>());
        Assert.False(IsMarkedWholeNumber<double>());
        Assert.False(IsMarkedWholeNumber<decimal>());
        Assert.False(IsMarkedWholeNumber<double?>());

        using var context = new BunitContext();
        using var cut = context.Render<QuasarNumberField<double>>();
        var input = cut.Find("input");
        Assert.Null(input.GetAttribute("step"));
        Assert.Equal("decimal", input.GetAttribute("inputmode"));
    }

    [Fact]
    public void UnsignedTypeOrNonNegativeMinimumBlocksTheMinusSign()
    {
        using var context = new BunitContext();

        using var signed = context.Render<QuasarNumberField<int>>(parameters => parameters
            .Add(p => p.Value, 5)
            .Add(p => p.Min, "-5"));
        Assert.False(signed.Find("input").HasAttribute("data-quasar-unsigned"));

        using var unsignedMinimum = context.Render<QuasarNumberField<int>>(parameters => parameters
            .Add(p => p.Value, 5)
            .Add(p => p.Min, "0"));
        Assert.True(unsignedMinimum.Find("input").HasAttribute("data-quasar-unsigned"));

        using var unsignedType = context.Render<QuasarNumberField<uint>>(parameters => parameters
            .Add(p => p.Value, 5u));
        Assert.True(unsignedType.Find("input").HasAttribute("data-quasar-unsigned"));
    }

    [Fact]
    public void DecimalTextInAWholeNumberFieldRestoresTheLastValue()
    {
        var value = 7;

        using var context = new BunitContext();
        using var cut = Render<int>(context, value, next => value = next);

        cut.Find("input").Change("70.5");

        Assert.Equal(7, value);
        Assert.Equal("7", cut.Find("input").GetAttribute("value"));
    }

    [Fact]
    public void ChangeClampsToMinAndMax()
    {
        var value = 10;

        using var context = new BunitContext();
        using var cut = context.Render<QuasarNumberField<int>>(parameters => parameters
            .Add(p => p.Value, value)
            .Add(p => p.ValueChanged, EventCallback.Factory.Create<int>(this, next => value = next))
            .Add(p => p.Min, "1")
            .Add(p => p.Max, "100"));

        cut.Find("input").Change("500");
        Assert.Equal(100, value);

        cut.Find("input").Change("-5");
        Assert.Equal(1, value);
    }

    [Fact]
    public void EmptyChangeRestoresTheLastValueForANonNullableType()
    {
        var value = 7;

        using var context = new BunitContext();
        using var cut = Render<int>(context, value, next => value = next);

        cut.Find("input").Change(string.Empty);

        Assert.Equal(7, value);
        Assert.Equal("7", cut.Find("input").GetAttribute("value"));
    }

    [Fact]
    public void EmptyChangeClearsANullableValue()
    {
        int? value = 7;

        using var context = new BunitContext();
        using var cut = context.Render<QuasarNumberField<int?>>(parameters => parameters
            .Add(p => p.Value, value)
            .Add(p => p.ValueChanged, EventCallback.Factory.Create<int?>(this, next => value = next)));

        cut.Find("input").Change(string.Empty);

        Assert.Null(value);
    }

    [Fact]
    public void ImmediateTypingKeepsAHalfTypedValueAndCommitsAfterAPause()
    {
        var value = 5;

        using var context = new BunitContext();
        using var cut = context.Render<QuasarNumberField<int>>(parameters => parameters
            .Add(p => p.Value, value)
            .Add(p => p.ValueChanged, EventCallback.Factory.Create<int>(this, next => value = next))
            .Add(p => p.Immediate, true)
            .Add(p => p.Min, "10"));

        // A half-typed "-" reads as empty and must neither reach the model nor
        // rewrite the input.
        cut.Find("input").Input(string.Empty);
        Assert.Equal(5, value);
        Assert.Equal(string.Empty, cut.Find("input").GetAttribute("value") ?? string.Empty);

        // A typed value commits after the pause, and is not clamped mid-typing.
        cut.Find("input").Input("1");
        Eventually.Until(() => value == 1, "the debounced commit");
    }

    [Fact]
    public void ChangeAfterImmediateTypingClampsTheFinalValue()
    {
        var value = 50;

        using var context = new BunitContext();
        using var cut = context.Render<QuasarNumberField<int>>(parameters => parameters
            .Add(p => p.Value, value)
            .Add(p => p.ValueChanged, EventCallback.Factory.Create<int>(this, next => value = next))
            .Add(p => p.Immediate, true)
            .Add(p => p.Min, "10"));

        cut.Find("input").Input("1");
        cut.Find("input").Change("1");

        Assert.Equal(10, value);
    }

    [Fact]
    public void NotImmediateRegistersNoInputHandler()
    {
        using var context = new BunitContext();
        using var cut = context.Render<QuasarNumberField<int>>(parameters => parameters
            .Add(p => p.Value, 5));

        Assert.DoesNotContain("blazor:oninput", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("blazor:onchange", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void UnsupportedTypeFailsClearly()
    {
        using var context = new BunitContext();

        var exception = Assert.ThrowsAny<Exception>(() =>
            context.Render<QuasarNumberField<string>>(parameters => parameters
                .Add(p => p.Value, "x")));

        Assert.Contains("not supported", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void RequiredNeverFailsForANonNullableType()
    {
        using var context = new BunitContext();
        using var cut = context.Render<QuasarNumberField<int>>(parameters => parameters
            .Add(p => p.Value, 0)
            .Add(p => p.Required, true));

        cut.Find("input").TriggerEvent("onblur", new Microsoft.AspNetCore.Components.Web.FocusEventArgs());

        Assert.Empty(cut.FindAll(".quasar-input-error"));
    }

    private static bool IsMarkedWholeNumber<T>()
    {
        using var context = new BunitContext();
        using var cut = context.Render<QuasarNumberField<T>>();
        return cut.Find("input").HasAttribute("data-quasar-integer");
    }

    private IRenderedComponent<QuasarNumberField<T>> Render<T>(
        BunitContext context,
        T value,
        Action<T> changed) =>
        context.Render<QuasarNumberField<T>>(parameters => parameters
            .Add(p => p.Value, value)
            .Add(p => p.ValueChanged, EventCallback.Factory.Create<T>(this, changed)));
}
