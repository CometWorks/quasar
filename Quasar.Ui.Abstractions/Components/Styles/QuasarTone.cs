namespace Quasar.Ui;

/// <summary>
/// Semantic colour role shared by native Quasar controls. A tone sets
/// <c language="css">--quasar-control-color</c> and
/// <c language="css">--quasar-control-text</c> from the dashboard palette.
/// </summary>
public enum QuasarTone
{
    /// <summary>Default surface, border and text colours.</summary>
    Default,

    /// <summary>The primary accent colour.</summary>
    Primary,

    /// <summary>The secondary accent colour.</summary>
    Secondary,

    /// <summary>Informational accent colour.</summary>
    Info,

    /// <summary>Success accent colour.</summary>
    Success,

    /// <summary>Warning accent colour.</summary>
    Warning,

    /// <summary>Error accent colour.</summary>
    Error,
}
