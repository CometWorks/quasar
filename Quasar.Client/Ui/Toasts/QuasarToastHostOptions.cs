namespace Quasar.Ui;

/// <summary>
/// Global toast placement and retention settings.
/// </summary>
/// <remarks>
/// Configure these once through <c>AddQuasarUi(options =&gt; ...)</c>. They
/// affect every toast in the browser application.
/// </remarks>
public sealed class QuasarToastHostOptions
{
    /// <summary>Screen corner used by the toast host. Defaults to top-right.</summary>
    public QuasarToastPosition Position { get; set; } = QuasarToastPosition.TopRight;

    /// <summary>
    /// Maximum number of toasts kept at once. Defaults to five. Adding another
    /// toast removes the oldest tracked toast.
    /// </summary>
    public int MaxVisibleToasts { get; set; } = 5;

    /// <summary>
    /// Shows the newest toast at the top of the stack. Defaults to <c>true</c>.
    /// Set to <c>false</c> for a chronological top-to-bottom stack.
    /// </summary>
    public bool NewestOnTop { get; set; } = true;

    /// <summary>
    /// Clears every toast when the application navigates. Defaults to
    /// <c>false</c>, so a toast raised by a background task survives a page
    /// change.
    /// </summary>
    public bool ClearAfterNavigation { get; set; }
}

/// <summary>
/// Screen corner used by the native toast host.
/// </summary>
/// <remarks>
/// Top positions are offset below the dashboard top bar. On narrow screens the
/// host spans the viewport minus a small gutter.
/// </remarks>
public enum QuasarToastPosition
{
    /// <summary>Top-left corner, below the top bar.</summary>
    TopLeft,

    /// <summary>Top center, below the top bar.</summary>
    TopCenter,

    /// <summary>Top-right corner, below the top bar.</summary>
    TopRight,

    /// <summary>Bottom-left corner.</summary>
    BottomLeft,

    /// <summary>Bottom center.</summary>
    BottomCenter,

    /// <summary>Bottom-right corner.</summary>
    BottomRight,
}
