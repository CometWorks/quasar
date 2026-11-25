namespace Quasar.Ui;

/// <summary>
/// Controls dialog sizing, dismissal, focus, and caller-supplied styling.
/// </summary>
/// <remarks>
/// <para>
/// Every property is optional. The defaults reproduce the previous MudBlazor
/// dialog behavior: a small, modal, scroll-locking dialog that closes on
/// Escape, a backdrop click, or navigation.
/// </para>
/// <para>
/// <b>Modal versus modeless.</b> A modal dialog uses the native
/// <c language="javascript">showModal()</c> behavior: the page is inert, a backdrop is painted, focus
/// is trapped, and background scrolling is locked. Set
/// <see cref="Modal"/> to <c language="csharp">false</c> for a modeless dialog that uses native
/// <c language="javascript">show()</c>: there is no backdrop, no focus trap, no scroll lock, and the
/// page remains interactive. Modeless dialogs also get a small drag handle and
/// a resize handle by default; see <see cref="Draggable"/> and
/// <see cref="Resizable"/>.
/// </para>
/// <para>
/// <b>Caller styling.</b> <see cref="Class"/> and <see cref="Style"/> are
/// applied to the native <c language="html">&lt;dialog&gt;</c> element. Prefer the documented
/// <c language="css">--quasar-dialog-*</c> CSS variables for per-dialog theming, for example
/// <c language="csharp">Style = "--quasar-dialog-surface:#3a2f5c;--quasar-dialog-radius:22px;"</c>.
/// See <c language="text">quasar-ui.css</c> for the full token list.
/// </para>
/// </remarks>
/// <example>
/// <code language="csharp">
/// // A modeless performance panel that survives page navigation.
/// var reference = Dialogs.Show&lt;PerformancePanel&gt;("Performance",
///     options: new QuasarDialogOptions
///     {
///         Modal = false,
///         CloseOnNavigation = false,
///         MaxWidth = QuasarDialogWidth.Medium,
///         Draggable = true,
///     });
/// </code>
/// </example>
public sealed class QuasarDialogOptions
{
    /// <summary>
    /// Maximum dialog width. Defaults to <see cref="QuasarDialogWidth.Small"/>.
    /// </summary>
    /// <remarks>
    /// The dialog is also limited by the viewport. Widths map to the same
    /// values used by the previous MudBlazor dialogs.
    /// </remarks>
    public QuasarDialogWidth MaxWidth { get; init; } = QuasarDialogWidth.Small;

    /// <summary>
    /// Uses the available width up to <see cref="MaxWidth"/>. Defaults to
    /// <c language="csharp">false</c> (the dialog sizes to its content within the maximum).
    /// </summary>
    public bool FullWidth { get; init; }

    /// <summary>
    /// Allows Escape to close the dialog. Defaults to <c language="csharp">true</c>.
    /// </summary>
    /// <remarks>
    /// When <c language="csharp">false</c>, the Escape key is prevented for both modal and
    /// modeless dialogs. The close button and footer actions still work.
    /// </remarks>
    public bool CloseOnEscapeKey { get; init; } = true;

    /// <summary>
    /// Allows a backdrop click to close the dialog. Ignored for modeless
    /// dialogs. Defaults to <c language="csharp">true</c>.
    /// </summary>
    /// <remarks>
    /// Only a press and release that both start outside the dialog closes it,
    /// so dragging text from inside the dialog to the page does not dismiss it.
    /// </remarks>
    public bool BackdropClick { get; init; } = true;

    /// <summary>
    /// Locks page scrolling while a modal dialog is open. Ignored for modeless
    /// dialogs. Defaults to <c language="csharp">true</c>.
    /// </summary>
    /// <remarks>
    /// Scroll locks are reference-counted, so closing one of several stacked
    /// dialogs keeps the page locked until the last modal dialog closes.
    /// </remarks>
    public bool LockScroll { get; init; } = true;

    /// <summary>
    /// Keeps the dialog modal: the page is inert and a backdrop is shown.
    /// Set to <c language="csharp">false</c> for a modeless dialog that uses the native
    /// <c language="javascript">show()</c> behavior and leaves the page interactive. Defaults to
    /// <c language="csharp">true</c>.
    /// </summary>
    /// <remarks>
    /// Modeless dialogs ignore <see cref="BackdropClick"/> and
    /// <see cref="LockScroll"/> because neither concept applies without a
    /// backdrop or a page lock. They close on Escape only while the dialog
    /// itself has focus.
    /// </remarks>
    public bool Modal { get; init; } = true;

    /// <summary>
    /// Shows a small drag handle at the top of modeless dialogs. Ignored for
    /// modal dialogs. Defaults to <c language="csharp">true</c>.
    /// </summary>
    /// <remarks>
    /// The handle supports pointer dragging and the arrow keys when focused
    /// (hold Shift for single-pixel steps). The dialog stays inside the
    /// viewport. Set to <c language="csharp">false</c> to remove the handle and leave the dialog
    /// at its initial centered position.
    /// </remarks>
    public bool Draggable { get; init; } = true;

    /// <summary>
    /// Shows a resize handle in the bottom-right corner of modeless dialogs.
    /// Ignored for modal dialogs. Defaults to <c language="csharp">true</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The handle supports pointer resizing and the arrow keys when focused
    /// (hold Shift for single-pixel steps). The dialog keeps its top-left
    /// corner fixed and is clamped to a minimum size and to the viewport.
    /// Resizing changes the dialog's outer width and height; the content
    /// region continues to scroll inside it.
    /// </para>
    /// <para>
    /// Set to <c language="csharp">false</c> for a fixed-size modeless dialog. This is useful for
    /// small progress or status panels whose layout should not change.
    /// </para>
    /// </remarks>
    public bool Resizable { get; init; } = true;

    /// <summary>
    /// Closes the dialog when the application navigates. Defaults to <c language="csharp">true</c>.
    /// </summary>
    /// <remarks>
    /// Set to <c language="csharp">false</c> on a modeless dialog to keep it open across page
    /// changes. The dialog host lives in the application shell, so the dialog
    /// component survives navigation even though the page that opened it is
    /// disposed. Pass IDs or immutable data to the dialog, not page-local
    /// objects that may become stale.
    /// </remarks>
    public bool CloseOnNavigation { get; init; } = true;

    /// <summary>
    /// Initial focus behavior. The native default is the first focusable element.
    /// </summary>
    public QuasarDialogFocus DefaultFocus { get; init; } = QuasarDialogFocus.FirstFocusable;

    /// <summary>
    /// Accessible name. Falls back to the title passed to
    /// <see cref="IQuasarDialogService.ShowAsync{TComponent}"/>, then to
    /// <c language="csharp">"Quasar dialog"</c>.
    /// </summary>
    /// <remarks>
    /// Set this when the visible title is rendered inside the dialog content
    /// and the native element would otherwise have no accessible name.
    /// </remarks>
    public string? AriaLabel { get; init; }

    /// <summary>
    /// Additional class applied to the native dialog element.
    /// </summary>
    /// <remarks>
    /// Use it to scope plugin-specific CSS. The public <c language="css">quasar-dialog</c>
    /// class and width classes are always applied. The value is trusted CSS
    /// from the plugin; do not pass untrusted input. CSS can load external
    /// URLs and reposition content in the dashboard shell.
    /// </remarks>
    public string? Class { get; init; }

    /// <summary>
    /// Inline style applied to the native dialog element. Use it for
    /// dialog-specific CSS variables.
    /// </summary>
    /// <remarks>
    /// The dialog host also writes <c language="css">--quasar-dialog-max-width</c> from
    /// <see cref="MaxWidth"/>; a value supplied here is appended afterwards and
    /// therefore wins. The value is trusted CSS from the plugin; do not pass
    /// untrusted input, because CSS can load external URLs and reposition
    /// content in the dashboard shell.
    /// </remarks>
    public string? Style { get; init; }
}
