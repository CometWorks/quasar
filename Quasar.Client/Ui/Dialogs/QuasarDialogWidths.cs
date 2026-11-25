using Quasar.Ui;

namespace Quasar.Client.Ui.Dialogs;

/// <summary>
/// Maps the public <see cref="QuasarDialogWidth"/> enum to the CSS max-width
/// variable and to a width class on the native dialog element.
/// </summary>
/// <remarks>
/// The class exists so plugin CSS can target a width without relying on the
/// inline variable, while the variable gives the dialog its actual size. The
/// values intentionally match the previous MudBlazor widths.
/// </remarks>
internal static class QuasarDialogWidths
{
    /// <summary>Returns the CSS length written to <c>--quasar-dialog-max-width</c>.</summary>
    public static string ToCss(QuasarDialogWidth width) => width switch
    {
        QuasarDialogWidth.ExtraSmall => "444px",
        QuasarDialogWidth.Small => "600px",
        QuasarDialogWidth.Medium => "960px",
        QuasarDialogWidth.Large => "1280px",
        QuasarDialogWidth.ExtraLarge => "1920px",
        _ => "600px",
    };

    /// <summary>Returns the public width class applied to the dialog element.</summary>
    public static string ToClass(QuasarDialogWidth width) => width switch
    {
        QuasarDialogWidth.ExtraSmall => "quasar-dialog-width-xs",
        QuasarDialogWidth.Small => "quasar-dialog-width-sm",
        QuasarDialogWidth.Medium => "quasar-dialog-width-md",
        QuasarDialogWidth.Large => "quasar-dialog-width-lg",
        QuasarDialogWidth.ExtraLarge => "quasar-dialog-width-xl",
        _ => "quasar-dialog-width-sm",
    };
}
