namespace Quasar.Ui;

/// <summary>
/// Maps the shared control vocabulary to the CSS classes defined in
/// <c language="text">quasar-ui.css</c>.
/// </summary>
public static class QuasarClasses
{
    /// <summary>Returns the tone class, or <see langword="null"/> for the default tone.</summary>
    public static string? Tone(QuasarTone tone) => tone switch
    {
        QuasarTone.Primary => "quasar-tone-primary",
        QuasarTone.Secondary => "quasar-tone-secondary",
        QuasarTone.Info => "quasar-tone-info",
        QuasarTone.Success => "quasar-tone-success",
        QuasarTone.Warning => "quasar-tone-warning",
        QuasarTone.Error => "quasar-tone-error",
        _ => null,
    };

    /// <summary>Returns the variant class, or <see langword="null"/> for the outlined default.</summary>
    public static string? Variant(QuasarVariant variant) => variant switch
    {
        QuasarVariant.Text => "quasar-variant-text",
        QuasarVariant.Filled => "quasar-variant-filled",
        _ => null,
    };

    /// <summary>Returns the size class, or <see langword="null"/> for the medium default.</summary>
    public static string? Size(QuasarSize size) => size switch
    {
        QuasarSize.Small => "quasar-size-small",
        QuasarSize.Large => "quasar-size-large",
        _ => null,
    };
}
