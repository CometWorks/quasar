namespace Quasar.Ui.Components;

/// <summary>Compares table values with nulls always last, in either direction.</summary>
internal sealed class QuasarNullsLastComparer : IComparer<object?>
{
    public static readonly QuasarNullsLastComparer Ascending = new(descending: false);
    public static readonly QuasarNullsLastComparer Descending = new(descending: true);

    private readonly bool _descending;

    private QuasarNullsLastComparer(bool descending) => _descending = descending;

    public int Compare(object? x, object? y)
    {
        if (x is null && y is null)
            return 0;
        if (x is null)
            return 1;
        if (y is null)
            return -1;

        var result = CompareValues(x, y);
        return _descending ? -result : result;
    }

    // Values of one comparable type sort naturally. Anything else falls back to
    // the text form, so a column that mixes types or holds objects without an
    // order still sorts instead of throwing.
    private static int CompareValues(object x, object y)
    {
        if (x is string left && y is string right)
            return string.Compare(left, right, StringComparison.CurrentCultureIgnoreCase);

        if (x is IComparable comparable && x.GetType() == y.GetType())
            return comparable.CompareTo(y);

        return string.Compare(
            Convert.ToString(x, System.Globalization.CultureInfo.CurrentCulture),
            Convert.ToString(y, System.Globalization.CultureInfo.CurrentCulture),
            StringComparison.CurrentCultureIgnoreCase);
    }
}
