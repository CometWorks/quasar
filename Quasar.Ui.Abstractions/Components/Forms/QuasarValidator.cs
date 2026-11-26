namespace Quasar.Ui.Components;

/// <summary>
/// Validates a field value asynchronously and returns zero or more messages.
/// </summary>
/// <typeparam name="T">The bound value type.</typeparam>
/// <param name="value">The current value.</param>
/// <param name="cancellationToken">
/// Cancelled when the value changes, a newer validation starts, or the field is
/// disposed.
/// </param>
public delegate Task<QuasarValidationResult> QuasarValidator<T>(
    T value,
    CancellationToken cancellationToken);
