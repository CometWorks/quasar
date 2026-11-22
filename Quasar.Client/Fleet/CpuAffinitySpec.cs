using System.Globalization;
using System.Text;

namespace Quasar.Models;

/// <summary>
/// Parsing, validation and formatting helpers for the per-server CPU affinity setting.
/// Affinity is stored as a cpuset string (taskset syntax) such as "0-7" or "0-7,16-23".
/// An empty string means no affinity (all cores allowed). When non-empty it must resolve
/// to at least two distinct, in-range logical cores.
/// </summary>
/// <remarks>
/// Windows <see cref="System.Diagnostics.Process.ProcessorAffinity"/> only addresses logical
/// processors 0-63 within a single processor group, so <see cref="ToWindowsMask"/> cannot
/// target machines that expose more than 64 logical CPUs on Windows.
/// </remarks>
public static class CpuAffinitySpec
{
    /// <summary>Minimum number of cores a non-empty affinity must contain.</summary>
    public const int MinimumCores = 2;

    /// <summary>
    /// Parses a cpuset string into a sorted, distinct list of core indices.
    /// An empty/whitespace input is valid and yields an empty core list (no affinity).
    /// </summary>
    /// <param name="text">The cpuset string, e.g. "0-3,8".</param>
    /// <param name="processorCount">Number of logical cores available; indices must be in [0, processorCount).</param>
    /// <param name="cores">The resolved, sorted, distinct core indices (empty when no affinity).</param>
    /// <param name="error">A human-readable error when parsing fails; null on success.</param>
    /// <returns>True when the input is valid (including empty); false otherwise.</returns>
    public static bool TryParse(string? text, int processorCount, out IReadOnlyList<int> cores, out string? error)
    {
        cores = Array.Empty<int>();
        error = null;

        var trimmed = text?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
            return true;

        var result = new SortedSet<int>();
        foreach (var rawPart in trimmed.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var dash = rawPart.IndexOf('-');
            if (dash < 0)
            {
                if (!TryParseCore(rawPart, processorCount, out var core, out error))
                    return false;
                result.Add(core);
                continue;
            }

            var startText = rawPart[..dash].Trim();
            var endText = rawPart[(dash + 1)..].Trim();
            if (!TryParseCore(startText, processorCount, out var start, out error) ||
                !TryParseCore(endText, processorCount, out var end, out error))
                return false;

            if (end < start)
            {
                error = $"Invalid range '{rawPart}': start is greater than end.";
                return false;
            }

            for (var core = start; core <= end; core++)
                result.Add(core);
        }

        if (result.Count < MinimumCores)
        {
            error = $"At least {MinimumCores} cores are required (got {result.Count}).";
            return false;
        }

        cores = result.ToArray();
        return true;
    }

    private static bool TryParseCore(string text, int processorCount, out int core, out string? error)
    {
        error = null;
        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out core))
        {
            error = $"'{text}' is not a valid core number.";
            return false;
        }

        if (core < 0 || core >= processorCount)
        {
            error = $"Core {core} is out of range (0-{processorCount - 1}).";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Formats a set of core indices into a canonical compact cpuset string, collapsing
    /// consecutive runs into ranges (e.g. {0,1,2,3,8} -> "0-3,8"). Returns an empty string
    /// for an empty input.
    /// </summary>
    public static string Format(IEnumerable<int> cores)
    {
        var sorted = new SortedSet<int>(cores).ToArray();
        if (sorted.Length == 0)
            return string.Empty;

        var builder = new StringBuilder();
        var runStart = sorted[0];
        var previous = sorted[0];

        for (var i = 1; i <= sorted.Length; i++)
        {
            if (i < sorted.Length && sorted[i] == previous + 1)
            {
                previous = sorted[i];
                continue;
            }

            if (builder.Length > 0)
                builder.Append(',');

            if (runStart == previous)
                builder.Append(runStart);
            else
                builder.Append(runStart).Append('-').Append(previous);

            if (i < sorted.Length)
            {
                runStart = sorted[i];
                previous = sorted[i];
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Builds a processor-affinity bitmask for the given cores. Cores at index 64 or above
    /// are ignored (not addressable via Process.ProcessorAffinity).
    /// </summary>
    public static long ToWindowsMask(IEnumerable<int> cores)
    {
        long mask = 0;
        foreach (var core in cores)
        {
            if (core is >= 0 and < 64)
                mask |= 1L << core;
        }

        return mask;
    }
}
