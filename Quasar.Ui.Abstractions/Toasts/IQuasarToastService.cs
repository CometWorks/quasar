namespace Quasar.Ui;

/// <summary>
/// Shows transient Quasar toasts.
/// </summary>
/// <remarks>
/// <para>
/// The implementation lives in <c language="csharp">Quasar.Client</c>. UI plugins can inject this
/// interface when the plugin's browser project references the
/// <c language="csharp">Quasar.Ui.Abstractions</c> assembly supplied by the running Quasar worker.
/// The host renders plain HTML and CSS; a plugin does not need MudBlazor or a
/// reference to <c language="csharp">Quasar.Client</c>.
/// </para>
/// <para>
/// The service is scoped to the browser application, so toasts survive page
/// navigation and are shared by every page and dialog in the shell. It is safe
/// to call from Blazor event handlers and from background tasks; the service
/// uses a lock internally and raises <see cref="Changed"/> outside the lock.
/// </para>
/// <para>
/// Each toast auto-dismisses after <see cref="QuasarToastOptions.Duration"/>
/// unless <see cref="QuasarToastOptions.RequireInteraction"/> is set. The host
/// pauses the timer while the pointer or keyboard focus is inside the toast,
/// then resumes with the remaining time. Setting the duration to zero or a
/// negative value disables the timer as well.
/// </para>
/// </remarks>
/// <example>
/// <code language="csharp">
/// Toasts.Add("Host restarted.", QuasarSeverity.Success);
///
/// Toasts.Add("Config validation failed.", QuasarSeverity.Error, options =&gt;
/// {
///     options.RequireInteraction = true;
///     options.ActionText = "Open log";
///     options.OnAction = () =&gt;
///     {
///         Navigation.NavigateTo("/logs");
///         return Task.CompletedTask;
///     };
/// });
/// </code>
/// </example>
public interface IQuasarToastService
{
    /// <summary>
    /// Adds a toast and returns its identifier.
    /// </summary>
    /// <param name="message">
    /// Plain-text message. It is rendered as text, not HTML; messages longer
    /// than 1000 characters are truncated.
    /// </param>
    /// <param name="severity">Severity used for color, icon, and accessible role.</param>
    /// <param name="configure">Optional callback that customizes the new toast before it is shown.</param>
    /// <returns>The toast identifier, usable with <see cref="Remove"/>.</returns>
    /// <remarks>
    /// Adding more toasts than <c language="csharp">QuasarToastHostOptions.MaxVisibleToasts</c>
    /// removes the oldest tracked toast, including its timer.
    /// </remarks>
    Guid Add(string message, QuasarSeverity severity = QuasarSeverity.Normal,
        Action<QuasarToastOptions>? configure = null);

    /// <summary>Removes a toast by identifier. Returns whether it was present.</summary>
    bool Remove(Guid id);

    /// <summary>Removes every toast and cancels their timers.</summary>
    void Clear();

    /// <summary>
    /// A snapshot of the currently tracked toasts, oldest first. The host
    /// applies the display limit and ordering. Treat the returned toast
    /// options as read-only.
    /// </summary>
    IReadOnlyList<QuasarToast> Snapshot { get; }

    /// <summary>
    /// Raised after the tracked toast set changes. The host subscribes to
    /// re-render; plugins normally do not need to handle it.
    /// </summary>
    event Action? Changed;
}
