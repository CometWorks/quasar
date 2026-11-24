using Microsoft.AspNetCore.Components;

namespace Quasar.Ui;

/// <summary>
/// Shows Quasar dialogs whose content is a Blazor component.
/// </summary>
/// <remarks>
/// <para>
/// The implementation lives in <c language="csharp">Quasar.Client</c>. UI plugins can inject this
/// interface when the plugin's browser project references the
/// <c language="csharp">Quasar.Ui.Abstractions</c> assembly supplied by the running Quasar worker.
/// The plugin never needs a reference to <c language="csharp">Quasar.Client</c> or MudBlazor.
/// </para>
/// <para>
/// A dialog is <em>modal</em> by default: the page is inert behind a backdrop,
/// focus is trapped, and page scrolling is locked. Set
/// <see cref="QuasarDialogOptions.Modal"/> to <c language="csharp">false</c> for a modeless
/// dialog that leaves the page interactive. Modeless dialogs get optional drag
/// and resize handles through <see cref="QuasarDialogOptions.Draggable"/> and
/// <see cref="QuasarDialogOptions.Resizable"/>. Both modes use the browser's
/// native <c language="html">&lt;dialog&gt;</c> element and report their result the same way.
/// </para>
/// <para>
/// The synchronous <see cref="Show{TComponent}"/> overloads return the
/// reference immediately; the dialog result is still asynchronous. Use them
/// from event handlers where no <c language="csharp">await</c> is needed to open the dialog.
/// <see cref="ShowAsync{TComponent}"/> is a convenience wrapper with the same
/// behavior.
/// </para>
/// <para>
/// Cancellation is observed for the dialog's whole lifetime. Cancellation before
/// the dialog is queued returns a canceled reference without rendering;
/// cancellation after it opens closes the dialog with a canceled result. The
/// <see cref="IQuasarDialogReference.Cancel"/> method remains available when no
/// token is used.
/// </para>
/// <para>
/// Quasar implements this interface. Adding members is a breaking change for
/// third-party implementations but not for callers; plugins should consume the
/// interface rather than implement it.
/// </para>
/// </remarks>
/// <example>
/// <code language="csharp">
/// // Open a modal dialog and await its result.
/// var reference = await Dialogs.ShowAsync&lt;ConfirmDialog&gt;(
///     "Delete host",
///     new QuasarDialogParameters&lt;ConfirmDialog&gt;
///     {
///         { dialog =&gt; dialog.Message, "Delete this host?" },
///     });
/// var result = await reference.Result;
/// if (!result.Canceled)
///     DeleteHost();
/// </code>
/// </example>
public interface IQuasarDialogService
{
    /// <summary>
    /// Shows a dialog for <typeparamref name="TComponent"/> and returns its
    /// reference immediately.
    /// </summary>
    /// <remarks>
    /// Use this overload when the caller does not need to await the open
    /// operation. The dialog result remains asynchronous through
    /// <see cref="IQuasarDialogReference.Result"/>. A modeless dialog is
    /// requested through <see cref="QuasarDialogOptions.Modal"/>. Cancellation
    /// before the dialog is queued returns a canceled reference; cancellation
    /// after it opens closes the dialog with a canceled result.
    /// </remarks>
    IQuasarDialogReference Show<TComponent>(
        string? title = null,
        QuasarDialogParameters? parameters = null,
        QuasarDialogOptions? options = null,
        CancellationToken cancellationToken = default)
        where TComponent : IComponent;

    /// <summary>
    /// Shows a dialog for <paramref name="componentType"/> and returns its
    /// reference immediately.
    /// </summary>
    /// <remarks>
    /// Prefer the generic overload so the component type is statically checked.
    /// This overload supports dynamic component discovery in plugin hosts.
    /// Cancellation behaves the same as the generic overload.
    /// </remarks>
    IQuasarDialogReference Show(
        Type componentType,
        string? title = null,
        QuasarDialogParameters? parameters = null,
        QuasarDialogOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Shows a dialog for <typeparamref name="TComponent"/>.
    /// </summary>
    /// <param name="title">Accessible name and default visible title.</param>
    /// <param name="parameters">Component parameters. Keys must match component parameters.</param>
    /// <param name="options">Dialog behavior and appearance overrides.</param>
    /// <param name="cancellationToken">
    /// Cancels the dialog. An already-canceled token returns a canceled
    /// reference immediately; canceling after the dialog opens closes it with a
    /// canceled result.
    /// </param>
    /// <returns>A reference whose <see cref="IQuasarDialogReference.Result"/> completes when the dialog closes.</returns>
    /// <remarks>
    /// The returned task completes as soon as the dialog is queued for
    /// rendering. It does not wait for the user to close the dialog; await
    /// <see cref="IQuasarDialogReference.Result"/> for that.
    /// </remarks>
    Task<IQuasarDialogReference> ShowAsync<TComponent>(
        string? title = null,
        QuasarDialogParameters? parameters = null,
        QuasarDialogOptions? options = null,
        CancellationToken cancellationToken = default)
        where TComponent : IComponent;

    /// <summary>
    /// Shows a dialog for <paramref name="componentType"/>.
    /// </summary>
    /// <remarks>
    /// Prefer the generic overload so the component type is statically checked.
    /// This overload supports dynamic component discovery in plugin hosts.
    /// Cancellation behaves the same as the generic overload.
    /// </remarks>
    Task<IQuasarDialogReference> ShowAsync(
        Type componentType,
        string? title = null,
        QuasarDialogParameters? parameters = null,
        QuasarDialogOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Shows a native message box and returns the user's choice.
    /// </summary>
    /// <param name="title">Accessible name and visible title. Must not be blank.</param>
    /// <param name="message">Plain-text message. It is rendered as text, not HTML.</param>
    /// <param name="yesText">Label for the primary button. The primary button is always shown; blank values fall back to <c language="csharp">OK</c>.</param>
    /// <param name="noText">Optional label for the secondary button. When omitted or blank, no button is shown.</param>
    /// <param name="cancelText">Optional label for the cancel button. When omitted or blank, no button is shown; Escape and backdrop dismissal still return <c language="csharp">null</c>.</param>
    /// <param name="options">Dialog behavior and appearance overrides. Keep <see cref="QuasarDialogOptions.Modal"/> and <see cref="QuasarDialogOptions.CloseOnNavigation"/> enabled for a blocking confirmation.</param>
    /// <param name="cancellationToken">Cancels opening the dialog or closes it if already open; the result is <c language="csharp">null</c>.</param>
    /// <returns>
    /// <c language="csharp">true</c> for Yes, <c language="csharp">false</c> for No, and <c language="csharp">null</c> for Cancel,
    /// Escape, a backdrop click, navigation, or a canceled token.
    /// </returns>
    /// <remarks>
    /// <para>
    /// This is the native equivalent of the familiar message-box call shape.
    /// It opens the same managed dialog host as
    /// <see cref="ShowAsync{TComponent}"/> and does not delegate to MudBlazor
    /// or any other dialog implementation.
    /// </para>
    /// <para>
    /// Use <see cref="ShowAsync{TComponent}"/> with a custom component when the
    /// message needs rich content, validation, or more than three choices.
    /// </para>
    /// <para>
    /// The method waits for a rendered Quasar dialog host. Calling it without a
    /// mounted <c language="csharp">QuasarDialogHost</c> leaves the returned task pending.
    /// </para>
    /// <para>
    /// This member is additive for callers. Custom implementations of this
    /// interface must implement it.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code language="csharp">
    /// var choice = await Dialogs.ShowMessageBoxAsync(
    ///     "Delete backup?",
    ///     "Permanently delete this backup?",
    ///     yesText: "Delete",
    ///     noText: "Keep",
    ///     cancelText: "Cancel");
    ///
    /// if (choice == true)
    ///     DeleteBackup();
    /// </code>
    /// </example>
    Task<bool?> ShowMessageBoxAsync(
        string title,
        string message,
        string yesText = "OK",
        string? noText = null,
        string? cancelText = null,
        QuasarDialogOptions? options = null,
        CancellationToken cancellationToken = default);
}
