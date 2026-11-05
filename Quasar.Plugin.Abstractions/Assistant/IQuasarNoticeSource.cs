namespace Quasar.Plugin.Abstractions.Assistant;

/// <summary>
/// Adds notices to the web push notifications and the notification list. Register implementations in DI.
/// Core asks every source per signed-in account and sends each key once per browser.
/// </summary>
public interface IQuasarNoticeSource
{
    /// <param name="provider">The account's sign-in provider, such as <c>Steam</c>.</param>
    /// <param name="subject">The account's subject, such as a SteamID64.</param>
    IEnumerable<QuasarNotice> GetNotices(string provider, string subject);
}

/// <param name="Key">Stable per notice: a browser gets each key once. At most 256 characters.</param>
/// <param name="Url">A path inside Quasar, such as <c>/assistant/approvals</c>.</param>
/// <param name="Severity"><c>info</c>, <c>warning</c> or <c>critical</c>.</param>
public sealed record QuasarNotice(
    string Key,
    string Title,
    string Body,
    string Url,
    DateTimeOffset? Time = null,
    string Severity = "info"
);
