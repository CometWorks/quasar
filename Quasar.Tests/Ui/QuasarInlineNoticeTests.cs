using Bunit;
using Microsoft.AspNetCore.Components;
using Quasar.Ui;
using Quasar.Ui.Components;
using Xunit;

namespace Quasar.Tests.Ui;

public sealed class QuasarInlineNoticeTests
{
    [Fact]
    public void RendersContentAndDefaultStatusRole()
    {
        using var context = new QuasarBunitContext();
        using var cut = context.Render<QuasarInlineNotice>(parameters => parameters
            .Add(p => p.ChildContent, builder => builder.AddContent(0, "Maintenance starts soon.")));

        Assert.Contains("Maintenance starts soon.", cut.Markup, StringComparison.Ordinal);
        Assert.Equal("status", cut.Find(".quasar-notice").GetAttribute("role"));
        Assert.Equal("true", cut.Find(".quasar-notice").GetAttribute("aria-atomic"));
    }

    [Theory]
    [InlineData(QuasarSeverity.Normal, null, "status")]
    [InlineData(QuasarSeverity.Info, "quasar-notice-info", "status")]
    [InlineData(QuasarSeverity.Success, "quasar-notice-success", "status")]
    [InlineData(QuasarSeverity.Warning, "quasar-notice-warning", "alert")]
    [InlineData(QuasarSeverity.Error, "quasar-notice-error", "alert")]
    public void SeverityMapsToClassAndRole(QuasarSeverity severity, string? expectedClass, string expectedRole)
    {
        using var context = new QuasarBunitContext();
        using var cut = context.Render<QuasarInlineNotice>(parameters => parameters
            .Add(p => p.Severity, severity));

        var notice = cut.Find(".quasar-notice");
        Assert.Equal(expectedRole, notice.GetAttribute("role"));
        if (expectedClass is null)
        {
            Assert.DoesNotContain("quasar-notice-info", notice.ClassList, StringComparer.Ordinal);
            Assert.DoesNotContain("quasar-notice-success", notice.ClassList, StringComparer.Ordinal);
            Assert.DoesNotContain("quasar-notice-warning", notice.ClassList, StringComparer.Ordinal);
            Assert.DoesNotContain("quasar-notice-error", notice.ClassList, StringComparer.Ordinal);
        }
        else
        {
            Assert.Contains(expectedClass, notice.ClassList, StringComparer.Ordinal);
        }
    }

    [Theory]
    [InlineData(QuasarInlineNoticeVariant.Outlined, null)]
    [InlineData(QuasarInlineNoticeVariant.Filled, "quasar-notice-filled")]
    [InlineData(QuasarInlineNoticeVariant.Text, "quasar-notice-text")]
    public void VariantMapsToClass(QuasarInlineNoticeVariant variant, string? expectedClass)
    {
        using var context = new QuasarBunitContext();
        using var cut = context.Render<QuasarInlineNotice>(parameters => parameters
            .Add(p => p.Variant, variant));

        var notice = cut.Find(".quasar-notice");
        if (expectedClass is null)
        {
            Assert.DoesNotContain("quasar-notice-filled", notice.ClassList, StringComparer.Ordinal);
            Assert.DoesNotContain("quasar-notice-text", notice.ClassList, StringComparer.Ordinal);
        }
        else
        {
            Assert.Contains(expectedClass, notice.ClassList, StringComparer.Ordinal);
        }
    }

    [Fact]
    public void DenseAddsDensityClass()
    {
        using var context = new QuasarBunitContext();
        using var cut = context.Render<QuasarInlineNotice>(parameters => parameters
            .Add(p => p.Dense, true));

        Assert.Contains("quasar-notice-dense", cut.Find(".quasar-notice").ClassList, StringComparer.Ordinal);
    }

    [Fact]
    public void ClassAndStylePassThrough()
    {
        using var context = new QuasarBunitContext();
        using var cut = context.Render<QuasarInlineNotice>(parameters => parameters
            .Add(p => p.Severity, QuasarSeverity.Info)
            .Add(p => p.Class, "custom-notice")
            .Add(p => p.Style, "margin-top: 4px"));

        var notice = cut.Find(".quasar-notice");
        Assert.Contains("custom-notice", notice.ClassList, StringComparer.Ordinal);
        Assert.Equal("margin-top: 4px", notice.GetAttribute("style"));
    }

    [Fact]
    public void AdditionalAttributesPassThroughWithoutOverridingComponentAttributes()
    {
        using var context = new QuasarBunitContext();
        using var cut = context.Render<QuasarInlineNotice>(parameters => parameters
            .Add(p => p.Class, "custom-notice")
            .Add(p => p.Style, "color: red")
            .Add(p => p.AdditionalAttributes, new Dictionary<string, object>
            {
                ["id"] = "notice-1",
                ["data-test"] = "notice",
                ["aria-label"] = "Status",
                ["class"] = "ignored-class",
                ["style"] = "color: blue",
                ["role"] = "presentation",
                ["aria-atomic"] = "false",
            }));

        var notice = cut.Find(".quasar-notice");
        Assert.Equal("notice-1", notice.Id);
        Assert.Equal("notice", notice.GetAttribute("data-test"));
        Assert.Equal("Status", notice.GetAttribute("aria-label"));
        Assert.Contains("custom-notice", notice.ClassList, StringComparer.Ordinal);
        Assert.DoesNotContain("ignored-class", notice.ClassList, StringComparer.Ordinal);
        Assert.Equal("color: red", notice.GetAttribute("style"));
        Assert.Equal("status", notice.GetAttribute("role"));
        Assert.Equal("true", notice.GetAttribute("aria-atomic"));
    }

    [Fact]
    public void ClassAndStyleCannotBreakOutOfAttributes()
    {
        using var context = new QuasarBunitContext();
        using var cut = context.Render<QuasarInlineNotice>(parameters => parameters
            .Add(p => p.Class, "custom\" onclick=\"alert(1)")
            .Add(p => p.Style, "color:red\" onmouseover=\"alert(1)"));

        var notice = cut.Find(".quasar-notice");
        Assert.Contains("custom\" onclick=\"alert(1)", notice.GetAttribute("class"), StringComparison.Ordinal);
        Assert.Equal("color:red\" onmouseover=\"alert(1)", notice.GetAttribute("style"));
        Assert.False(notice.HasAttribute("onclick"));
        Assert.False(notice.HasAttribute("onmouseover"));
    }
}
