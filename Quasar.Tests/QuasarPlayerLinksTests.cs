using System.Text.Json;
using Quasar.Services.Players;
using Xunit;

namespace Quasar.Tests;

public sealed class QuasarPlayerLinksTests : IDisposable
{
    private const string Pilot = "76561198000000001", Other = "76561198000000002";
    private readonly string path = Path.Combine(Path.GetTempPath(), "player-links-" + Guid.NewGuid().ToString("N"), "player-links.json");
    public void Dispose() { if (Directory.Exists(Path.GetDirectoryName(path))) Directory.Delete(Path.GetDirectoryName(path)!, true); }

    [Fact]
    public async Task BrowserProofIsDurableUniqueAndDoesNotSilentlyReplaceAccounts()
    {
        var links = new QuasarPlayerLinks(path);
        var linked = await links.AuthenticateAsync(Pilot, "111");
        Assert.True(Guid.TryParseExact(linked.AuthenticationId, "N", out _));
        Assert.Equal(linked, await links.AuthenticateAsync(Pilot, "111"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => links.AuthenticateAsync(Pilot, "222"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => links.AuthenticateAsync(Other, "111"));
        Assert.Equal(linked, new QuasarPlayerLinks(path).FindBySteamId(Pilot));
        Assert.Equal(Pilot, links.FindByDiscordUser("111")!.SteamId);
        if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        Assert.True(await links.RemoveAsync(Pilot));
        var relinked = await links.AuthenticateAsync(Pilot, "222");
        Assert.NotEqual(linked.AuthenticationId, relinked.AuthenticationId);
    }

    [Fact]
    public async Task LegacyLinksRemainVisibleButCannotIdentifyPlayersUntilBothProvidersAreProven()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { links = new[] { new QuasarPlayerLink("111", Pilot, DateTimeOffset.UtcNow) } }));
        var links = new QuasarPlayerLinks(path);
        Assert.Single(links.GetLinks());
        Assert.Null(links.FindBySteamId(Pilot));
        Assert.Null(links.FindByDiscordUser("111"));
        var verified = await links.AuthenticateAsync(Pilot, "111");
        Assert.NotNull(verified.AuthenticationId);
        Assert.Equal(verified, Assert.Single(links.GetLinks()));
    }

    [Fact]
    public async Task ConcurrentConflictingProofsCannotCreateDuplicateLinks()
    {
        var links = new QuasarPlayerLinks(path);
        async Task<bool> Try(string discord)
        {
            try { await links.AuthenticateAsync(Pilot, discord); return true; }
            catch (InvalidOperationException) { return false; }
        }
        var results = await Task.WhenAll(Try("111"), Try("222"));
        Assert.Single(results.Where(result => result));
        Assert.Single(new QuasarPlayerLinks(path).GetLinks());
    }

    [Fact]
    public async Task FailedPersistenceDoesNotPublishAnAuthenticatedLink()
    {
        var links = new QuasarPlayerLinks(path);
        Assert.Empty(links.GetLinks());
        Directory.CreateDirectory(path);
        await Assert.ThrowsAnyAsync<IOException>(() => links.AuthenticateAsync(Pilot, "111"));
        Assert.Null(links.FindBySteamId(Pilot));
    }

    [Fact]
    public async Task DamagedIdentityDataCannotBeReplacedSilently()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "broken");
        var links = new QuasarPlayerLinks(path);
        await Assert.ThrowsAsync<InvalidDataException>(() => links.AuthenticateAsync(Pilot, "111"));
        Assert.Equal("broken", await File.ReadAllTextAsync(path));
    }
}
