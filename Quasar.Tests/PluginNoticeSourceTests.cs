using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Quasar.Plugin.Abstractions.Assistant;
using Quasar.Services;
using Quasar.Services.Auth;
using Quasar.Services.Updates;
using Xunit;

namespace Quasar.Tests;

public sealed class PluginNoticeSourceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "plugin-notices-" + Guid.NewGuid().ToString("N")
    );

    public PluginNoticeSourceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException) { }
    }

    private sealed class Source(params QuasarNotice[] notices) : IQuasarNoticeSource
    {
        internal readonly List<(string Provider, string Subject)> Asked = [];

        public IEnumerable<QuasarNotice> GetNotices(string provider, string subject)
        {
            Asked.Add((provider, subject));
            return notices;
        }
    }

    private sealed class Broken : IQuasarNoticeSource
    {
        public IEnumerable<QuasarNotice> GetNotices(string provider, string subject) =>
            throw new InvalidOperationException("down");
    }

    [Fact]
    public async Task PluginNoticesArePushedOnceByKeyAlongsideTheOthers()
    {
        var proposals = new Source(
            new QuasarNotice(
                "assistant:proposal:1",
                "Approve a restore",
                "Pilot asks to restore Miner.",
                "/assistant/approvals"
            ),
            new QuasarNotice(
                "assistant:proposal:1",
                "Duplicate",
                "Same key.",
                "/assistant/approvals"
            ),
            new QuasarNotice(
                "assistant:proposal:2",
                "Approve a kick",
                "Kick a griefer.",
                "https://evil.example/",
                Severity: "loud"
            ),
            new QuasarNotice("", "No key", "Dropped.", "/"),
            new QuasarNotice(
                AnnouncementService.KeyPrefix + "fake",
                "Fake announcement",
                "Dropped.",
                "/"
            )
        );
        var roles = new QuasarRoleMapper(
            new QuasarAuthOptions { TrustedNetworkBypass = new() { Roles = [QuasarRoles.Viewer] } },
            null!
        );
        using var consent = new DataHandlingConsentCatalog(
            NullLogger<DataHandlingConsentCatalog>.Instance,
            Path.Combine(_root, "consent.json")
        );
        using var announcements = new AnnouncementService(
            consent,
            null!,
            new HttpClient(),
            NullLogger<AnnouncementService>.Instance,
            Path.Combine(_root, "announcements.json")
        );
        var sent = new List<UpdateNotice>();
        using var push = new PushNotificationService(
            new EphemeralDataProtectionProvider(),
            roles,
            announcements,
            Path.Combine(_root, "push.json"),
            (notice, _) =>
            {
                sent.Add(notice);
                return Task.CompletedTask;
            },
            otherNotices: () => [new UpdateNotice("update:1", "Update", "New Quasar.", "/updates")],
            noticeSources: [proposals, new Broken()]
        );
        var viewer = roles.CreateTrustedNetworkPrincipal();
        await push.SubscribeAsync(
            viewer,
            new BrowserPushSubscription(
                "https://fcm.googleapis.com/fcm/send/plugin-notices",
                Base64(
                    new byte[] { 4 }
                        .Concat(new byte[64])
                        .ToArray()
                ),
                Base64(new byte[16])
            )
        );

        var listed = await push.GetNoticesAsync(viewer);
        Assert.Equal(
            new[] { "update:1", "assistant:proposal:1", "assistant:proposal:2" },
            listed.Select(n => n.Key)
        );
        var second = listed.Single(n => n.Key == "assistant:proposal:2");
        Assert.Equal("/", second.Url);
        Assert.Equal("info", second.Severity);
        Assert.Contains(
            (QuasarAuthSchemes.TrustedNetwork, QuasarAuthSchemes.TrustedNetwork),
            proposals.Asked
        );

        await push.SendPendingAsync(default);
        await push.SendPendingAsync(default);
        Assert.Equal(
            new[] { "update:1", "assistant:proposal:1", "assistant:proposal:2" },
            sent.Select(n => n.Key)
        );
    }

    private static string Base64(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
