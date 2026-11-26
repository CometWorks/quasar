using Bunit;
using Microsoft.AspNetCore.Components;
using Quasar.Ui.Components;
using Xunit;

namespace Quasar.Tests.Ui;

public sealed class QuasarFormTests
{
    [Fact]
    public async Task CaptureBaselineReportsTheFormAsCleanAgain()
    {
        var dirtyChanges = new List<bool>();

        await using var context = new BunitContext();
        using var cut = context.Render<QuasarForm>(parameters => parameters
            .Add(p => p.IsDirtyChanged, EventCallback.Factory.Create<bool>(this, dirtyChanges.Add))
            .Add(p => p.ChildContent, TextField()));

        cut.Find("input").Change("edited");
        await cut.WaitForAssertionAsync(() => Assert.Equal([true], dirtyChanges));
        Assert.True(cut.Instance.IsDirty);

        // A save loads the new values and takes them as the baseline.
        await cut.InvokeAsync(cut.Instance.CaptureBaseline);

        await cut.WaitForAssertionAsync(() => Assert.Equal([true, false], dirtyChanges));
        Assert.False(cut.Instance.IsDirty);
    }

    [Fact]
    public async Task ValidityChangesAreReported()
    {
        var validChanges = new List<bool>();

        await using var context = new BunitContext();
        using var cut = context.Render<QuasarForm>(parameters => parameters
            .Add(p => p.IsValidChanged, EventCallback.Factory.Create<bool>(this, validChanges.Add))
            .Add(p => p.ChildContent, TextField(required: true)));

        await cut.InvokeAsync(() => cut.Instance.ValidateAsync());

        await cut.WaitForAssertionAsync(() =>
        {
            Assert.False(cut.Instance.IsValid);
            Assert.Contains(false, validChanges);
        });

        cut.Find("input").Change("now valid");

        await cut.WaitForAssertionAsync(() => Assert.True(cut.Instance.IsValid));
    }

    [Fact]
    public async Task FormErrorsRenderAsAnAlertAndClear()
    {
        await using var context = new BunitContext();
        using var cut = context.Render<QuasarForm>(parameters => parameters
            .Add(p => p.ChildContent, TextField()));

        await cut.InvokeAsync(() => cut.Instance.AddError("Save failed."));

        await cut.WaitForAssertionAsync(() =>
            Assert.Contains("Save failed.", cut.Find("[role=alert]").TextContent, StringComparison.Ordinal));

        await cut.InvokeAsync(cut.Instance.ClearErrors);

        await cut.WaitForAssertionAsync(() => Assert.Empty(cut.FindAll("[role=alert]")));
    }

    [Fact]
    public async Task DisposedFieldsLeaveTheForm()
    {
        await using var context = new BunitContext();
        var show = true;
        using var cut = context.Render<QuasarForm>(parameters => parameters
            .Add(p => p.ChildContent, builder =>
            {
                if (!show)
                    return;
                builder.OpenComponent<QuasarTextField>(0);
                builder.AddAttribute(1, "Required", true);
                builder.CloseComponent();
            }));

        await cut.InvokeAsync(() => cut.Instance.ValidateAsync());
        await cut.WaitForAssertionAsync(() => Assert.False(cut.Instance.IsValid));

        show = false;
        cut.Render(parameters => parameters
            .Add(p => p.ChildContent, builder => { }));

        await cut.WaitForAssertionAsync(() => Assert.True(cut.Instance.IsValid));
    }

    private static RenderFragment TextField(bool required = false) => builder =>
    {
        builder.OpenComponent<QuasarTextField>(0);
        if (required)
            builder.AddAttribute(1, "Required", true);
        else
            builder.AddAttribute(2, "Value", "start");
        builder.CloseComponent();
    };
}
