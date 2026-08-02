using Quasar.Models;
using Quasar.Services;
using Xunit;

namespace Quasar.Tests;

public sealed class QuasarConfigProfileTests
{
    [Fact]
    public void NewProfile_DefaultsOnlineModeToPublic()
    {
        Assert.Equal(1, new QuasarConfigProfile().SessionSettings.OnlineMode);
    }

    [Theory]
    [InlineData("OFFLINE")]
    [InlineData("PUBLIC")]
    [InlineData("FRIENDS")]
    [InlineData("PRIVATE")]
    public void WorldTemplateProfile_DefaultsOnlineModeToPublic(string sourceOnlineMode)
    {
        var path = Path.Combine(Path.GetTempPath(), $"quasar-template-{Guid.NewGuid():N}.sbc");
        try
        {
            File.WriteAllText(path, $"<MyObjectBuilder_Checkpoint><Settings><OnlineMode>{sourceOnlineMode}</OnlineMode></Settings></MyObjectBuilder_Checkpoint>");

            var import = WorldSandboxConfigEditor.ReadConfigProfile(path);

            Assert.Equal(1, import.Profile.SessionSettings.OnlineMode);
        }
        finally
        {
            File.Delete(path);
        }
    }

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
