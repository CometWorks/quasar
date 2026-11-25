namespace Quasar.Ui;

/// <summary>
/// A toast entry tracked by <see cref="IQuasarToastService"/>.
/// </summary>
/// <remarks>
/// The record itself is immutable. <see cref="Options"/> is a snapshot copied
/// when the toast was added; treat it as read-only.
/// </remarks>
/// <param name="Id">Stable toast identifier.</param>
/// <param name="Message">Plain-text message.</param>
/// <param name="Severity">Severity used for color, icon, and accessibility role.</param>
/// <param name="Options">Per-toast options.</param>
/// <param name="CreatedAt">Creation time from the host's time provider.</param>
public sealed record QuasarToast(
    Guid Id,
    string Message,
    QuasarSeverity Severity,
    QuasarToastOptions Options,
    DateTimeOffset CreatedAt);
