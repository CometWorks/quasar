namespace Quasar.Ui.Components;

/// <summary>What a <see cref="QuasarTable{T}"/> does on a narrow screen.</summary>
public enum QuasarTableNarrowLayout
{
    /// <summary>Keeps the columns and scrolls the table sideways inside its box.</summary>
    Scroll,

    /// <summary>
    /// Shows each row as a card of label and value lines. Give each cell a label
    /// with <see cref="QuasarTableCell.Label"/>.
    /// </summary>
    Stack,
}

/// <summary>The screen width below which a stacked table turns into cards.</summary>
public enum QuasarBreakpoint
{
    /// <summary>Below 600 px, typically phones.</summary>
    Small,

    /// <summary>Below 960 px, typically phones and small tablets.</summary>
    Medium,

    /// <summary>Below 1280 px, anything narrower than a wide laptop.</summary>
    Large,
}
