using System.Xml.Linq;
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

    [Fact]
    public void ExistingServerProfile_CanImportOnlineMode()
    {
        var path = Path.Combine(Path.GetTempPath(), $"quasar-import-{Guid.NewGuid():N}.sbc");
        try
        {
            File.WriteAllText(path, "<MyObjectBuilder_WorldConfiguration><Settings><OnlineMode>PRIVATE</OnlineMode></Settings></MyObjectBuilder_WorldConfiguration>");

            var import = WorldSandboxConfigEditor.ReadConfigProfile(path, includeOnlineMode: true);

            Assert.Equal(3, import.Profile.SessionSettings.OnlineMode);
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

    [Fact]
    public async Task WriteProfileAsync_UpdatesAdvertisedWorldName()
    {
        var path = Path.Combine(Path.GetTempPath(), $"quasar-world-{Guid.NewGuid():N}.sbc");
        try
        {
            await File.WriteAllTextAsync(
                path,
                "<MyObjectBuilder_WorldConfiguration><SessionName>Old World</SessionName><Settings /></MyObjectBuilder_WorldConfiguration>");

            await WorldSandboxConfigEditor.WriteProfileAsync(
                path,
                new QuasarConfigProfile(),
                "New World");

            var document = XDocument.Load(path);
            Assert.Equal("New World", document.Root?.Element("SessionName")?.Value);
        }
        finally
        {
            File.Delete(path);
        }
    }
    [Fact]
    public async Task ProfileSettingsPatch_ClusterNodesKeepNoGameBackups()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"quasar-patch-{Guid.NewGuid():N}");
        var path = Path.Combine(Path.GetTempPath(), $"quasar-world-{Guid.NewGuid():N}.sbc");
        try
        {
            var profile = new QuasarConfigProfile();
            Assert.Equal(5, profile.SessionSettings.MaxBackupSaves);
            var patch = XDocument.Parse(await ClusterWorkshopModStager.ProfileSettingsXmlAsync(directory, profile, "World", default));
            Assert.Equal("0", patch.Root?.Element("Settings")?.Element("MaxBackupSaves")?.Value);

            // A single server keeps the profile's value: Quasar clones and backs up its world from those backups.
            await File.WriteAllTextAsync(path, "<MyObjectBuilder_WorldConfiguration><Settings /></MyObjectBuilder_WorldConfiguration>");
            await WorldSandboxConfigEditor.WriteProfileAsync(path, profile, "World");
            Assert.Equal("5", XDocument.Load(path).Root?.Element("Settings")?.Element("MaxBackupSaves")?.Value);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
            File.Delete(path);
        }
    }
}
