namespace Quasar.Services.Updates;

public sealed record UpdateNotice(string Key, string Title, string Body, string Url, DateTimeOffset? OccurredAt = null,
    string Severity = "info")
{
    internal const string AnnouncementKeyPrefix = "announcement:";
    public bool IsAnnouncement => Key.StartsWith(AnnouncementKeyPrefix, StringComparison.Ordinal);
    public string Tone => Severity == "critical" ? "error" : Severity;
}
