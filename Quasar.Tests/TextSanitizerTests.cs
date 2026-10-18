using Quasar.Services;
using Xunit;

namespace Quasar.Tests;

public sealed class TextSanitizerTests
{
    private const string Reply = "Found 2 player(s) online:\r\nQAdmin\n> IdentityId: 1\n";

    [Fact]
    public void DropsLineBreaksByDefault() =>
        Assert.Equal(
            "Found 2 player(s) online:QAdmin> IdentityId: 1",
            TextSanitizer.CleanGameText(Reply)
        );

    [Fact]
    public void KeepsLineBreaksForChatRows() =>
        Assert.Equal(
            "Found 2 player(s) online:\nQAdmin\n> IdentityId: 1",
            TextSanitizer.CleanGameText(Reply, keepLineBreaks: true)
        );
}
