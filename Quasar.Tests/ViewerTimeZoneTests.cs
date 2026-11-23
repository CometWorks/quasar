using System.Globalization;
using System.Text.Json;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Quasar.Components.Pages;
using Quasar.Services;
using Quasar.Services.Analytics;
using Xunit;

namespace Quasar.Tests;

public sealed class ViewerTimeZoneTests
{
    private static readonly DateTimeOffset Sample = new(2026, 11, 8, 13, 55, 7, TimeSpan.Zero);

    [Fact]
    public void PluginsCanResolveTheOriginalWorkerTypeName() =>
        Assert.Equal(typeof(ViewerTimeZone), Type.GetType("Quasar.Services.ViewerTimeZone, Quasar", throwOnError: true));

    [Fact]
    public void UnknownZoneShowsUtcWithSuffix() =>
        Assert.Equal(
            new DateTime(2026, 11, 8, 13, 55, 7).ToString("G", CultureInfo.CurrentCulture) + "\u00A0UTC",
            ViewerTimeZone.Format(Sample, null)
        );

    public static TheoryData<string, DateTime> Zones => new()
    {
        { "Europe/Budapest", new DateTime(2026, 11, 8, 14, 55, 7) },
        { "America/Los_Angeles", new DateTime(2026, 11, 8, 5, 55, 7) },
        { "Asia/Kolkata", new DateTime(2026, 11, 8, 19, 25, 7) },
        { "UTC", new DateTime(2026, 11, 8, 13, 55, 7) },
    };

    [Theory]
    [MemberData(nameof(Zones))]
    public void NamedZoneShowsViewerWallClock(string zone, DateTime expected) =>
        Assert.Equal(expected.ToString("G", CultureInfo.CurrentCulture),
            ViewerTimeZone.Format(Sample, ViewerTimeZone.Resolve(zone, null)));

    [Fact]
    public void NamedZoneFollowsDaylightSavingOfTheTimestamp()
    {
        var zone = ViewerTimeZone.Resolve("Europe/Budapest", 60);
        Assert.Equal(
            new DateTime(2026, 7, 1, 14, 0, 0).ToString("G", CultureInfo.CurrentCulture),
            ViewerTimeZone.Format(new DateTimeOffset(2026, 7, 1, 12, 0, 0, TimeSpan.Zero), zone)
        );
    }

    [Fact]
    public void UnknownZoneNameFallsBackToTheBrowserOffset()
    {
        var zone = ViewerTimeZone.Resolve("Not/A_Zone", -300);
        Assert.Equal(new DateTime(2026, 11, 8, 8, 55, 7).ToString("G", CultureInfo.CurrentCulture),
            ViewerTimeZone.Format(Sample, zone));
    }

    [Fact]
    public void StandardPatternStaysInvariantForLogsAndData() =>
        Assert.Equal("2026\u201111\u201108\u00A013:55:07",
            Sample.UtcDateTime.ToString(ViewerTimeZone.Pattern, CultureInfo.InvariantCulture));

    [Fact]
    public void NothingUsableLeavesTheZoneUnknown() =>
        Assert.Null(ViewerTimeZone.Resolve(null, null));

    [Fact]
    public async Task DisplaySettingsSaveAndRestoreTheBrowserDefault()
    {
        await using var context = new QuasarBunitContext();
        var viewer = context.Services.GetRequiredService<ViewerTimeZone>();
        var browser = new ViewerTimeZone.BrowserTimeZone("America/New_York", -300, null);
        context
            .JSInterop.Setup<ViewerTimeZone.BrowserTimeZone>("quasarConfigs.getTimeZone")
            .SetResult(browser);
        context.JSInterop.SetupVoid("quasarConfigs.setLocalStorage", _ => true).SetVoidResult();
        await viewer.InitializeAsync(context.JSInterop.JSRuntime);

        var page = context.Render<DisplaySettings>();
        Assert.Contains("(America/New_York)", page.Find("#quasar-time-zone-help").TextContent);
        Assert.Equal(new DateTime(2026, 11, 8, 8, 55, 7).ToString("G", CultureInfo.CurrentCulture), viewer.Format(Sample));
        page.Find("input").Change("Asia/Kolkata");
        page.Find("form").Submit();
        Assert.Equal("Asia/Kolkata", viewer.PreferredId);
        Assert.Equal(
            new DateTime(2026, 11, 8, 19, 25, 7).ToString("d", CultureInfo.CurrentCulture)
                + "\n"
                + new DateTime(2026, 11, 8, 19, 25, 7).ToString("T", CultureInfo.CurrentCulture),
            viewer.FormatStacked(Sample));
        Assert.Equal(
            "Asia/Kolkata",
            context.JSInterop.Invocations["quasarConfigs.setLocalStorage"].Single().Arguments[1]
        );
        var utc = viewer.FromViewer(new DateTime(2026, 11, 8, 19, 25, 7));
        Assert.Equal(Sample, utc);
        Assert.Equal(TimeSpan.Zero, utc.Offset);

        page.Find("input").Change("Unknown/Zone");
        page.Find("form").Submit();
        Assert.Equal("Asia/Kolkata", viewer.PreferredId);
        Assert.Contains("Choose a valid time zone", page.Find("[role=alert]").TextContent);

        page.Find("button[type=button]").Click();
        Assert.Null(viewer.PreferredId);
        Assert.Equal(new DateTime(2026, 11, 8, 8, 55, 7).ToString("G", CultureInfo.CurrentCulture), viewer.Format(Sample));
    }

    [Fact]
    public async Task NewCircuitAppliesTheSavedZone()
    {
        await using var context = new QuasarBunitContext();
        var viewer = new ViewerTimeZone();
        context
            .JSInterop.Setup<ViewerTimeZone.BrowserTimeZone>("quasarConfigs.getTimeZone")
            .SetResult(new("America/New_York", -300, "Asia/Tokyo"));

        await viewer.InitializeAsync(context.JSInterop.JSRuntime);

        Assert.Equal(new DateTime(2026, 11, 8, 22, 55, 7).ToString("G", CultureInfo.CurrentCulture), viewer.Format(Sample));

        var config = new AnalyticsViewConfig
        {
            CustomFromDate = new DateTime(2026, 11, 8),
            CustomFromTime = new TimeSpan(8, 55, 7),
        };
        Assert.True(config.MigrateCustomRange(viewer, new DateTime(2026, 11, 8)));
        Assert.Equal(Sample, config.CustomFromUtc);
        Assert.Equal(TimeSpan.Zero, config.CustomFromUtc!.Value.Offset);
        Assert.Equal(
            new DateTime(2026, 11, 8, 22, 55, 7),
            viewer.ToViewer(config.CustomFromUtc.Value)
        );
        Assert.DoesNotContain("CustomFromDate", JsonSerializer.Serialize(config));
    }

    [Fact]
    public void OtherTimestampShapesFormatTheSame()
    {
        var viewer = new ViewerTimeZone();
        var expected = new DateTime(2026, 11, 8, 13, 55, 7).ToString("G", CultureInfo.CurrentCulture) + "\u00A0UTC";
        Assert.Equal(expected, viewer.FormatTicks(Sample.UtcTicks));
        Assert.Equal(expected, viewer.Format(Sample.UtcDateTime));
        Assert.Equal(expected, viewer.Format(DateTime.SpecifyKind(Sample.UtcDateTime, DateTimeKind.Unspecified)));
        Assert.Equal("never", viewer.Format((DateTimeOffset?)null, "never"));
    }

    [Fact]
    public void ViewerWallClockRoundTripsToTheSameInstant()
    {
        var zone = ViewerTimeZone.Resolve("America/New_York", null);
        var viewer = new ViewerTimeZone();
        typeof(ViewerTimeZone).GetProperty(nameof(ViewerTimeZone.Zone))!.SetValue(viewer, zone);
        var wallClock = viewer.ToViewer(Sample);
        Assert.Equal(new DateTime(2026, 11, 8, 8, 55, 7), wallClock);
        Assert.Equal(Sample, viewer.FromViewer(wallClock));
    }
}
