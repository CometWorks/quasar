using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Quasar.Models;
using Quasar.Services;
using Quasar.Services.ServerList;
using Xunit;

namespace Quasar.Tests;

public sealed class ServerListSettingsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "listing-settings-" + Guid.NewGuid().ToString("N"));
    private string PathName => Path.Combine(_root, "server-list.json");
    private ServerListSettingsCatalog Catalog(IConfiguration? configuration = null) => new(PathName,
        configuration ?? new ConfigurationBuilder().Build(), NullLogger<ServerListSettingsCatalog>.Instance);

    private static ServerListEntry Entry(string name = "solo") => new()
    { UniqueName = name, Name = "Public server", PublicHost = "play.example.org", PublicPort = 27016 };

    [Fact]
    public async Task SavedSettingsPreserveIdentityAndDisabledDraftsAcrossRestart()
    {
        var catalog = Catalog();
        var id = Guid.NewGuid().ToString("N");
        var entry = Entry(); entry.ListingId = id;
        var disabled = Entry("later"); disabled.Enabled = false; disabled.PublicHost = "";
        int changes = 0; catalog.Changed += () => changes++;
        await catalog.SaveAsync(new() { Enabled = true, Listings = [entry, disabled] }, 0);
        entry.Name = "Unsaved edit";
        var snapshot = catalog.GetSnapshot(); snapshot.Options.Listings[0].PublicPort = 123;
        var restarted = Catalog().GetSnapshot().Options;
        Assert.True(restarted.Enabled);
        Assert.Equal(id, restarted.Listings[0].ListingId);
        Assert.Equal("Public server", restarted.Listings[0].Name);
        Assert.Equal(27016, catalog.GetSnapshot().Options.Listings[0].PublicPort);
        Assert.False(restarted.Listings[1].Enabled);
        Assert.Equal("", restarted.Listings[1].PublicHost);
        Assert.Equal(1, changes);
    }

    [Fact]
    public async Task LegacySettingsImportedUntilFirstSaveAndEmptySelectionOverridesThem()
    {
        var legacy = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Quasar:ServerList:Enabled"] = "true", ["Quasar:ServerList:Listings:0:UniqueName"] = "solo",
            ["Quasar:ServerList:Listings:0:Name"] = "Existing", ["Quasar:ServerList:Listings:0:PublicHost"] = "play.example.org",
            ["Quasar:ServerList:Listings:0:ListingId"] = new string('a', 32),
        }).Build();
        var catalog = Catalog(legacy);
        Assert.Equal(new string('a', 32), Assert.Single(catalog.GetSnapshot().Options.Listings).ListingId);
        await catalog.SaveAsync(new() { Enabled = false }, 0);
        Assert.Empty(Catalog(legacy).GetSnapshot().Options.Listings);
        Assert.False(Catalog(legacy).GetSnapshot().Options.Enabled);
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("0.0.0.0")]
    [InlineData("127.0.0.1")]
    [InlineData("https://play.example.org")]
    [InlineData("play.example.org:27016")]
    public async Task InvalidEnabledAddressCannotReplaceSavedSettings(string host)
    {
        var catalog = Catalog();
        await catalog.SaveAsync(new() { Enabled = true, Listings = [Entry()] }, 0);
        var entry = Entry(); entry.PublicHost = host;
        await Assert.ThrowsAsync<InvalidDataException>(() => catalog.SaveAsync(new() { Enabled = true, Listings = [entry] }, 1));
        Assert.Equal("play.example.org", Assert.Single(Catalog().GetSnapshot().Options.Listings).PublicHost);
    }

    [Fact]
    public async Task ConflictingSessionsCannotOverwriteNewerSettings()
    {
        var catalog = Catalog();
        await catalog.SaveAsync(new() { Enabled = true, Listings = [Entry()] }, 0);
        await Assert.ThrowsAsync<InvalidOperationException>(() => catalog.SaveAsync(new(), 0));
        Assert.True(Catalog().GetSnapshot().Options.Enabled);
    }

    [Fact]
    public void CorruptSavedSettingsFailClosed()
    {
        Directory.CreateDirectory(_root); File.WriteAllText(PathName, "broken json");
        var catalog = Catalog();
        Assert.False(catalog.GetSnapshot().Options.Enabled);
        Assert.NotNull(catalog.LoadError);
        Assert.Equal("broken json", File.ReadAllText(PathName));
    }

    [Fact]
    public void DraftsDiscoverBothKindsAndUseSteamPortInsteadOfAdminPort()
    {
        var existing = Entry(); existing.ListingId = new string('b', 32);
        var cluster = new ClusterDefinition { UniqueName = "fleet", DisplayName = "Cluster name",
            GatewayUrl = "http://private-admin:28016", Preparation = new("hash", """{"steamListen":"0.0.0.0:28000"}""", "http://private-admin:28016", []) };
        var drafts = ServerListSettingsCatalog.CreateDrafts(new() { Enabled = true, Listings = [existing] },
            [new() { UniqueName = "SOLO" }, new() { UniqueName = "new", DisplayName = "Local label", InGameServerName = "Game name", ServerPort = 27116 }], [cluster]);
        Assert.Equal(3, drafts.Count);
        Assert.Equal(new string('b', 32), drafts.Single(e => e.UniqueName == "solo").ListingId);
        var server = drafts.Single(e => e.UniqueName == "new");
        Assert.Equal("Game name", server.Name); Assert.Equal(27116, server.PublicPort); Assert.False(server.Enabled);
        var fleet = drafts.Single(e => e.Kind == "cluster");
        Assert.Equal("Cluster name", fleet.Name); Assert.Equal(28000, fleet.PublicPort);
        Assert.Equal("", fleet.PublicHost); Assert.False(fleet.Enabled);
        Assert.DoesNotContain("private-admin", JsonSerializer.Serialize(drafts));
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
