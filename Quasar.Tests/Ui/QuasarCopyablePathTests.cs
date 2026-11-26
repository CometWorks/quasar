using Bunit;
using Microsoft.JSInterop;
using Microsoft.Extensions.DependencyInjection;
using Quasar.Client.Ui.Toasts;
using Quasar.Ui;
using Quasar.Ui.Components;
using Xunit;

namespace Quasar.Tests.Ui;

public sealed class QuasarCopyablePathTests
{
    [Fact]
    public void EmptyValueRendersNothing()
    {
        using var context = new QuasarBunitContext();
        using var cut = context.Render<QuasarCopyablePath>(parameters => parameters
            .Add(p => p.Value, " "));

        Assert.True(string.IsNullOrWhiteSpace(cut.Markup));
    }

    [Fact]
    public void RendersTheValueWithItsOwnClassesAndACopyButton()
    {
        using var context = new QuasarBunitContext();
        using var cut = context.Render<QuasarCopyablePath>(parameters => parameters
            .Add(p => p.Value, @"C:\Servers\One")
            .Add(p => p.Inline, true)
            .Add(p => p.Class, "extra"));

        var root = cut.Find("span.quasar-copyable-path");
        Assert.Contains("quasar-copyable-path-inline", root.ClassList, StringComparer.Ordinal);
        Assert.Contains("extra", root.ClassList, StringComparer.Ordinal);
        Assert.Equal(@"C:\Servers\One", cut.Find(".quasar-copyable-path-text").TextContent);
        Assert.Equal("Copy path", cut.Find("button").GetAttribute("aria-label"));
        Assert.DoesNotContain("copyable-path-text", cut.Markup.Replace("quasar-copyable-path-text", string.Empty), StringComparison.Ordinal);
    }

    [Fact]
    public void DisplayValueReplacesTheTextAndTheValueTooltipShowsTheFullPath()
    {
        using var context = new QuasarBunitContext();
        using var cut = context.Render<QuasarCopyablePath>(parameters => parameters
            .Add(p => p.Value, "/srv/very/long/path")
            .Add(p => p.DisplayValue, ".../path")
            .Add(p => p.ShowValueTooltip, true));

        var text = cut.Find(".quasar-copyable-path-text");
        Assert.Equal(".../path", text.TextContent);
        Assert.Equal("/srv/very/long/path", text.GetAttribute("title"));
    }

    [Theory]
    [InlineData(true, QuasarSeverity.Info, "Path copied to clipboard.")]
    [InlineData(false, QuasarSeverity.Warning, "Copy failed - select and copy the path manually.")]
    public void ClickCopiesAndRaisesAToast(bool copied, QuasarSeverity severity, string message)
    {
        using var context = new QuasarBunitContext();
        context.JSInterop.Setup<bool>("quasarUi.copyText", "/srv/one").SetResult(copied);
        var toasts = context.Services.GetRequiredService<IQuasarToastService>();

        using var cut = context.Render<QuasarCopyablePath>(parameters => parameters
            .Add(p => p.Value, "/srv/one"));

        cut.Find("button").Click();

        cut.WaitForAssertion(() =>
        {
            var toast = Assert.Single(((QuasarToastService)toasts).Snapshot);
            Assert.Equal(message, toast.Message);
            Assert.Equal(severity, toast.Severity);
        });
    }
}
