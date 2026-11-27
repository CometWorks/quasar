namespace Quasar.Ui.Components;

/// <summary>One page returned by <see cref="QuasarTable{T}.LoadData"/>.</summary>
/// <typeparam name="T">Row type.</typeparam>
/// <param name="Items">Rows on the page.</param>
/// <param name="TotalItems">Total rows across every page.</param>
public sealed record QuasarTableData<T>(IReadOnlyList<T> Items, int TotalItems);
