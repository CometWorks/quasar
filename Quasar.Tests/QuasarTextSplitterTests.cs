using Quasar.Services.Assistant;
using Xunit;

namespace Quasar.Tests;

public sealed class QuasarTextSplitterTests
{
    [Fact]
    public void ShortTextIsOneMessageAndEmptyTextNone()
    {
        Assert.Equal(
            new[] { "Hello there." },
            QuasarTextSplitter.Split("  Hello there.\n", 500, 12)
        );
        Assert.Empty(QuasarTextSplitter.Split("  \n ", 500, 12));
        Assert.Empty(QuasarTextSplitter.Split(null, 500, 12));
    }

    [Fact]
    public void SplitsOnParagraphsThenLinesThenWords()
    {
        var paragraphs = new string('a', 60) + "\n\n" + new string('b', 30);
        Assert.Equal(
            new[] { new string('a', 60), new string('b', 30) },
            QuasarTextSplitter.Split(paragraphs, 80, 12)
        );

        var lines = new string('a', 60) + "\n" + new string('b', 30);
        Assert.Equal(
            new[] { new string('a', 60), new string('b', 30) },
            QuasarTextSplitter.Split(lines, 80, 12)
        );

        var words = string.Join(' ', Enumerable.Repeat("word", 40));
        var parts = QuasarTextSplitter.Split(words, 50, 12);
        Assert.All(parts, part => Assert.InRange(part.Length, 1, 50));
        Assert.All(parts, part => Assert.DoesNotContain("wo rd", part));
        Assert.Equal(words, string.Join(' ', parts));

        var unbroken = new string('x', 120);
        Assert.Equal(
            new[] { new string('x', 50), new string('x', 50), new string('x', 20) },
            QuasarTextSplitter.Split(unbroken, 50, 12)
        );
    }

    [Fact]
    public void LongAnswersAreCutAtTheCap()
    {
        var parts = QuasarTextSplitter.Split(
            string.Join(' ', Enumerable.Repeat("word", 5000)),
            QuasarTextSplitter.WhisperLength,
            QuasarTextSplitter.MaxWhispers
        );
        Assert.Equal(QuasarTextSplitter.MaxWhispers, parts.Count);
        Assert.Equal(QuasarTextSplitter.CutLine, parts[^1]);
        Assert.All(parts, part => Assert.InRange(part.Length, 1, QuasarTextSplitter.WhisperLength));

        // Exactly filling the cap needs no cut line.
        var full = QuasarTextSplitter.Split(new string('x', 500 * 12), 500, 12);
        Assert.Equal(12, full.Count);
        Assert.DoesNotContain(QuasarTextSplitter.CutLine, full);
    }
}
