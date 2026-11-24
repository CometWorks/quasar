namespace Quasar.Ui;

/// <summary>
/// Result returned when a dialog closes.
/// </summary>
/// <remarks>
/// <para>
/// A dialog may close without an explicit result (Escape, backdrop click,
/// navigation, or the dialog host being disposed). Those paths produce a
/// canceled result, so callers should always check <see cref="Canceled"/>
/// before using <see cref="Data"/>.
/// </para>
/// <para>
/// Use <see cref="Ok{T}"/> with a payload for data the caller needs, and
/// <see cref="Ok()"/> for plain confirmation. <see cref="TryGetData{T}"/> is
/// the safe way to read a typed payload.
/// </para>
/// </remarks>
/// <param name="Canceled">Whether the dialog was canceled or dismissed.</param>
/// <param name="Data">The optional result payload.</param>
public sealed record QuasarDialogResult(bool Canceled, object? Data)
{
    /// <summary>Creates a successful result with a payload.</summary>
    public static QuasarDialogResult Ok<T>(T data) => new(false, data);

    /// <summary>Creates a successful result without a payload.</summary>
    public static QuasarDialogResult Ok() => new(false, null);

    /// <summary>Creates a canceled result.</summary>
    public static QuasarDialogResult Cancel() => new(true, null);

    /// <summary>Tries to read a typed payload from a successful result.</summary>
    /// <typeparam name="T">Expected payload type.</typeparam>
    /// <param name="value">The payload when the result is successful and typed correctly.</param>
    /// <returns>
    /// <c language="csharp">true</c> only when the dialog was not canceled and the payload is
    /// assignable to <typeparamref name="T"/>; otherwise <c language="csharp">false</c>.
    /// </returns>
    public bool TryGetData<T>(out T value)
    {
        if (!Canceled && Data is T typed)
        {
            value = typed;
            return true;
        }

        value = default!;
        return false;
    }
}
