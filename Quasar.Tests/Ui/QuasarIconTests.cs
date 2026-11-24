using Bunit;
using MudBlazor;
using Quasar.Components.Shared;
using Xunit;

namespace Quasar.Tests.Ui;

public sealed class QuasarIconTests
{
    [Fact]
    public void UntrustedSvgIsEncoded()
    {
        using var context = new QuasarBunitContext();
        using var cut = context.Render<QuasarIcon>(parameters => parameters
            .Add(p => p.Icon, "</svg><img src=x onerror=alert(1)>"));

        Assert.DoesNotContain("<img", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("&lt;/svg&gt;", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void BuiltInIconRendersMarkup()
    {
        using var context = new QuasarBunitContext();
        using var cut = context.Render<QuasarIcon>(parameters => parameters
            .Add(p => p.Icon, QuasarIcons.Close));

        Assert.Contains("<svg", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("<path", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void MudBlazorIconRequiresExplicitTrust()
    {
        using var context = new QuasarBunitContext();
        using var cut = context.Render<QuasarIcon>(parameters => parameters
            .Add(p => p.Icon, Icons.Material.Filled.AccessAlarm));

        Assert.DoesNotContain("<svg", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("&lt;path", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void TrustedMudBlazorIconRendersMarkup()
    {
        using var context = new QuasarBunitContext();
        using var cut = context.Render<QuasarIcon>(parameters => parameters
            .Add(p => p.Icon, Icons.Material.Filled.AccessAlarm)
            .Add(p => p.TrustedMarkup, true));

        Assert.Contains("<svg", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("<path", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void CustomSvgRequiresExplicitTrust()
    {
        using var context = new QuasarBunitContext();
        using var cut = context.Render<QuasarIcon>(parameters => parameters
            .Add(p => p.Icon, "<path d=\"M0 0h24v24H0z\"/>")
            .Add(p => p.TrustedMarkup, true));

        Assert.Contains("<svg", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("<path", cut.Markup, StringComparison.Ordinal);
    }
}
