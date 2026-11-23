using System.Globalization;
using Microsoft.JSInterop;

namespace Quasar.Services;

/// <summary>
/// Converts UTC timestamps at the UI boundary using the viewer's chosen zone or the browser's local zone.
/// </summary>
public sealed class ViewerTimeZone
{
    private const string StorageKey = "quasar.timeZone";

    // Non-breaking hyphens and spaces keep the machine-readable timestamp together.
    private const string DatePattern = "yyyy\u2011MM\u2011dd";
    private const string TimePattern = "HH:mm:ss";

    /// <summary>
    /// Invariant timestamp pattern for logs, configs and machine-readable output.
    /// The UI display uses the viewer's culture instead.
    /// </summary>
    public const string Pattern = DatePattern + "\u00A0" + TimePattern;

    private const string DisplayFormat = "G";
    private const string DisplayDatePattern = "d";
    private const string DisplayTimePattern = "T";

    public TimeZoneInfo? Zone { get; private set; }
    public string? PreferredId { get; private set; }
    public string? BrowserId { get; private set; }
    private TimeZoneInfo? _browserZone;

    public static IReadOnlyList<string> AvailableIds { get; } =
        TimeZoneInfo
            .GetSystemTimeZones()
            .Select(zone => AsIanaId(zone.Id))
            .Append("UTC")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.Ordinal)
            .ToArray();

    /// <summary>True once the browser was asked, whether or not it gave a usable zone.</summary>
    public bool Resolved { get; private set; }

    public async Task InitializeAsync(IJSRuntime js)
    {
        try
        {
            var info = await js.InvokeAsync<BrowserTimeZone>("quasarConfigs.getTimeZone");
            BrowserId = info?.Id;
            _browserZone = Resolve(info?.Id, info?.OffsetMinutes);
            Zone = _browserZone;
            if (Resolve(info?.PreferredId, null) is { } preferred)
            {
                PreferredId = AsIanaId(info!.PreferredId!);
                Zone = preferred;
            }
        }
        catch (JSException) { }
        catch (JSDisconnectedException) { }
        catch (TaskCanceledException) { }
        Resolved = true;
    }

    public async Task<bool> TrySetPreferenceAsync(IJSRuntime js, string id)
    {
        id = id.Trim();
        var preferred = Resolve(id, null);
        if (id.Length != 0 && preferred is null)
            return false;

        var ianaId = preferred is null ? null : AsIanaId(id);
        await js.InvokeVoidAsync("quasarConfigs.setLocalStorage", StorageKey, ianaId ?? "");
        PreferredId = ianaId;
        Zone = preferred ?? _browserZone;
        return true;
    }

    /// <summary>
    /// Formats a timestamp in the viewer's culture and selected zone for display.
    /// Logs, configs and API payloads must not use this formatter.
    /// </summary>
    public string Format(DateTimeOffset value) => Format(value, Zone);

    /// <summary>
    /// Formats a timestamp as a date line and a time line for dense tables.
    /// The visible text follows the viewer's culture; machine-readable output
    /// keeps the invariant pattern.
    /// </summary>
    public string FormatStacked(DateTimeOffset value) => FormatStacked(value, Zone);

    public string Format(DateTimeOffset? value, string missing) =>
        value is { } time ? Format(time) : missing;

    /// <summary>A <see cref="DateTimeKind.Unspecified"/> value is taken as UTC.</summary>
    public string Format(DateTime value) => Format(AsOffset(value));

    public string FormatTicks(long ticksUtc) => Format(new DateTimeOffset(ticksUtc, TimeSpan.Zero));

    /// <summary>Wall-clock time of <paramref name="value"/> in the viewer's zone, for date and time inputs.</summary>
    public DateTime ToViewer(DateTimeOffset value) => ToWallClock(value, Zone);

    internal DateTime ToBrowser(DateTimeOffset value) => ToWallClock(value, _browserZone);

    private static DateTime ToWallClock(DateTimeOffset value, TimeZoneInfo? zone) =>
        zone is null ? value.UtcDateTime : TimeZoneInfo.ConvertTime(value, zone).DateTime;

    /// <summary>Converts wall-clock time entered in the UI to a UTC instant.</summary>
    public DateTimeOffset FromViewer(DateTime wallClock) => ToUtc(wallClock, Zone);

    internal DateTimeOffset FromBrowser(DateTime wallClock) => ToUtc(wallClock, _browserZone);

    private static DateTimeOffset ToUtc(DateTime wallClock, TimeZoneInfo? zone)
    {
        var unspecified = DateTime.SpecifyKind(wallClock, DateTimeKind.Unspecified);
        return zone is null
            ? new DateTimeOffset(unspecified, TimeSpan.Zero)
            : new DateTimeOffset(unspecified, zone.GetUtcOffset(unspecified)).ToUniversalTime();
    }

    internal static string Format(DateTimeOffset value, TimeZoneInfo? zone)
    {
        var local = zone is null ? value.UtcDateTime : TimeZoneInfo.ConvertTime(value, zone).DateTime;
        return local.ToString(DisplayFormat, CultureInfo.CurrentCulture) + (zone is null ? "\u00A0UTC" : "");
    }

    internal static string FormatStacked(DateTimeOffset value, TimeZoneInfo? zone)
    {
        var local = zone is null ? value.UtcDateTime : TimeZoneInfo.ConvertTime(value, zone).DateTime;
        var text = local.ToString(DisplayDatePattern, CultureInfo.CurrentCulture)
            + "\n"
            + local.ToString(DisplayTimePattern, CultureInfo.CurrentCulture);
        return text + (zone is null ? "\u00A0UTC" : "");
    }

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
        if (offsetMinutes is not { } minutes || minutes is < -840 or > 840)
            return null;
        var offset = TimeSpan.FromMinutes(minutes);
        var name = $"UTC{(minutes < 0 ? "-" : "+")}{offset:hh\\:mm}";
        return TimeZoneInfo.CreateCustomTimeZone(name, offset, name, name);
    }

    private static DateTimeOffset AsOffset(DateTime value) =>
        value.Kind == DateTimeKind.Local
            ? new DateTimeOffset(value)
            : new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private static string AsIanaId(string id) =>
        TimeZoneInfo.TryConvertWindowsIdToIanaId(id, out var ianaId) ? ianaId : id;

    internal sealed record BrowserTimeZone(string? Id, int? OffsetMinutes, string? PreferredId);
}
