namespace Quasar.Ui.Components;

/// <summary>One item rendered by a select, radio group or similar control.</summary>
/// <typeparam name="T">The value type.</typeparam>
/// <param name="Value">The value bound when the item is selected.</param>
/// <param name="Text">Visible label.</param>
public sealed record QuasarOption<T>(T Value, string Text)
{
    /// <summary>Disables this item without disabling the whole control.</summary>
    public bool Disabled { get; init; }
}
