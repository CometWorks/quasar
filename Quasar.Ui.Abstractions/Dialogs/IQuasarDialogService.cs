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
    /// requested through <see cref="QuasarDialogOptions.Modal"/>.
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
    /// An already-cancelled token returns a canceled reference immediately.
    /// The token is not observed after the dialog has been queued.
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
    /// </remarks>
    Task<IQuasarDialogReference> ShowAsync(
        Type componentType,
        string? title = null,
        QuasarDialogParameters? parameters = null,
        QuasarDialogOptions? options = null,
        CancellationToken cancellationToken = default);
}
