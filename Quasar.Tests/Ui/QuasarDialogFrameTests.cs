using Bunit;
using Quasar.Components.Shared;
using Quasar.Ui;
using Xunit;

namespace Quasar.Tests.Ui;

public sealed class QuasarDialogFrameTests
{
    [Fact]
    public void RendersTitleAndClosesThroughCascadedInstance()
    {
        using var context = new QuasarBunitContext();
        var dialog = new TestDialogInstance();
        var closed = false;

        var cut = context.Render<QuasarDialogFrame>(parameters => parameters
            .Add(p => p.Title, "Frame title")
            .Add(p => p.ShowClose, true)
            .Add(p => p.OnClose, () => closed = true)
            .AddCascadingValue<IQuasarDialogInstance>(dialog));

        Assert.Contains("Frame title", cut.Markup);
        cut.Find("button[aria-label='Close dialog']").Click();

        Assert.True(dialog.Canceled);
        Assert.True(closed);
    }

    [Fact]
    public void UntrustedCloseIconIsEncoded()
    {
        using var context = new QuasarBunitContext();
        var dialog = new TestDialogInstance();

        var cut = context.Render<QuasarDialogFrame>(parameters => parameters
            .Add(p => p.ShowClose, true)
            .Add(p => p.CloseIcon, "</svg><img src=x onerror=alert(1)>")
            .AddCascadingValue<IQuasarDialogInstance>(dialog));

        Assert.DoesNotContain("<img", cut.Markup);
    }

    [Fact]
    public void BlankTitleFallsBackToTheCascadedDialogTitle()
    {
        using var context = new QuasarBunitContext();
        var dialog = new TestDialogInstance();

        var cut = context.Render<QuasarDialogFrame>(parameters => parameters
            .Add(p => p.Title, " ")
            .AddCascadingValue<IQuasarDialogInstance>(dialog));

        Assert.Contains("Service title", cut.Markup);
    }

    private sealed class TestDialogInstance : IQuasarDialogInstance
    {
        public Guid Id { get; } = Guid.CreateVersion7();
        public string? Title => "Service title";
        public bool Canceled { get; private set; }
        public QuasarDialogResult? Result { get; private set; }

        public void Close(QuasarDialogResult? result = null) =>
            Result = result ?? QuasarDialogResult.Cancel();

        public void Cancel()
        {
            Canceled = true;
            Result = QuasarDialogResult.Cancel();
        }
    }
}
