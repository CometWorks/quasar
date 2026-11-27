namespace Quasar.Ui.Components;

/// <summary>Page, size and sort state passed to <see cref="QuasarTable{T}.LoadData"/>.</summary>
/// <param name="Page">One-based page number.</param>
/// <param name="PageSize">Rows per page.</param>
/// <param name="SortKey">Active column key, or null when unsorted.</param>
/// <param name="Descending">True for descending order.</param>
public readonly record struct QuasarTableState(
    int Page,
    int PageSize,
    string? SortKey,
    bool Descending);
