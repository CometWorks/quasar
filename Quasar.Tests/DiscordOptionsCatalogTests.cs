using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Quasar.Services.Discord;
using Xunit;

namespace Quasar.Tests;

public sealed class DiscordOptionsCatalogTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "discord-options-" + Guid.NewGuid().ToString("N"));
    private readonly string _path;

    public DiscordOptionsCatalogTests()
    {
        Directory.CreateDirectory(_root);
        _path = Path.Combine(_root, "discord.json");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task BotTokenIsProtectedAndFileIsOwnerOnly()
    {
        var protection = new EphemeralDataProtectionProvider();
        using (var catalog = CreateCatalog(protection))
        {
            await catalog.SaveAsync(new DiscordOptions
            {
                BotToken = "discord-secret-token",
                GuildId = 42,
                Enabled = true,
            });
        }

        var persisted = await File.ReadAllTextAsync(_path);
        Assert.DoesNotContain("discord-secret-token", persisted, StringComparison.Ordinal);
        Assert.DoesNotContain("\"botToken\"", persisted, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("protectedBotToken", persisted, StringComparison.OrdinalIgnoreCase);
        if (!OperatingSystem.IsWindows())
            Assert.Equal((UnixFileMode)0x180, File.GetUnixFileMode(_path));

        using var reloaded = CreateCatalog(protection);
        Assert.Equal("discord-secret-token", reloaded.GetOptions().BotToken);
        Assert.Equal((ulong)42, reloaded.GetOptions().GuildId);
    }

    [Fact]
    public void LegacyPlaintextTokenIsMigratedImmediately()
    {
        File.WriteAllText(_path,
            """
            {
              "botToken": "legacy-discord-token",
              "guildId": 73,
              "enabled": true,
              "useProxy": false
            }
            """);

        using var catalog = CreateCatalog(new EphemeralDataProtectionProvider());

        var options = catalog.GetOptions();
        Assert.Equal("legacy-discord-token", options.BotToken);
        Assert.Equal((ulong)73, options.GuildId);
        Assert.True(options.Enabled);
        Assert.False(options.UseProxy);
        var migrated = File.ReadAllText(_path);
        Assert.DoesNotContain("legacy-discord-token", migrated, StringComparison.Ordinal);
        Assert.DoesNotContain("\"botToken\"", migrated, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("protectedBotToken", migrated, StringComparison.OrdinalIgnoreCase);
        if (!OperatingSystem.IsWindows())
            Assert.Equal((UnixFileMode)0x180, File.GetUnixFileMode(_path));
    }

    [Fact]
    public async Task LostKeyringKeepsNonSecretOptions()
    {
        using (var catalog = CreateCatalog(new EphemeralDataProtectionProvider()))
        {
            await catalog.SaveAsync(new DiscordOptions
            {
                BotToken = "unreadable-token",
                GuildId = 91,
                Enabled = true,
                UseProxy = false,
            });
        }

        using var reloaded = CreateCatalog(new EphemeralDataProtectionProvider());
        var options = reloaded.GetOptions();
        Assert.Empty(options.BotToken);
        Assert.Equal((ulong)91, options.GuildId);
        Assert.True(options.Enabled);
        Assert.False(options.UseProxy);
    }

    private DiscordOptionsCatalog CreateCatalog(IDataProtectionProvider protection) =>
        new(NullLogger<DiscordOptionsCatalog>.Instance, protection, _path);
}
