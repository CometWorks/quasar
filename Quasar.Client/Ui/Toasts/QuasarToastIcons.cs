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
        QuasarSeverity.Info => Quasar.Ui.Components.QuasarIcons.Info,
        QuasarSeverity.Success => Quasar.Ui.Components.QuasarIcons.CheckCircle,
        QuasarSeverity.Warning => Quasar.Ui.Components.QuasarIcons.ReportProblem,
        QuasarSeverity.Error => Quasar.Ui.Components.QuasarIcons.ErrorOutline,
        _ => Quasar.Ui.Components.QuasarIcons.EventNote,
    };
}
