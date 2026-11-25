using Quasar.Ui;

namespace Quasar.Client.Ui.Dialogs;

/// <summary>
/// One open dialog tracked by <see cref="QuasarDialogService"/>.
/// </summary>
/// <remarks>
/// The entry is the mutable server-side model for a native dialog element. It
/// is created when the caller shows the dialog and removed after the browser
/// reports the native close event. All members except the requested-result
/// handshake are read-only after creation.
/// </remarks>
public sealed class QuasarDialogEntry
{
    /// <summary>Stable dialog identifier, also used as the Blazor render key.</summary>
    public required Guid Id { get; init; }

    /// <summary>Concrete Blazor component type rendered inside the dialog.</summary>
    public required Type ComponentType { get; init; }

    /// <summary>The caller's parameter set, kept for diagnostics and equality.</summary>
    public required QuasarDialogParameters Parameters { get; init; }

    /// <summary>
    /// Mutable copy of the parameters in the shape required by
    /// <c>DynamicComponent</c>. Keys are case-insensitive to match Blazor's
    /// parameter binding.
    /// </summary>
    public required Dictionary<string, object> ComponentParameters { get; init; }

    /// <summary>Behavior and appearance overrides supplied by the caller.</summary>
    public required QuasarDialogOptions Options { get; init; }

    /// <summary>Trimmed accessible title, or <c>null</c> when none was supplied.</summary>
    public required string? Title { get; init; }

    /// <summary>The caller-facing handle that owns the result task.</summary>
    internal QuasarDialogReference Reference { get; init; } = null!;

    /// <summary>
    /// Owns the caller's cancellation registration so it can be released when
    /// the dialog finishes closing.
    /// </summary>
    internal QuasarDialogCancellation Cancellation { get; } = new();

    /// <summary>The instance cascaded to the dialog component.</summary>
    public IQuasarDialogInstance Instance { get; set; } = null!;

    /// <summary>
    /// Result recorded by the first close request. It is used when the native
    /// dialog finishes closing. A <c>null</c> value means "cancel".
    /// </summary>
    internal QuasarDialogResult? RequestedResult { get; set; }

    /// <summary>
    /// Whether a close has been requested. The host uses it to send exactly one
    /// close instruction to JavaScript.
    /// </summary>
    public bool CloseRequested { get; set; }

    /// <summary>
    /// Accessible name for the native dialog: explicit ARIA label first, then
    /// the trimmed title, then a generic fallback.
    /// </summary>
    public string AriaLabel
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Options.AriaLabel))
                return Options.AriaLabel!;
            return string.IsNullOrWhiteSpace(Title) ? "Quasar dialog" : Title!;
        }
    }
}

/// <summary>
/// Caller-facing dialog handle. It forwards close requests to the service and
/// owns the single result task for the dialog.
/// </summary>
internal sealed class QuasarDialogReference(Guid id) : IQuasarDialogReference
{
    // RunContinuationsAsynchronously prevents a caller's continuation from
    // running inside the service lock while the entry is being removed.
    private readonly TaskCompletionSource<QuasarDialogResult> _result =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Set by the service when the entry is created. It routes close requests
    /// back through the service's two-phase close.
    /// </summary>
    internal Action<QuasarDialogResult?>? CloseRequested { get; set; }

    public Guid Id { get; } = id;

    public Task<QuasarDialogResult> Result => _result.Task;

    public void Close(QuasarDialogResult? result = null) =>
        CloseRequested?.Invoke(result ?? QuasarDialogResult.Cancel());

    public void Cancel() => CloseRequested?.Invoke(QuasarDialogResult.Cancel());

    /// <summary>
    /// Completes the result exactly once. Returns <c>false</c> when another
    /// path already completed it.
    /// </summary>
    internal bool Complete(QuasarDialogResult result) => _result.TrySetResult(result);
}

/// <summary>
/// Dialog instance cascaded to the component rendered inside the dialog.
/// </summary>
/// <remarks>
/// This is a thin adapter over <see cref="QuasarDialogEntry"/> so the dialog
/// component never sees the service or the caller's reference.
/// </remarks>
internal sealed class QuasarDialogInstance(QuasarDialogEntry entry) : IQuasarDialogInstance
{
    public Guid Id => entry.Id;

    public string? Title => entry.Title;

    public void Close(QuasarDialogResult? result = null) => entry.Reference.Close(result);

    public void Cancel() => entry.Reference.Cancel();
}
