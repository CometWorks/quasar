namespace Quasar.Ui.Components;

/// <summary>A field registered with a <see cref="QuasarForm"/>.</summary>
/// <remarks>
/// Implemented by <see cref="QuasarFieldBase{T}"/>. Implement it directly only
/// when a custom control needs to participate in form validation and dirty
/// tracking.
/// </remarks>
public interface IQuasarField
{
    /// <summary>True when the field has no messages and is not validating.</summary>
    bool IsValid { get; }

    /// <summary>True while an asynchronous validation run is pending.</summary>
    bool IsValidating { get; }

    /// <summary>True when the current value differs from the baseline value.</summary>
    bool IsDirty { get; }

    /// <summary>Runs validation immediately and cancels any pending debounce.</summary>
    Task ValidateAsync();

    /// <summary>Records the current value as the clean baseline.</summary>
    void CaptureBaseline();
}
