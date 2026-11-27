using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Quasar.Ui.Components;
using Xunit;

namespace Quasar.Tests.Ui;

public sealed class QuasarFieldBaseTests
{
    [Fact]
    public void StaleValidationResultIsDiscarded()
    {
        var slow = new TaskCompletionSource<QuasarValidationResult>();
        QuasarValidator<string> validator = (value, _) =>
            value == "slow"
                ? slow.Task
                : Task.FromResult<QuasarValidationResult>("Fast error.");

        using var context = new BunitContext();
        using var cut = context.Render<QuasarTextField>(parameters => parameters
            .Add(p => p.Validation, validator));

        cut.Find("input").Change("slow");
        cut.WaitForAssertion(() => Assert.True(cut.Instance.IsValidating));

        cut.Find("input").Change("fast");
        cut.WaitForAssertion(() => Assert.Contains("Fast error.", cut.Markup, StringComparison.Ordinal));

        // The first run finishes after it was superseded and must not replace
        // the newer result.
        slow.SetResult("Slow error.");

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Fast error.", cut.Markup, StringComparison.Ordinal);
            Assert.DoesNotContain("Slow error.", cut.Markup, StringComparison.Ordinal);
            Assert.False(cut.Instance.IsValidating);
        });
    }

    [Fact]
    public void NewerValidationCancelsThePreviousToken()
    {
        var tokens = new List<CancellationToken>();
        QuasarValidator<string> validator = (_, token) =>
        {
            tokens.Add(token);
            return new TaskCompletionSource<QuasarValidationResult>().Task;
        };

        using var context = new BunitContext();
        using var cut = context.Render<QuasarTextField>(parameters => parameters
            .Add(p => p.Validation, validator));

        // The first run is the field validating its starting value when it appears.
        cut.Find("input").Change("one");
        cut.Find("input").Change("two");

        Assert.Equal(3, tokens.Count);
        Assert.True(tokens[0].IsCancellationRequested);
        Assert.True(tokens[1].IsCancellationRequested);
        Assert.False(tokens[2].IsCancellationRequested);
    }

    [Fact]
    public void DisposingCancelsARunningValidation()
    {
        CancellationToken captured = default;
        QuasarValidator<string> validator = (_, token) =>
        {
            captured = token;
            return new TaskCompletionSource<QuasarValidationResult>().Task;
        };

        using var context = new BunitContext();
        var cut = context.Render<QuasarTextField>(parameters => parameters
            .Add(p => p.Validation, validator));

        cut.Find("input").Change("x");
        Assert.False(captured.IsCancellationRequested);

        // Disposing the context disposes the renderer and every component in it.
        context.Dispose();

        Assert.True(captured.IsCancellationRequested);
    }

    [Fact]
    public void FailingValidatorShowsAGenericMessage()
    {
        QuasarValidator<string> validator = (_, _) =>
            throw new InvalidOperationException("secret detail");

        using var context = new BunitContext();
        using var cut = context.Render<QuasarTextField>(parameters => parameters
            .Add(p => p.Validation, validator));

        cut.Find("input").Change("x");

        cut.WaitForAssertion(() =>
        {
            var message = cut.Find(".quasar-input-error").TextContent;
            Assert.Contains("Validation failed", message, StringComparison.Ordinal);
            Assert.DoesNotContain("InvalidOperation", message, StringComparison.Ordinal);
            Assert.DoesNotContain("secret detail", message, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void ValidatorThatThrowsItsOwnCancellationIsNotValid()
    {
        // A timeout inside the validator, for example from an HttpClient, is not a
        // newer validation superseding this one.
        QuasarValidator<string> validator = (_, _) =>
            throw new TaskCanceledException("The request timed out.");

        using var context = new BunitContext();
        using var cut = context.Render<QuasarTextField>(parameters => parameters
            .Add(p => p.Validation, validator));

        cut.Find("input").Change("x");

        cut.WaitForAssertion(() =>
        {
            Assert.False(cut.Instance.IsValid);
            Assert.Contains("Validation failed", cut.Find(".quasar-input-error").TextContent, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void EmptyRequiredFieldIsInvalidButShowsNothingUntilTouched()
    {
        using var context = new BunitContext();
        using var cut = context.Render<QuasarTextField>(parameters => parameters
            .Add(p => p.Required, true));

        // Invalid from the first render, so a form can keep Save disabled...
        cut.WaitForAssertion(() => Assert.False(cut.Instance.IsValid));

        // ...but a new, empty field does not open as a red box.
        Assert.Empty(cut.FindAll(".quasar-input-error"));
        Assert.Empty(cut.FindAll(".is-invalid"));
        Assert.Null(cut.Find("input").GetAttribute("aria-invalid"));

        cut.Find("input").TriggerEvent("onblur", new FocusEventArgs());

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("This field is required.", cut.Find(".quasar-input-error").TextContent, StringComparison.Ordinal);
            Assert.NotEmpty(cut.FindAll(".is-invalid"));
            Assert.Equal("true", cut.Find("input").GetAttribute("aria-invalid"));
        });
    }

    [Fact]
    public void LoadedValueThatFailsValidationIsShownAtOnce()
    {
        QuasarValidator<string> validator = (value, _) => Task.FromResult<QuasarValidationResult>(
            value == "bad" ? "That value is not allowed." : QuasarValidationResult.Valid);

        using var context = new BunitContext();
        using var cut = context.Render<QuasarTextField>(parameters => parameters
            .Add(p => p.Value, "bad")
            .Add(p => p.Validation, validator));

        // Nobody has touched the field: a loaded bad value is red, with its reason.
        cut.WaitForAssertion(() =>
        {
            Assert.False(cut.Instance.IsValid);
            Assert.Contains("That value is not allowed.", cut.Find(".quasar-input-error").TextContent, StringComparison.Ordinal);
            Assert.NotEmpty(cut.FindAll(".is-invalid"));
            Assert.Equal("true", cut.Find("input").GetAttribute("aria-invalid"));
        });
    }

    [Fact]
    public void ValueChangedFromOutsideIsValidatedWithoutBeingTouched()
    {
        QuasarValidator<string> validator = (value, _) => Task.FromResult<QuasarValidationResult>(
            value == "bad" ? "That value is not allowed." : QuasarValidationResult.Valid);

        using var context = new BunitContext();
        using var cut = context.Render<QuasarTextField>(parameters => parameters
            .Add(p => p.Value, "fine")
            .Add(p => p.Validation, validator));

        cut.WaitForAssertion(() => Assert.True(cut.Instance.IsValid));

        // The page loads other data into the field, for example after an asynchronous read.
        cut.Render(parameters => parameters
            .Add(p => p.Value, "bad")
            .Add(p => p.Validation, validator));

        cut.WaitForAssertion(() =>
        {
            Assert.False(cut.Instance.IsValid);
            Assert.Contains("That value is not allowed.", cut.Find(".quasar-input-error").TextContent, StringComparison.Ordinal);
        });

        cut.Render(parameters => parameters
            .Add(p => p.Value, "fine")
            .Add(p => p.Validation, validator));

        cut.WaitForAssertion(() =>
        {
            Assert.True(cut.Instance.IsValid);
            Assert.Empty(cut.FindAll(".quasar-input-error"));
        });
    }

    [Fact]
    public void FirstBlurValidatesAnUntouchedRequiredField()
    {
        using var context = new BunitContext();
        using var cut = context.Render<QuasarTextField>(parameters => parameters
            .Add(p => p.Required, true));

        cut.Find("input").TriggerEvent("onblur", new FocusEventArgs());

        cut.WaitForAssertion(() =>
            Assert.Contains("This field is required.", cut.Markup, StringComparison.Ordinal));
    }

    [Fact]
    public void ErrorsAndHintAreLinkedWithAriaDescribedBy()
    {
        using var context = new BunitContext();
        using var cut = context.Render<QuasarTextField>(parameters => parameters
            .Add(p => p.Hint, "A hint.")
            .Add(p => p.Required, true));

        cut.Find("input").TriggerEvent("onblur", new FocusEventArgs());

        cut.WaitForAssertion(() =>
        {
            var input = cut.Find("input");
            var describedBy = input.GetAttribute("aria-describedby")!.Split(' ');
            Assert.Equal(2, describedBy.Length);
            Assert.Equal(cut.Find(".quasar-input-hint").Id, describedBy[0]);
            Assert.Equal(cut.Find(".quasar-input-error").Id, describedBy[1]);
            Assert.Equal("true", input.GetAttribute("aria-invalid"));
        });
    }
}
