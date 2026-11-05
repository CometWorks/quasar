using Quasar.Services.Assistant;
using Xunit;

namespace Quasar.Tests;

public sealed class QuasarPlayerLinksTests : IDisposable
{
    private const string Pilot = "76561198000000001";
    private const string Other = "76561198000000002";
    private readonly string _path = Path.Combine(
        Path.GetTempPath(),
        "player-links-" + Guid.NewGuid().ToString("N"),
        "player-links.json"
    );
    private readonly Clock _clock = new();

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path.GetDirectoryName(_path)!, recursive: true);
        }
        catch (DirectoryNotFoundException) { }
    }

    private sealed class Clock : TimeProvider
    {
        internal DateTimeOffset Now = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public async Task CodeLinksOnceAndExpires()
    {
        var links = new QuasarPlayerLinks(_path, _clock);
        var code = links.IssueCode(Pilot);
        Assert.Equal(QuasarPlayerLinks.CodeLength, code.Length);
        Assert.DoesNotContain(code, c => "01ILO".Contains(c));

        Assert.Null(await links.RedeemAsync("111", "WRONGCDE"));
        var link = await links.RedeemAsync(
            "111",
            " " + code.ToLowerInvariant()[..4] + "-" + code[4..] + " "
        );
        Assert.Equal(Pilot, link!.SteamId);
        Assert.Equal(Pilot, links.FindByDiscordUser("111")!.SteamId);
        Assert.Null(await links.RedeemAsync("222", code)); // one-time

        var late = links.IssueCode(Other);
        _clock.Now += QuasarPlayerLinks.CodeLifetime;
        Assert.Null(await links.RedeemAsync("222", late));
        Assert.Throws<ArgumentException>(() => links.IssueCode("12345"));
    }

    [Fact]
    public async Task OnlyTheNewestCodeOfAPlayerWorks()
    {
        var links = new QuasarPlayerLinks(_path, _clock);
        var first = links.IssueCode(Pilot);
        var second = links.IssueCode(Pilot);
        Assert.Null(await links.RedeemAsync("111", first));
        Assert.NotNull(await links.RedeemAsync("111", second));
    }

    [Fact]
    public async Task RelinkingReplacesAndLinksSurviveARestart()
    {
        var links = new QuasarPlayerLinks(_path, _clock);
        await links.RedeemAsync("111", links.IssueCode(Pilot));
        await links.RedeemAsync("111", links.IssueCode(Other));
        Assert.Equal(Other, Assert.Single(links.GetLinks()).SteamId);

        // Another Discord account proving the same Steam account takes the link over.
        await links.RedeemAsync("222", links.IssueCode(Other));
        Assert.Equal("222", Assert.Single(links.GetLinks()).DiscordUserId);
        await links.RedeemAsync("111", links.IssueCode(Pilot));

        var reloaded = new QuasarPlayerLinks(_path, _clock);
        Assert.Equal(2, reloaded.GetLinks().Count);
        Assert.Equal(Pilot, reloaded.FindByDiscordUser("111")!.SteamId);
        if (!OperatingSystem.IsWindows())
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite,
                File.GetUnixFileMode(_path)
            );

        var changed = 0;
        reloaded.Changed += () => changed++;
        Assert.True(await reloaded.RemoveAsync(Pilot));
        Assert.False(await reloaded.RemoveAsync(Pilot));
        Assert.Equal(1, changed);
        Assert.Null(new QuasarPlayerLinks(_path, _clock).FindBySteamId(Pilot));
    }
}
