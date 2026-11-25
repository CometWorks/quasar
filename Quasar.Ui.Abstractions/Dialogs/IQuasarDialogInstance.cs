namespace Quasar.Ui;

/// <summary>
/// Cascaded to the component rendered inside a Quasar dialog.
/// </summary>
/// <remarks>
/// <para>
/// A dialog component receives this instance through a
/// <c language="csharp">[CascadingParameter]</c>. It exposes the dialog title and lets the
/// component close itself without holding a reference to the dialog host.
/// </para>
/// <para>
/// This is the same dialog as the caller's <see cref="IQuasarDialogReference"/>,
/// but the component does not own the result task. The caller awaits
/// <see cref="IQuasarDialogReference.Result"/>; the component only requests
/// closure. Use <see cref="QuasarDialogResult.Ok{T}"/> with a payload when the
/// caller needs data back.
/// </para>
/// </remarks>
/// <example>
/// <code language="csharp">
/// [CascadingParameter]
/// private IQuasarDialogInstance Dialog { get; set; } = default!;
///
/// private void Confirm() =&gt;
///     Dialog.Close(QuasarDialogResult.Ok(new HostSelection(hostId)));
/// </code>
/// </example>
public interface IQuasarDialogInstance
{
    /// <summary>Stable dialog identifier, shared with the caller's reference.</summary>
    Guid Id { get; }

    /// <summary>
    /// The title supplied when the dialog was shown. It is <c language="csharp">null</c> when
    /// the caller passed no title. <see cref="QuasarDialogOptions.AriaLabel"/>
    /// is an accessibility fallback, not a visible title, and is not returned.
    /// </summary>
    string? Title { get; }

    /// <summary>
    /// Requests that the dialog close with the supplied result. Passing
    /// <c language="csharp">null</c> is equivalent to <see cref="Cancel"/>.
    /// </summary>
    void Close(QuasarDialogResult? result = null);

    /// <summary>Requests that the dialog close with a canceled result.</summary>
    void Cancel();
}
