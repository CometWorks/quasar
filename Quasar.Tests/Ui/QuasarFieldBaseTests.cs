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

        cut.Find("input").Change("one");
        cut.Find("input").Change("two");

        Assert.Equal(2, tokens.Count);
        Assert.True(tokens[0].IsCancellationRequested);
        Assert.False(tokens[1].IsCancellationRequested);
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
