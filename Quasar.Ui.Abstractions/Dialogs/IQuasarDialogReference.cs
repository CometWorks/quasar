namespace Quasar.Ui;

/// <summary>
/// Identifies an open dialog and exposes its eventual result.
/// </summary>
/// <remarks>
/// <para>
/// The reference is returned immediately by <see cref="IQuasarDialogService"/>.
/// It does not own or render the dialog component; it is the caller's handle
/// for closing the dialog and waiting for the outcome.
/// </para>
/// <para>
/// Calling <see cref="Close"/> or <see cref="Cancel"/> only requests a close.
/// The native dialog then animates or closes, and the host completes
/// <see cref="Result"/> after the browser reports the native close event. This
/// two-phase close keeps the DOM and the Blazor render tree in sync. Repeated
/// close requests are ignored, and <see cref="Result"/> completes exactly once.
/// </para>
/// <para>
/// If the application is disposed, every open dialog is completed as canceled,
/// so awaiting callers do not hang.
/// </para>
/// </remarks>
public interface IQuasarDialogReference
{
    /// <summary>Stable dialog identifier, unique within the running application.</summary>
    Guid Id { get; }

    /// <summary>
    /// Completes when the dialog closes or is canceled. The task never faults
    /// for a normal close; failures surface as a canceled result.
    /// </summary>
    Task<QuasarDialogResult> Result { get; }

    /// <summary>
    /// Requests that the dialog close with the supplied result. Passing
    /// <c language="csharp">null</c> is equivalent to <see cref="Cancel"/>.
    /// </summary>
    void Close(QuasarDialogResult? result = null);

    /// <summary>Requests that the dialog close with a canceled result.</summary>
    void Cancel();
}
