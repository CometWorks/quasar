namespace Quasar.Ui;

/// <summary>
/// Toast and notice severity. It maps to the dashboard palette and to an accessible
/// <c language="text">status</c> or <c language="text">alert</c> role.
/// </summary>
/// <remarks>
/// <see cref="Warning"/> and <see cref="Error"/> render with <c language="html">role="alert"</c>
/// so assistive technology announces them immediately. The other severities use
/// <c language="html">role="status"</c>, which is announced politely.
/// </remarks>
public enum QuasarSeverity
{
    /// <summary>A neutral notification.</summary>
    Normal,

    /// <summary>Informational notification.</summary>
    Info,

    /// <summary>Successful completion.</summary>
    Success,

    /// <summary>Warning that needs attention.</summary>
    Warning,

    /// <summary>Failure that needs attention.</summary>
    Error,
}
