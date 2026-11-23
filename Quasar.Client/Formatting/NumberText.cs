using System.Globalization;

namespace Quasar.Client.Formatting;

/// <summary>
/// Centralizes the boundary between human-facing numbers and stored data.
/// </summary>
/// <remarks>
/// <para>
/// Display follows the viewer's culture, so a German user sees <c>0,98</c> and an
/// English user sees <c>0.98</c>. Configs, game data, CSS values and transport
/// payloads must stay invariant, regardless of the viewer's culture.
/// </para>
/// <para>
/// User-entered numbers are accepted in the current culture first, then in the
/// invariant format as a fallback. This lets someone type their local decimal
/// separator while still accepting values copied from configs or logs.
/// </para>
/// </remarks>
public static class NumberText
{
    /// <summary>Formats a number for the current viewer's culture.</summary>
    public static string Display(IFormattable value, string format) =>
        value.ToString(format, CultureInfo.CurrentCulture);

    /// <summary>
    /// Formats a number for configs, game data, CSS, HTML attributes or transport.
    /// </summary>
    public static string Invariant(IFormattable value, string format) =>
        value.ToString(format, CultureInfo.InvariantCulture);

    /// <summary>
    /// Parses a user-entered number in the current culture, then invariant as a
    /// fallback for values copied from configs, logs or other cultures.
    /// </summary>
    public static bool TryParseUser(string? text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value)
        || double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
}
