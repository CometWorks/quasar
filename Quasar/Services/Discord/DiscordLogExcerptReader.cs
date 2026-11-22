namespace Quasar.Services.Discord;

internal static class DiscordLogExcerptReader
{
    private const int ExceptionContextLines = 50;
    private const int ExceptionMatchLimit = 10;
    private const int ExceptionSearchBytes = 4 * 1024 * 1024;
    internal static async Task<(string Text, bool Truncated)> ReadTailAsync(string path, int maxBytes)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);

        var truncated = false;
        if (stream.Length > maxBytes)
        {
            stream.Seek(-maxBytes, SeekOrigin.End);
            truncated = true;
        }

        using var reader = new StreamReader(stream);
        if (truncated)
            await reader.ReadLineAsync();

        var text = await reader.ReadToEndAsync();
        return (text, truncated);
    }

    internal static string ExtractExceptionBlocks(string text, bool truncated, string logName)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var matches = lines
            .Select((line, index) => (line, index))
            .Where(item => item.line.Contains("Exception", StringComparison.OrdinalIgnoreCase))
            .Select(item => item.index)
            .TakeLast(ExceptionMatchLimit)
            .ToList();

        if (matches.Count == 0)
        {
            return truncated
                ? $"No lines containing \"Exception\" found in the searched {logName} tail."
                : $"No lines containing \"Exception\" found in {logName}.";
        }

        var ranges = new List<(int Start, int End)>();
        foreach (var index in matches)
        {
            var start = index;
            var end = Math.Min(lines.Length - 1, index + ExceptionContextLines);
            if (ranges.Count > 0 && start <= ranges[^1].End + 1)
            {
                var previous = ranges[^1];
                ranges[^1] = (previous.Start, Math.Max(previous.End, end));
            }
            else
            {
                ranges.Add((start, end));
            }
        }

        var output = new List<string>();
        if (truncated)
            output.Add($"Searched latest {ExceptionSearchBytes / (1024 * 1024)} MB of {logName}.");

        for (var rangeIndex = 0; rangeIndex < ranges.Count; rangeIndex++)
        {
            var (start, end) = ranges[rangeIndex];
            if (output.Count > 0)
                output.Add(string.Empty);

            output.Add($"--- Exception excerpt {rangeIndex + 1} (line {start + 1} of searched text) ---");
            for (var i = start; i <= end; i++)
                output.Add(lines[i]);
        }

        return string.Join(Environment.NewLine, output);
    }

}
