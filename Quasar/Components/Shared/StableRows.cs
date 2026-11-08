using System.Text.Json;

namespace Quasar.Components.Shared;

/// <summary>
/// Keeps table row objects the same instances across rebuilds while their content is unchanged.
/// MudTable keys each row by its item, so a list rebuilt from fresh copies recreates every row and
/// its tooltip, menu and select popovers on each render. Firefox freezes under that churn (QSR-16).
/// A changed row still gets its fresh instance, so rows that change on most updates need key-based
/// equality on the row type instead, like the Players page's KnownPlayerView.
/// </summary>
public sealed class StableRows<T>(Func<T, string> key)
    where T : class
{
    private Dictionary<string, T> _rows = new(StringComparer.Ordinal);

    public IReadOnlyList<T> Reuse(IEnumerable<T> items)
    {
        var next = new Dictionary<string, T>(StringComparer.Ordinal);
        var result = new List<T>();
        foreach (var fresh in items)
        {
            var id = key(fresh);
            var item = fresh;
            // Compares the previous instance as it is now, so an instance edited in place is replaced.
            // A duplicate id keeps its fresh instance, so one object never shows up as two rows.
            if (!next.ContainsKey(id) && _rows.TryGetValue(id, out var previous) && !ReferenceEquals(previous, fresh)
                && JsonSerializer.Serialize(previous) == JsonSerializer.Serialize(fresh))
                item = previous;
            next.TryAdd(id, item);
            result.Add(item);
        }

        _rows = next;
        return result;
    }
}
