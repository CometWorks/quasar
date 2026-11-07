using System.Security.Claims;

namespace Quasar.Plugin.Abstractions.Notifications;

/// <summary>
/// Adds notices to the web push notifications and the notification list. Register implementations in DI.
/// Core asks every source per signed-in account and sends each key once per browser.
/// </summary>
public interface IQuasarNoticeSource
{
    /// <param name="subscriber">The account with its current roles, rebuilt on every push sweep,
    /// so policies can be checked on it with <c>IAuthorizationService</c>.</param>
    ValueTask<IReadOnlyList<QuasarNotice>> GetNoticesAsync(
        ClaimsPrincipal subscriber,
        CancellationToken token
    );
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
