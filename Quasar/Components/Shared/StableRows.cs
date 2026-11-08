using System.Text.Json;

namespace Quasar.Components.Shared;

/// <summary>
/// Keeps table row objects the same instances across rebuilds while their content is unchanged.
/// MudTable keys each row by its item, so a list rebuilt from fresh copies recreates every row and
/// its tooltip, menu and select popovers on each render. Firefox freezes under that churn (QSR-16).
/// </summary>
public sealed class StableRows<T>(Func<T, string> key)
    where T : class
{
    private Dictionary<string, (T Item, string Json)> _rows = new(StringComparer.Ordinal);

    public IReadOnlyList<T> Reuse(IEnumerable<T> items)
    {
        var next = new Dictionary<string, (T Item, string Json)>(StringComparer.Ordinal);
        var result = new List<T>();
        foreach (var fresh in items)
        {
            var id = key(fresh);
            var item = fresh;
            var json = (string?)null;
            // A duplicate id keeps its fresh instance, so one object never shows up as two rows.
            if (!next.ContainsKey(id) && _rows.TryGetValue(id, out var previous))
            {
                json = ReferenceEquals(previous.Item, fresh) ? previous.Json : JsonSerializer.Serialize(fresh);
                if (json == previous.Json)
                    item = previous.Item;
            }

            next.TryAdd(id, (item, json ?? JsonSerializer.Serialize(fresh)));
            result.Add(item);
        }

        _rows = next;
        return result;
    }
}
