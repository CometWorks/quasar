using System.Security.Claims;
using Quasar.Models;
using Quasar.Services;
using Quasar.Services.Auth;
using Quasar.Services.Updates;
using Xunit;

namespace Quasar.Tests;

public sealed class PushNotificationTests
{
    [Fact]
    public void MultipleBrowsersForOneAccountRemainIndependent()
    {
        const string provider = QuasarAuthSchemes.Steam;
        const string subject = "76561198000000000";
        var first = new BrowserPushSubscription("https://fcm.googleapis.com/fcm/send/first", "key-a", "auth-a");
        var second = new BrowserPushSubscription("https://fcm.googleapis.com/fcm/send/second", "key-b", "auth-b");
        var subscriptions = PushNotificationService.RegisterSubscription([], provider, subject, first, "notice-a");
        subscriptions = PushNotificationService.RegisterSubscription(subscriptions, provider, subject, second, "notice-b");

        Assert.Equal(2, subscriptions.Count);
        Assert.Contains(subscriptions, item => item.Endpoint == first.Endpoint && item.LastNoticeKey == "notice-a");
        Assert.Contains(subscriptions, item => item.Endpoint == second.Endpoint && item.LastNoticeKey == "notice-b");

        subscriptions = PushNotificationService.RegisterSubscription(subscriptions, provider, subject,
            first with { Auth = "new-auth" }, "new-notice");
        Assert.Equal(2, subscriptions.Count);
        Assert.Contains(subscriptions, item => item.Endpoint == first.Endpoint
            && item.Auth == "new-auth" && item.LastNoticeKey == "notice-a");

        subscriptions = PushNotificationService.RemoveSubscription(subscriptions, provider, subject, first.Endpoint);
        Assert.Single(subscriptions);
        Assert.Equal(second.Endpoint, subscriptions[0].Endpoint);
    }

    [Fact]
    public void DifferentAccountsKeepTheirOwnBrowserSubscriptions()
    {
        const string provider = QuasarAuthSchemes.Steam;
        var first = new BrowserPushSubscription("https://fcm.googleapis.com/fcm/send/first", "key-a", "auth-a");
        var second = new BrowserPushSubscription("https://fcm.googleapis.com/fcm/send/second", "key-b", "auth-b");
        var subscriptions = PushNotificationService.RegisterSubscription([], provider, "account-a", first, null);
        subscriptions = PushNotificationService.RegisterSubscription(subscriptions, provider, "account-b", second, null);

        subscriptions = PushNotificationService.RemoveSubscription(subscriptions, provider, "account-a", second.Endpoint);
        Assert.Equal(2, subscriptions.Count);
        Assert.Contains(subscriptions, item => item.Subject == "account-a" && item.Endpoint == first.Endpoint);
        Assert.Contains(subscriptions, item => item.Subject == "account-b" && item.Endpoint == second.Endpoint);
    }

    [Fact]
    public void ReassigningAnotherAccountsEndpointCannotBypassBrowserLimit()
    {
        const string provider = QuasarAuthSchemes.Steam;
        List<PushNotificationService.StoredSubscription> subscriptions = [];
        for (var index = 0; index < 16; index++)
            subscriptions = PushNotificationService.RegisterSubscription(subscriptions, provider, "account-a",
                new BrowserPushSubscription($"https://fcm.googleapis.com/fcm/send/{index}", "key", "auth"), null);
        var other = new BrowserPushSubscription("https://fcm.googleapis.com/fcm/send/other", "key", "auth");
        subscriptions = PushNotificationService.RegisterSubscription(subscriptions, provider, "account-b", other, null);

        Assert.Throws<InvalidOperationException>(() => PushNotificationService.RegisterSubscription(
            subscriptions, provider, "account-a", other, null));
        Assert.Equal(16, subscriptions.Count(item => item.Subject == "account-a"));
        Assert.Single(subscriptions, item => item.Subject == "account-b");
    }

    [Theory]
    [InlineData("https://fcm.googleapis.com/fcm/send/abc", true)]
    [InlineData("https://updates.push.services.mozilla.com/wpush/v2/abc", true)]
    [InlineData("https://web.push.apple.com/abc", true)]
    [InlineData("http://fcm.googleapis.com/fcm/send/abc", false)]
    [InlineData("https://fcm.googleapis.com.evil.example/abc", false)]
    [InlineData("https://127.0.0.1/abc", false)]
    [InlineData("https://fcm.googleapis.com:8443/abc", false)]
    public void SubscriptionEndpointMustBeAnApprovedPushProvider(string endpoint, bool allowed)
    {
        var subscription = new BrowserPushSubscription(endpoint,
            Convert.ToBase64String(new byte[] { 4 }.Concat(new byte[64]).ToArray())
                .TrimEnd('=').Replace('+', '-').Replace('/', '_'),
            Convert.ToBase64String(new byte[16]).TrimEnd('=').Replace('+', '-').Replace('/', '_'));

        if (allowed) PushNotificationService.Validate(subscription);
        else Assert.Throws<InvalidDataException>(() => PushNotificationService.Validate(subscription));
    }

    [Fact]
    public void BellNoticePointsToAffectedCluster()
    {
        var cluster = new ClusterDefinition
        {
            UniqueName = "alpha",
            DisplayName = "Alpha",
            PackageSelection = new ClusterPackageSelection(1, "1.1.4", "hash", "commit", "key"),
        };
        var release = new ClusterPackageRelease("1.1.5", 1, 2, 100, "hash");
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Role, QuasarRoles.Viewer)], QuasarAuthSchemes.Steam));

        var notice = UpdateNotices.Current(new QuasarUpdateSnapshot(),
            new ClusterReleaseSnapshot(release, DateTimeOffset.UtcNow, null), [cluster], principal);

        Assert.Equal("cluster:alpha:1.1.5", notice?.Key);
        Assert.Equal("/clusters/alpha", notice?.Url);
    }

    [Fact]
    public void StagedWebUpdateTakesPriorityAndUsesUpdatesPage()
    {
        var snapshot = new QuasarUpdateSnapshot
        {
            WebReleases =
            [
                new QuasarUpdateCandidate { Version = "1.2.0", IsNewer = true },
                new QuasarUpdateCandidate { Version = "1.1.9", IsNewer = true, IsStaged = true },
            ],
        };

        var notice = UpdateNotices.Current(snapshot, new ClusterReleaseSnapshot(null, null, null),
            [], new ClaimsPrincipal());

        Assert.Equal("web:1.1.9:True", notice?.Key);
        Assert.Equal("/settings/updates", notice?.Url);
    }
}
