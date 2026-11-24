namespace Quasar.Ui;

/// <summary>
/// Per-toast behavior and appearance overrides.
/// </summary>
/// <remarks>
/// Configure these through the callback passed to
/// <see cref="IQuasarToastService.Add"/>. The callback runs before the toast is
/// added, so changes are visible immediately.
/// </remarks>
public sealed class QuasarToastOptions
{
    /// <summary>
    /// Time before auto-dismiss. Defaults to five seconds. A zero or negative
    /// value keeps the toast visible; values above 24 hours are clamped.
    /// </summary>
    public TimeSpan Duration { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Keeps the toast visible until the user dismisses it, ignoring
    /// <see cref="Duration"/>. Defaults to <c language="csharp">false</c>.
    /// </summary>
    /// <remarks>
    /// Use this for errors and security notices that must not disappear before
    /// the user reads them. The close button remains available unless
    /// <see cref="Closable"/> is disabled.
    /// </remarks>
    public bool RequireInteraction { get; set; }

    /// <summary>Shows a close button. Defaults to <c language="csharp">true</c>.</summary>
    public bool Closable { get; set; } = true;

    /// <summary>Shows the severity icon. Defaults to <c language="csharp">true</c>.</summary>
    public bool ShowIcon { get; set; } = true;

    /// <summary>
    /// Overrides the severity icon with an icon-font name or a built-in Quasar
    /// icon constant.
    /// </summary>
    /// <remarks>
    /// The toast host does not trust custom SVG markup from this property. A
    /// value containing '&lt;' is rendered as text; use an icon-font name or
    /// omit the override to keep the severity icon.
    /// </remarks>
    public string? Icon { get; set; }

    /// <summary>Optional action button label.</summary>
    /// <remarks>
    /// <see cref="OnAction"/> must also be set for the button to appear.
    /// </remarks>
    public string? ActionText { get; set; }

    /// <summary>
    /// Optional action callback. It runs before the toast is removed.
    /// </summary>
    /// <remarks>
    /// An exception thrown by the callback is logged and does not prevent the
    /// toast from closing. The callback runs on the Blazor synchronization
    /// context, so it can update component state directly.
    /// </remarks>
    public Func<Task>? OnAction { get; set; }

    /// <summary>Additional class applied to the toast element.</summary>
    /// <remarks>
    /// Use it to scope plugin-specific CSS. The public <c language="css">quasar-toast</c> and
    /// severity classes are always applied. The value is trusted CSS from the
    /// plugin; do not pass untrusted input, because CSS can load external URLs
    /// and reposition content in the dashboard shell.
    /// </remarks>
    public string? Class { get; set; }

    /// <summary>
    /// Inline style applied to the toast element. Use it for per-toast CSS
    /// variables such as <c language="css">--quasar-toast-radius</c>.
    /// </summary>
    /// <remarks>
    /// The value is trusted CSS from the plugin; do not pass untrusted input,
    /// because CSS can load external URLs and reposition content in the
    /// dashboard shell.
    /// </remarks>
    public string? Style { get; set; }
}
