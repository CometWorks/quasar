using HarmonyLib;
using Magnetar.Protocol.Model;
using Microsoft.AspNetCore.DataProtection;
using Quasar.Agent;
using Quasar.ClusterDeployment;
using Quasar.Services.Players;
using Sandbox.Engine.Multiplayer;
using Xunit;

namespace Quasar.Tests;

public sealed class PlayerAccessTests : IDisposable
{
    private const string Steam = "76561198000000001";
    private readonly string directory = Path.Combine(Path.GetTempPath(), "player-access-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }

    [Fact]
    public async Task SettingsProtectCredentialsRejectLostUpdatesAndInvalidateChangedDomains()
    {
        var protection = new EphemeralDataProtectionProvider();
        var path = Path.Combine(directory, "player-access.json");
        var catalog = new PlayerAccessCatalog(path, protection);
        var settings = new PlayerAccessSettings { PublicUrl = "https://quasar.example.com", DiscordClientId = "42", DiscordClientSecret = "oauth-secret" };
        settings.Policies.Add(new() { Target = "server:alpha", GuildId = 42, Enabled = true });
        await catalog.SaveSettingsAsync(settings);
        Assert.DoesNotContain("oauth-secret", File.ReadAllText(path));
        await catalog.RecordVerificationAsync(settings.PublicUrl, DateTimeOffset.UtcNow.AddDays(7), default);
        var verified = catalog.GetSettings();
        Assert.NotNull(verified.VerifiedUntil);
        await catalog.AuthorizeAsync("server:alpha", "authenticated-link", default);
        Assert.True(catalog.IsAuthorized("server:alpha", "authenticated-link"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => catalog.SaveSettingsAsync(settings));
        verified.PublicUrl = "https://new.example.com";
        await catalog.SaveSettingsAsync(verified);
        var reloaded = new PlayerAccessCatalog(path, protection);
        Assert.Equal("oauth-secret", reloaded.GetSettings().DiscordClientSecret);
        Assert.Null(reloaded.GetSettings().VerifiedUntil);
        Assert.True(reloaded.IsAuthorized("server:alpha", "authenticated-link"));
        if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
    }

    [Theory]
    [InlineData("http://quasar.example.com")]
    [InlineData("https://127.0.0.1")]
    [InlineData("https://localhost")]
    [InlineData("https://user:password@quasar.example.com")]
    [InlineData("https://quasar.example.com/private")]
    [InlineData("https://quasar.example.com?redirect=https://attacker.test")]
    public void InvalidPublicOriginsAreRejected(string url) => Assert.False(PlayerAccessCatalog.IsPublicUrl(url));

    [Fact]
    public void EquivalentWebsiteFormsUseOneCanonicalOrigin() =>
        Assert.Equal("https://quasar.example.com", PlayerAccessCatalog.CanonicalPublicUrl(" https://QUASAR.EXAMPLE.COM:443/ "));

    [Fact]
    public void ActiveAuthenticationCannotDeployAPackageWithoutTheDeclaredCapability()
    {
        var cli = Path.Combine(directory, "cli"); Directory.CreateDirectory(cli);
        var path = Path.Combine(cli, "deployment-capabilities.json");
        File.WriteAllText(path, "{\"schemaVersion\":1,\"frozenPluginBundles\":true}");
        Assert.Throws<InvalidDataException>(() => ClusterDeploymentFiles.RequirePlayerAuthentication(directory));
        File.WriteAllText(path, "{\"schemaVersion\":1,\"frozenPluginBundles\":true,\"playerAuthentication\":1}");
        ClusterDeploymentFiles.RequirePlayerAuthentication(directory);
    }

    [Fact]
    public async Task LostProtectionKeysDisableOAuthWithoutErasingTheCredentialOrStoppingOtherSettings()
    {
        var originalKey = new EphemeralDataProtectionProvider();
        var path = Path.Combine(directory, "player-access.json");
        var original = new PlayerAccessCatalog(path, originalKey);
        await original.SaveSettingsAsync(new() { DiscordClientId = "42", DiscordClientSecret = "recoverable-secret" });
        var lost = new PlayerAccessCatalog(path, new EphemeralDataProtectionProvider());
        Assert.True(lost.CredentialUnavailable);
        Assert.Empty(lost.GetSettings().DiscordClientSecret);
        var settings = lost.GetSettings();
        settings.PublicUrl = "https://new.example.com";
        await lost.SaveSettingsAsync(settings);
        Assert.Equal("recoverable-secret", new PlayerAccessCatalog(path, originalKey).GetSettings().DiscordClientSecret);
    }

    [Fact]
    public async Task CanonicalRenamesCannotSilentlyRemoveAnActiveAuthenticationPolicy()
    {
        var catalog = new PlayerAccessCatalog(Path.Combine(directory, "access.json"), new EphemeralDataProtectionProvider());
        await catalog.SaveSettingsAsync(new() { Policies = [new() { Target = "server:alpha", Enabled = true, GuildId = 42 }] });
        catalog.EnsureServerIdentityChangeAllowed("alpha", "alpha");
        Assert.Throws<InvalidOperationException>(() => catalog.EnsureServerIdentityChangeAllowed("alpha", "beta"));
        Assert.Throws<InvalidOperationException>(() => catalog.EnsureServerIdentityChangeAllowed("alpha", "Alpha"));
    }

    [Fact]
    public void RoleRulesRequireMembershipAcceptanceAndAnyAccessRoleThenCombineBenefits()
    {
        var policy = new PlayerAccessPolicy
        {
            RequiredRoles = [10, 20], PriorityRoles = [30],
            Promotions = [new(10, 1), new(20, 3), new(30, 4)],
        };
        var until = DateTimeOffset.UtcNow.AddMinutes(1);
        var membership = new DiscordPlayerMembership(true, true, [20, 30], until);
        var decision = PlayerAccessService.Resolve(policy, Steam, membership, true);
        Assert.True(decision.Grant.Allowed);
        Assert.True(decision.Grant.Priority);
        Assert.Equal(4, decision.Grant.Promotion);
        Assert.Equal(until, decision.Grant.ExpiresAt);
        Assert.False(PlayerAccessService.Resolve(policy, Steam, membership, false).Grant.Allowed);
        Assert.False(PlayerAccessService.Resolve(policy, Steam, membership with { Member = false }, true).Grant.Allowed);
        var removed = PlayerAccessService.Resolve(policy, Steam, membership with { Roles = [10] }, true);
        Assert.True(removed.Grant.Allowed);
        Assert.Equal(1, removed.Grant.Promotion);
        Assert.False(removed.Grant.Priority);
        Assert.False(PlayerAccessService.Resolve(policy, Steam, membership with { Roles = [30] }, true).Grant.Allowed);
        var unavailable = PlayerAccessService.Resolve(policy, Steam, membership with { Known = false }, true);
        Assert.False(unavailable.Grant.Allowed);
        Assert.False(unavailable.Grant.Confirmed);
    }

    [Fact]
    public void AdmissionFailsClosedUntilFreshAndExpiresAtBothPolicyAndMemberBoundaries()
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = new PlayerAccessSnapshot
        {
            Target = "server:alpha", Enabled = true, Whitelist = true, IssuedAt = now,
            ExpiresAt = now.AddMinutes(2), Players = [new() { SteamId = ulong.Parse(Steam), Allowed = true, Confirmed = true, ExpiresAt = now.AddMinutes(1) }],
        };
        Assert.False(PlayerAccessEnforcement.Allows(null!, true, ulong.Parse(Steam), now));
        Assert.True(PlayerAccessEnforcement.Allows(snapshot, true, ulong.Parse(Steam), now));
        Assert.False(PlayerAccessEnforcement.Allows(snapshot, true, ulong.Parse(Steam), now.AddMinutes(1)));
        Assert.False(PlayerAccessEnforcement.Allows(snapshot, true, 999, now));
        Assert.True(PlayerAccessEnforcement.Allows(snapshot, false, 999, now));
        snapshot.Players[0].Confirmed = false;
        Assert.False(PlayerAccessEnforcement.Allows(snapshot, true, ulong.Parse(Steam), now));
        snapshot.Players[0].Promotion = 5;
        Assert.Throws<InvalidDataException>(() => PlayerAccessEnforcement.Validate(snapshot, "server:alpha", now));
        snapshot.Players[0].Promotion = 4;
        Assert.Throws<InvalidDataException>(() => PlayerAccessEnforcement.Validate(snapshot, "server:other", now));
        Assert.Throws<InvalidDataException>(() => PlayerAccessEnforcement.Validate(snapshot, "server:alpha", now.AddMinutes(2)));
        Assert.Throws<InvalidDataException>(() => PlayerAccessEnforcement.Validate(snapshot, "server:alpha", now, 100));
    }

    [Fact]
    public void MissingOrRestoredOlderSettingsCannotOpenAnExistingWhitelistAtLaunch()
    {
        Assert.True(PlayerAccessEnforcement.InitialWhitelist(null, 0, true, 100));
        Assert.True(PlayerAccessEnforcement.InitialWhitelist(false, 99, true, 100));
        Assert.False(PlayerAccessEnforcement.InitialWhitelist(false, 101, true, 100));
        Assert.True(PlayerAccessEnforcement.InitialWhitelist(true, 101, false, 100));
        Assert.False(PlayerAccessEnforcement.InitialWhitelist(null, 0, false, 0));
    }

    [Fact]
    public void PriorityHookMatchesExactlyOneReservedCheckInTheCurrentServerBuild()
    {
        var target = AccessTools.DeclaredMethod(typeof(MyDedicatedServerBase), "SetUserConnectionResult");
        var original = PatchProcessor.GetOriginalInstructions(target, generator: null);
        var rewritten = PlayerAccessEnforcement.RewritePriority(original).ToArray();
        Assert.Single(rewritten.Where(instruction => instruction.operand is System.Reflection.MethodInfo method
            && method.DeclaringType == typeof(PlayerAccessEnforcement) && method.Name == "IsReserved"));
        Assert.Throws<InvalidOperationException>(() => PlayerAccessEnforcement.RewritePriority([]).ToArray());
        Assert.NotNull(AccessTools.DeclaredMethod(typeof(MyDedicatedServerBase), "UserAccepted", [typeof(ulong), typeof(bool)]));
        Assert.NotNull(AccessTools.DeclaredMethod(typeof(MyDedicatedServerBase), "UserRejected"));
    }
}
