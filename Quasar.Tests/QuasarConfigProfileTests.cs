using Quasar.Models;
using Xunit;

namespace Quasar.Tests;

public sealed class QuasarConfigProfileTests
{
    [Theory]
    [InlineData("earthlike", "earthlike", true)]
    [InlineData("earthlike", "EARTHLIKE", true)]
    [InlineData("earthlike", "mars", false)]
    [InlineData("", "earthlike", false)]
    [InlineData("earthlike", "", false)]
    public void WasCreatedFromWorldTemplate_MatchesOnlyLinkedTemplate(
        string sourceWorldTemplateId,
        string worldTemplateId,
        bool expected)
    {
        var profile = new QuasarConfigProfile
        {
            SourceWorldTemplateId = sourceWorldTemplateId,
        };

        Assert.Equal(expected, profile.WasCreatedFromWorldTemplate(worldTemplateId));
    }
}
