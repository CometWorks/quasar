namespace Quasar.Ui;

/// <summary>
/// Supported dialog widths, matching the values used by the previous MudBlazor
/// dialogs during the UI migration.
/// </summary>
/// <remarks>
/// The width is a maximum. A dialog never exceeds the viewport, and
/// <see cref="QuasarDialogOptions.FullWidth"/> makes it use all available
/// space up to that maximum. For a one-off width, set
/// <c language="css">--quasar-dialog-max-width</c> through <see cref="QuasarDialogOptions.Style"/>.
/// </remarks>
public enum QuasarDialogWidth
{
    /// <summary>444 CSS pixels.</summary>
    ExtraSmall,

    /// <summary>600 CSS pixels.</summary>
    Small,

    /// <summary>960 CSS pixels.</summary>
    Medium,

    /// <summary>1280 CSS pixels.</summary>
    Large,

    /// <summary>1920 CSS pixels.</summary>
    ExtraLarge,
}
