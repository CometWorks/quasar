namespace Quasar.Services.Assistant;

/// <summary>Splits an answer into chat-sized messages on paragraph, line, then word boundaries.</summary>
public static class QuasarTextSplitter
{
    /// <summary>One in-game whisper; the network allows 4096 characters, chat reads better shorter.</summary>
    public const int WhisperLength = 500;

    /// <summary>Whispers per answer; a longer answer ends with <see cref="CutLine"/>.</summary>
    public const int MaxWhispers = 12;

    /// <summary>Discord's message limit.</summary>
    public const int DiscordLength = 2000;

    public const int MaxDiscordMessages = 5;

    public const string CutLine = "…(cut)";

    public static IReadOnlyList<string> Split(string? text, int maxLength, int maxParts)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLength, CutLine.Length);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxParts, 2);
        var parts = new List<string>();
        var rest = (text ?? string.Empty).Trim();
        while (rest.Length > 0)
        {
            if (parts.Count == maxParts - 1 && rest.Length > maxLength)
            {
                parts.Add(CutLine);
                break;
            }

            var end = rest.Length <= maxLength ? rest.Length : SplitPoint(rest, maxLength);
            parts.Add(rest[..end].TrimEnd());
            rest = rest[end..].TrimStart();
        }

        return parts;
    }

    // Splitting at index i keeps rest[..i], at most maxLength characters.
    private static int SplitPoint(string text, int maxLength)
    {
        var window = text[..(maxLength + 1)];
        var paragraph = window.LastIndexOf("\n\n", StringComparison.Ordinal);
        if (paragraph >= maxLength / 3)
            return paragraph;
        var line = window.LastIndexOf('\n');
        if (line >= maxLength / 3)
            return line;
        var word = window.LastIndexOfAny([' ', '\t', '\n']);
        if (word > 0)
            return word;
        return char.IsHighSurrogate(text[maxLength - 1]) ? maxLength - 1 : maxLength;
    }
}
