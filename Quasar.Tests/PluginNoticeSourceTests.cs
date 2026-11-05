using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
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
        internal readonly List<ClaimsPrincipal> Asked = [];

        public ValueTask<IReadOnlyList<QuasarNotice>> GetNoticesAsync(
            ClaimsPrincipal subscriber,
            CancellationToken token
        )
        {
            Asked.Add(subscriber);
            return ValueTask.FromResult<IReadOnlyList<QuasarNotice>>(notices);
        }
    }

    // Like an approval queue: only accounts that could approve hear about a proposal.
    private sealed class ApproverSource(IAuthorizationService authorization) : IQuasarNoticeSource
    {
        public async ValueTask<IReadOnlyList<QuasarNotice>> GetNoticesAsync(
            ClaimsPrincipal subscriber,
            CancellationToken token
        ) =>
            (
                await authorization.AuthorizeAsync(
                    subscriber,
                    null,
                    QuasarPolicyNames.CanControlServers
                )
            ).Succeeded
                ? [new QuasarNotice("assistant:approval:1", "Approve", "Waiting.", "/assistant")]
                : [];
    }

    private sealed class Broken : IQuasarNoticeSource
    {
        public ValueTask<IReadOnlyList<QuasarNotice>> GetNoticesAsync(
            ClaimsPrincipal subscriber,
            CancellationToken token
        ) => throw new InvalidOperationException("down");
    }

    [Fact]
    public async Task PluginNoticesReachOnlyTheSubscribersTheirPolicyAllowsAndArePushedOnceByKey()
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
        using var services = new ServiceCollection()
            .AddLogging()
            .AddAuthorization(Program.ConfigureAuthorizationPolicies)
            .BuildServiceProvider();
        var authorization = services.GetRequiredService<IAuthorizationService>();
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
            noticeSources: [proposals, new ApproverSource(authorization), new Broken()]
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
        Assert.True(proposals.Asked[^1].IsInRole(QuasarRoles.Viewer));

        var editor = new QuasarRoleMapper(
            new QuasarAuthOptions { TrustedNetworkBypass = new() { Roles = [QuasarRoles.Editor] } },
            null!
        ).CreateTrustedNetworkPrincipal();
        Assert.Contains(
            "assistant:approval:1",
            (await push.GetNoticesAsync(editor)).Select(n => n.Key)
        );

        // The sweep rebuilds the subscriber from its roles: the viewer gets no approval notice.
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
