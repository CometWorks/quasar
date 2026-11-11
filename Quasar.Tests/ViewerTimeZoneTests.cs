using Quasar.Services;
using Xunit;

namespace Quasar.Tests;

public sealed class ViewerTimeZoneTests
{
    private static readonly DateTimeOffset Sample = new(2026, 11, 8, 13, 55, 7, TimeSpan.Zero);

    [Fact]
    public void UnknownZoneShowsUtcWithSuffix() =>
        Assert.Equal(
            "2026\u201111\u201108\u00A013:55:07\u00A0UTC",
            ViewerTimeZone.Format(Sample, null)
        );

    [Theory]
    [InlineData("Europe/Budapest", "2026\u201111\u201108\u00A014:55:07")]
    [InlineData("America/Los_Angeles", "2026\u201111\u201108\u00A005:55:07")]
    [InlineData("Asia/Kolkata", "2026\u201111\u201108\u00A019:25:07")]
    [InlineData("UTC", "2026\u201111\u201108\u00A013:55:07")]
    public void NamedZoneShowsViewerWallClock(string zone, string expected) =>
        Assert.Equal(expected, ViewerTimeZone.Format(Sample, ViewerTimeZone.Resolve(zone, null)));

    [Fact]
    public void NamedZoneFollowsDaylightSavingOfTheTimestamp()
    {
        var zone = ViewerTimeZone.Resolve("Europe/Budapest", 60);
        Assert.Equal(
            "2026\u201107\u201101\u00A014:00:00",
            ViewerTimeZone.Format(new DateTimeOffset(2026, 7, 1, 12, 0, 0, TimeSpan.Zero), zone)
        );
    }

    [Fact]
    public void UnknownZoneNameFallsBackToTheBrowserOffset()
    {
        var zone = ViewerTimeZone.Resolve("Not/A_Zone", -300);
        Assert.Equal("2026\u201111\u201108\u00A008:55:07", ViewerTimeZone.Format(Sample, zone));
    }

    [Fact]
    public void NothingUsableLeavesTheZoneUnknown() =>
        Assert.Null(ViewerTimeZone.Resolve(null, null));

    [Fact]
    public void OtherTimestampShapesFormatTheSame()
    {
        var viewer = new ViewerTimeZone();
        Assert.Equal(
            "2026\u201111\u201108\u00A013:55:07\u00A0UTC",
            viewer.FormatTicks(Sample.UtcTicks)
        );
        Assert.Equal(
            "2026\u201111\u201108\u00A013:55:07\u00A0UTC",
            viewer.Format(Sample.UtcDateTime)
        );
        Assert.Equal(
            "2026\u201111\u201108\u00A013:55:07\u00A0UTC",
            viewer.Format(DateTime.SpecifyKind(Sample.UtcDateTime, DateTimeKind.Unspecified))
        );
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
