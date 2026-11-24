namespace Quasar.Ui;

/// <summary>
/// Maps a <see cref="QuasarSeverity"/> to the shared Quasar icon set used by the
/// native toast host.
/// </summary>
/// <remarks>
/// Severity color comes from CSS (<c>--quasar-toast-accent</c>), not from this
/// class. <see cref="QuasarToastOptions.Icon"/> overrides the result.
/// </remarks>
internal static class QuasarToastIcons
{
    /// <summary>Returns the default SVG path for <paramref name="severity"/>.</summary>
    public static string For(QuasarSeverity severity) => severity switch
    {
        QuasarSeverity.Info => Quasar.Components.Shared.QuasarIcons.Info,
        QuasarSeverity.Success => Quasar.Components.Shared.QuasarIcons.CheckCircle,
        QuasarSeverity.Warning => Quasar.Components.Shared.QuasarIcons.ReportProblem,
        QuasarSeverity.Error => Quasar.Components.Shared.QuasarIcons.ErrorOutline,
        _ => Quasar.Components.Shared.QuasarIcons.EventNote,
    };
}
