using System.Collections;
using System.Diagnostics.CodeAnalysis;

namespace Quasar.Ui;

/// <summary>
/// Parameters passed to a dialog component. Keys are matched to component
/// parameters by name, case-insensitively, like Blazor's normal parameter binding.
/// </summary>
/// <remarks>
/// <para>
/// A dictionary is useful when the component type is only known at runtime
/// (for example when a plugin page opens a dialog by name). For ordinary
/// compile-time checked call sites, prefer
/// <see cref="QuasarDialogParameters{TComponent}"/>, which keeps parameter
/// names in sync with the component.
/// </para>
/// <para>
/// Parameters are applied once when the dialog component is created. Changing
/// a parameter object afterwards does not update an open dialog; close and
/// reopen the dialog or let the component observe a service instead.
/// </para>
/// </remarks>
/// <example>
/// <code language="csharp">
/// var parameters = new QuasarDialogParameters
/// {
///     ["Message"] = "Delete this host?",
///     ["HostId"] = hostId,
/// };
/// </code>
/// </example>
public class QuasarDialogParameters : IReadOnlyDictionary<string, object?>
{
    private readonly Dictionary<string, object?> _values = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates an empty parameter set.</summary>
    public QuasarDialogParameters()
    {
    }

    /// <summary>
    /// Creates a parameter set from an existing sequence. Later entries
    /// overwrite earlier entries with the same name.
    /// </summary>
    public QuasarDialogParameters(IEnumerable<KeyValuePair<string, object?>> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        foreach (var pair in values)
            Set(pair.Key, pair.Value);
    }

    /// <summary>Gets or sets a parameter by component parameter name.</summary>
    /// <remarks>
    /// Reading a name that was not set throws <see cref="KeyNotFoundException"/>,
    /// matching a read-only dictionary. Use <see cref="TryGetValue"/> for a
    /// safe lookup.
    /// </remarks>
    public object? this[string key]
    {
        get => _values[key];
        set => Set(key, value);
    }

    /// <summary>Sets a parameter and returns this instance for chaining.</summary>
    /// <param name="parameterName">Component parameter name, case-insensitive.</param>
    /// <param name="value">Value passed to the component parameter.</param>
    public QuasarDialogParameters Set(string parameterName, object? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parameterName);
        _values[parameterName] = value;
        return this;
    }

    /// <inheritdoc />
    public int Count => _values.Count;

    /// <inheritdoc />
    public IEnumerable<string> Keys => _values.Keys;

    /// <inheritdoc />
    public IEnumerable<object?> Values => _values.Values;

    /// <inheritdoc />
    public bool ContainsKey(string key) => _values.ContainsKey(key);

    /// <inheritdoc />
    public bool TryGetValue(string key, [MaybeNullWhen(false)] out object? value) =>
        _values.TryGetValue(key, out value);

    /// <inheritdoc />
    public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() => _values.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
