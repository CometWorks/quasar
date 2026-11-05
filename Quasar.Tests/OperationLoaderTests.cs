using Bunit;
using Quasar.Components.Shared;
using Xunit;

namespace Quasar.Tests;

public sealed class OperationLoaderTests
{
    [Fact]
    public async Task LongOperationShowsItsActivityAndOnlyReportedPercentages()
    {
        using var context = new BunitContext();
        var loader = context.Render<QuasarOperationLoader>(p => p
            .Add(c => c.Title, "Preparing runtime")
            .Add(c => c.Message, "Downloading Dedicated Server"));

        Assert.Equal("true", loader.Find("[aria-busy]").GetAttribute("aria-busy"));
        Assert.Equal("Preparing runtime", loader.Find("progress").GetAttribute("aria-label"));
        Assert.False(loader.Find("progress").HasAttribute("value"));
        Assert.Contains("Elapsed 00:00:", loader.Markup);
        Assert.Equal("Downloading Dedicated Server", loader.Find("[role=status]").TextContent);

        loader.Render(p => p.Add(c => c.Percent, 42));
        Assert.Equal("42", loader.Find("progress").GetAttribute("value"));
        Assert.Contains("42%", loader.Markup);

        loader.Render(p => p.Add(c => c.Percent, null).Add(c => c.Message, "Verifying downloaded files"));
        Assert.False(loader.Find("progress").HasAttribute("value"));
        Assert.DoesNotContain("42%", loader.Markup);
        Assert.Equal("Verifying downloaded files", loader.Find("[role=status]").TextContent);
        await loader.WaitForAssertionAsync(() => Assert.DoesNotContain("Elapsed 00:00:00", loader.Markup), TimeSpan.FromSeconds(3));
    }
}
