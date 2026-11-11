using System.Globalization;
using Microsoft.JSInterop;

namespace Quasar.Services;

/// <summary>
/// The browser's time zone for one Blazor circuit. Every timestamp the web UI shows goes through
/// <see cref="Format(DateTimeOffset)"/>, so it reads <c>yyyy-MM-dd HH:mm:ss</c> in the viewer's zone,
/// not the zone of the machine running Quasar. Until the browser has reported its zone, times are
/// shown in UTC with a <c>UTC</c> suffix.
/// </summary>
public sealed class ViewerTimeZone
{
    // Non-breaking hyphens and spaces keep the timestamp together in UI text.
    private const string DatePattern = "yyyy\u2011MM\u2011dd";
    private const string TimePattern = "HH:mm:ss";
    public const string Pattern = DatePattern + "\u00A0" + TimePattern;
    private const string StackedPattern = DatePattern + "\n" + TimePattern;

    public TimeZoneInfo? Zone { get; private set; }

    /// <summary>True once the browser was asked, whether or not it gave a usable zone.</summary>
    public bool Resolved { get; private set; }

    public async Task InitializeAsync(IJSRuntime js)
    {
        try
        {
            var info = await js.InvokeAsync<BrowserTimeZone>("quasarConfigs.getTimeZone");
            Zone = Resolve(info.Id, info.OffsetMinutes);
        }
        catch (JSException) { }
        catch (JSDisconnectedException) { }
        catch (TaskCanceledException) { }
        Resolved = true;
    }

    public string Format(DateTimeOffset value) => Format(value, Zone);

    public string FormatStacked(DateTimeOffset value) => Format(value, Zone, StackedPattern);

    public string Format(DateTimeOffset? value, string missing) =>
        value is { } time ? Format(time) : missing;

    /// <summary>A <see cref="DateTimeKind.Unspecified"/> value is taken as UTC.</summary>
    public string Format(DateTime value) => Format(AsOffset(value));

    public string FormatTicks(long ticksUtc) => Format(new DateTimeOffset(ticksUtc, TimeSpan.Zero));

    /// <summary>Wall-clock time of <paramref name="value"/> in the viewer's zone, for date and time inputs.</summary>
    public DateTime ToViewer(DateTimeOffset value) =>
        Zone is null ? value.UtcDateTime : TimeZoneInfo.ConvertTime(value, Zone).DateTime;

    /// <summary>The instant a wall-clock time typed by the viewer stands for.</summary>
    public DateTimeOffset FromViewer(DateTime wallClock)
    {
        var unspecified = DateTime.SpecifyKind(wallClock, DateTimeKind.Unspecified);
        return Zone is null
            ? new DateTimeOffset(unspecified, TimeSpan.Zero)
            : new DateTimeOffset(unspecified, Zone.GetUtcOffset(unspecified));
    }

    internal static string Format(
        DateTimeOffset value,
        TimeZoneInfo? zone,
        string pattern = Pattern
    ) =>
        zone is null
            ? value.UtcDateTime.ToString(pattern, CultureInfo.InvariantCulture) + "\u00A0UTC"
            : TimeZoneInfo.ConvertTime(value, zone).ToString(pattern, CultureInfo.InvariantCulture);

    /// <summary>
    /// The IANA zone the browser named, or a fixed offset when this machine does not know that zone.
    /// </summary>
    internal static TimeZoneInfo? Resolve(string? id, int? offsetMinutes)
    {
        if (
            !string.IsNullOrWhiteSpace(id)
            && TimeZoneInfo.TryFindSystemTimeZoneById(id, out var zone)
        )
            return zone;
        if (offsetMinutes is not { } minutes || Math.Abs(minutes) > 14 * 60)
            return null;
        var offset = TimeSpan.FromMinutes(minutes);
        var name = $"UTC{(minutes < 0 ? "-" : "+")}{offset:hh\\:mm}";
        return TimeZoneInfo.CreateCustomTimeZone(name, offset, name, name);
    }

    private static DateTimeOffset AsOffset(DateTime value) =>
        value.Kind == DateTimeKind.Local
            ? new DateTimeOffset(value)
            : new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private sealed record BrowserTimeZone(string? Id, int? OffsetMinutes);
}
