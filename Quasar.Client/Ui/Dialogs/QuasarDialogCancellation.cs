namespace Quasar.Client.Ui.Dialogs;

/// <summary>
/// Owns the optional <see cref="CancellationTokenRegistration"/> for one dialog.
/// </summary>
/// <remarks>
/// The registration is created after the dialog entry is visible to the service,
/// while the token may already be canceled. Registration can therefore invoke
/// the close callback synchronously, and the entry can be removed before the
/// registration is stored. The holder makes that race harmless: a registration
/// stored after disposal is rejected so the caller can unregister it instead of
/// leaking the callback.
/// </remarks>
internal sealed class QuasarDialogCancellation : IDisposable
{
    private readonly Lock _sync = new();
    private CancellationTokenRegistration _registration;
    private bool _disposed;

    /// <summary>
    /// Stores the registration. Returns <c>false</c> when the holder has already
    /// been disposed; the caller then owns the registration and must unregister it.
    /// </summary>
    public bool TrySet(CancellationTokenRegistration registration)
    {
        lock (_sync)
        {
            if (_disposed)
                return false;

            _registration = registration;
            return true;
        }
    }

    public void Dispose()
    {
        CancellationTokenRegistration registration;
        lock (_sync)
        {
            if (_disposed)
                return;
            _disposed = true;
            registration = _registration;
            _registration = default;
        }

        // Unregister instead of Dispose: the entry is already removed from the
        // service, so there is no reason to block on a callback that may still
        // be running.
        registration.Unregister();
    }
}
