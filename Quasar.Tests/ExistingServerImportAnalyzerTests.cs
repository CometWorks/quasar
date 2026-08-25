using Quasar.Models;
using Quasar.Services;
using Xunit;

namespace Quasar.Tests;

public sealed class ExistingServerImportAnalyzerTests
{
    [Fact]
    public void AnalyzeVanilla_ImportsAccessMotdNetworkAndWorldSettings()
    {
        using var fixture = new ImportFixture();
        var world = fixture.CreateWorld("Saves/Active World", onlineMode: "FRIENDS", maxPlayers: 18, workshopId: 123456);
        fixture.Write(
            "SpaceEngineers-Dedicated.cfg",
            $$"""
            <MyConfigDedicated>
              <SessionSettings><OnlineMode>PRIVATE</OnlineMode><MaxPlayers>12</MaxPlayers></SessionSettings>
              <LoadWorld>{{world}}</LoadWorld>
              <IP>127.0.0.1</IP>
              <ServerPort>28016</ServerPort>
              <ServerName>Imported DS</ServerName>
              <WorldName>Imported World</WorldName>
              <MessageOfTheDay>Welcome engineers</MessageOfTheDay>
              <MessageOfTheDayUrl>https://example.test/rules</MessageOfTheDayUrl>
              <NetworkType>EOS</NetworkType>
              <GroupID>42</GroupID>
              <Administrators><unsignedLong>76561198000000001</unsignedLong></Administrators>
              <Reserved><unsignedLong>76561198000000002</unsignedLong></Reserved>
              <Banned><unsignedLong>76561198000000003</unsignedLong></Banned>
              <ServerPasswordHash>not-reversible</ServerPasswordHash>
            </MyConfigDedicated>
            """);

        var analysis = ExistingServerImportAnalyzer.Analyze(ExistingServerKind.Vanilla, fixture.Root);

        Assert.Equal("Imported DS", analysis.ServerName);
        Assert.Equal("Imported World", analysis.WorldName);
        Assert.Equal("127.0.0.1", analysis.ServerIp);
        Assert.Equal(28016, analysis.ServerPort);
        Assert.True(analysis.HasPasswordHash);
        Assert.Equal(4, analysis.AccessEntryCount);
        Assert.Equal("Welcome engineers", analysis.DetectedProfile.RootSettings.MessageOfTheDay);
        Assert.Equal("https://example.test/rules", analysis.DetectedProfile.RootSettings.MessageOfTheDayUrl);
        Assert.Equal(QuasarNetworkType.EOS, analysis.DetectedProfile.RootSettings.NetworkType);
        Assert.Equal((ulong)42, analysis.DetectedProfile.RootSettings.GroupId);
        Assert.Single(analysis.DetectedProfile.RootSettings.Administrators);
        Assert.Single(analysis.DetectedProfile.RootSettings.Reserved);
        Assert.Single(analysis.DetectedProfile.RootSettings.Banned);

        var detectedWorld = Assert.Single(analysis.Worlds);
        Assert.True(detectedWorld.IsConfiguredWorld);
        Assert.Equal(2, detectedWorld.DetectedProfile.SessionSettings.OnlineMode);
        Assert.Equal(18, detectedWorld.DetectedProfile.SessionSettings.MaxPlayers);
        Assert.Equal(123456, Assert.Single(detectedWorld.DetectedProfile.Mods).WorkshopId);
    }

    [Fact]
    public void AnalyzeTorch_UsesInstanceAndReportsTorchOnlyData()
    {
        using var fixture = new ImportFixture();
        var instancePath = Path.Combine(fixture.Root, "Instance");
        Directory.CreateDirectory(instancePath);
        fixture.CreateWorld("Instance/Saves/Torch World", onlineMode: "PUBLIC", maxPlayers: 8);
        fixture.Write(
            "Torch.cfg",
            """
            <TorchConfig>
              <InstancePath>Instance</InstancePath>
              <Autostart>true</Autostart>
              <RestartOnCrash>false</RestartOnCrash>
              <EnableWhitelist>true</EnableWhitelist>
              <Whitelist><guid>entry</guid></Whitelist>
              <Plugins><guid>plugin</guid></Plugins>
            </TorchConfig>
            """);
        fixture.Write(
            "Instance/SpaceEngineers-Dedicated.cfg",
            """
            <MyConfigDedicated>
              <ServerName>Torch Server</ServerName>
              <LoadWorld>Saves/Torch World</LoadWorld>
            </MyConfigDedicated>
            """);

        var analysis = ExistingServerImportAnalyzer.Analyze(ExistingServerKind.Torch, fixture.Root);

        Assert.Equal(instancePath, analysis.AppDataPath);
        Assert.True(analysis.TorchAutostart);
        Assert.False(analysis.TorchRestartOnCrash);
        Assert.True(analysis.TorchWhitelistEnabled);
        Assert.Equal(1, analysis.TorchWhitelistEntryCount);
        Assert.Equal(1, analysis.TorchPluginCount);
        Assert.Contains(analysis.Warnings, warning => warning.Contains("whitelist", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(analysis.Warnings, warning => warning.Contains("Torch plugin", StringComparison.OrdinalIgnoreCase));
        Assert.True(Assert.Single(analysis.Worlds).IsConfiguredWorld);
    }

    [Fact]
    public void BuildProfile_RespectsAccessAndMotdSelections()
    {
        var sourceProfile = new QuasarConfigProfile();
        sourceProfile.RootSettings.MessageOfTheDay = "Keep this MOTD";
        sourceProfile.RootSettings.CrossPlatform = true;
        sourceProfile.RootSettings.GroupId = 99;
        sourceProfile.RootSettings.Administrators = ["admin"];
        sourceProfile.RootSettings.Reserved = [7];
        sourceProfile.RootSettings.Banned = [8];
        sourceProfile.SessionSettings.OnlineMode = 3;
        var worldProfile = new QuasarConfigProfile();
        worldProfile.SessionSettings.OnlineMode = 2;
        worldProfile.Mods = [new QuasarModSelection { WorkshopId = 123 }];
        var world = new ExistingServerWorldCandidate
        {
            Name = "World",
            Path = Path.GetTempPath(),
            SessionSettingCount = 1,
            ImportedProfile = worldProfile,
        };
        var analysis = new ExistingServerImportAnalysis
        {
            Kind = ExistingServerKind.Vanilla,
            SourcePath = Path.GetTempPath(),
            AppDataPath = Path.GetTempPath(),
            ConfigPath = Path.Combine(Path.GetTempPath(), "SpaceEngineers-Dedicated.cfg"),
            ServerName = "Server",
            WorldName = "World",
            ServerIp = "0.0.0.0",
            Worlds = [world],
            Warnings = [],
            ImportedProfile = sourceProfile,
        };
        var template = new QuasarWorldTemplate { WorldTemplateId = "world", Name = "World" };

        var profile = ExistingServerImportService.BuildProfile(
            analysis,
            world,
            template,
            "Imported",
            ExistingServerImportSections.ServerSettings | ExistingServerImportSections.AccessLists);

        Assert.Equal("Keep this MOTD", profile.RootSettings.MessageOfTheDay);
        Assert.Equal((ulong)99, profile.RootSettings.GroupId);
        Assert.Equal("admin", Assert.Single(profile.RootSettings.Administrators));
        Assert.Equal((ulong)7, Assert.Single(profile.RootSettings.Reserved));
        Assert.Equal((ulong)8, Assert.Single(profile.RootSettings.Banned));
        Assert.False(profile.RootSettings.CrossPlatform);
        Assert.Equal(1, profile.SessionSettings.OnlineMode);
        Assert.Empty(profile.Mods);
    }

    private sealed class ImportFixture : IDisposable
    {
        public ImportFixture()
        {
            Root = Path.Combine(Path.GetTempPath(), $"quasar-import-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public string CreateWorld(string relativePath, string onlineMode, int maxPlayers, long? workshopId = null)
        {
            var path = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(path);
            File.WriteAllText(Path.Combine(path, "Sandbox.sbc"), "<MyObjectBuilder_Checkpoint />");
            var mods = workshopId is null
                ? string.Empty
                : $"<Mods><ModItem FriendlyName=\"Test Mod\"><PublishedFileId>{workshopId}</PublishedFileId></ModItem></Mods>";
            File.WriteAllText(
                Path.Combine(path, WorldSandboxConfigEditor.SandboxConfigFileName),
                $"<MyObjectBuilder_WorldConfiguration><Settings><OnlineMode>{onlineMode}</OnlineMode><MaxPlayers>{maxPlayers}</MaxPlayers></Settings>{mods}<SessionName>{Path.GetFileName(path)}</SessionName></MyObjectBuilder_WorldConfiguration>");
            return path;
        }

        public void Write(string relativePath, string contents)
        {
            var path = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, contents);
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
    }
}
