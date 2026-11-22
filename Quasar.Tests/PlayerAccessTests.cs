using HarmonyLib;
using Magnetar.Protocol.Model;
using System.Text.Json;
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
    public async Task LegacyWebsiteSettingsAreDiscardedWhilePolicyAndAuthorizationSurvive()
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "player-access.json");
        await File.WriteAllTextAsync(path, """
            {
              "settings": {
                "revision": 42, "publicUrl": "https://panel.example.com",
                "verifiedUrl": "https://panel.example.com", "verifiedUntil": "2030-01-01T00:00:00Z",
                "discordClientId": "123", "discordClientSecret": "obsolete-protected-value",
                "policies": [{ "target": "server:alpha", "guildId": 77, "enabled": true, "whitelist": true }]
              },
              "authorizations": [{ "target": "server:alpha", "authenticationId": "existing-link" }]
            }
            """);
        var catalog = new PlayerAccessCatalog(path);
        var settings = catalog.GetSettings();
        Assert.Equal(42, settings.Revision);
        var policy = Assert.Single(settings.Policies);
        Assert.True(policy.Whitelist);
        await catalog.AuthorizeAsync(policy, "another-link", default);
        await catalog.SaveSettingsAsync(settings);
        await Assert.ThrowsAsync<InvalidOperationException>(() => catalog.SaveSettingsAsync(settings));
        var reloaded = new PlayerAccessCatalog(path);
        Assert.True(reloaded.IsAuthorized("server:alpha", "existing-link"));
        Assert.True(reloaded.IsAuthorized("server:alpha", "another-link"));
        Assert.True(Assert.Single(reloaded.GetSettings().Policies).Enabled);
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        var stored = document.RootElement.GetProperty("settings");
        Assert.Equal(new[] { "policies", "revision" }, stored.EnumerateObject().Select(p => p.Name).Order());
        if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
    }

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
    public async Task CanonicalRenamesCannotSilentlyRemoveAnActiveAuthenticationPolicy()
    {
        var catalog = new PlayerAccessCatalog(Path.Combine(directory, "access.json"));
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
