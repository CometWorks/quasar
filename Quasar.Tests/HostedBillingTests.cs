using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Quasar.Hosting;
using Quasar.Services;
using Quasar.Services.Auth;
using Quasar.Services.Updates;
using Xunit;

namespace Quasar.Tests;

public sealed partial class HostedPanelTests
{
    [Fact]
    public async Task BoundInstallationPublishesOnlySteamAdministratorsAndDeliversTargetedBillingPushOnce()
    {
        const string payer = "76561198000000001", incoming = "76561198000000002", viewer = "76561198000000003";
        var state = State with { EnrollmentId = "installation-enrollment" };
        await HostedPanelState.SaveAsync(state);
        using var rbac = new RbacConfigCatalog(NullLogger<RbacConfigCatalog>.Instance);
        await rbac.SaveAsync(new RbacConfig { SubjectRoleMappings = [
            new() { Subject = payer, Roles = [QuasarRoles.Admin] },
            new() { Subject = incoming, Roles = [QuasarRoles.Admin] },
            new() { Subject = viewer, Roles = [QuasarRoles.Viewer] },
            new() { Provider = "Oidc", Subject = "external-subject", Roles = [QuasarRoles.Admin] }
        ] });
        using var handler = new AdministrationHandler(state, payer, incoming);
        var services = new ServiceCollection();
        services.AddHttpClient("Nebula").ConfigurePrimaryHttpMessageHandler(() => handler);
        await using var provider = services.BuildServiceProvider();
        using var hosted = new HostedPanelService(provider.GetRequiredService<IHttpClientFactory>(), NullLogger<HostedPanelService>.Instance, rbac);
        await hosted.RefreshAsync();
        Assert.Equal(new[] { payer, incoming }, handler.Administrators);
        Assert.Empty(hosted.BillingNotices(QuasarAuthSchemes.TrustedNetwork, payer));
        Assert.Empty(hosted.BillingNotices(QuasarAuthSchemes.Steam, viewer));
        Assert.Equal(HostedPanelState.Origin + "/account", Assert.Single(hosted.BillingNotices(QuasarAuthSchemes.Steam, payer)).Url);
        Assert.Single(hosted.BillingNotices(QuasarAuthSchemes.Steam, incoming));

        var protection = new EphemeralDataProtectionProvider();
        var roles = new QuasarRoleMapper(new QuasarAuthOptions(), rbac);
        using var consent = new DataHandlingConsentCatalog(NullLogger<DataHandlingConsentCatalog>.Instance, Path.Combine(root, "consent.json"));
        using var announcements = new AnnouncementService(consent, null!, new HttpClient(), NullLogger<AnnouncementService>.Instance, Path.Combine(root, "announcements.json"));
        var sent = new List<UpdateNotice>();
        string pushPath = Path.Combine(root, "billing-push.json");
        using (var push = new PushNotificationService(protection, roles, announcements, pushPath,
            (notice, _) => { sent.Add(notice); return Task.CompletedTask; }, otherNotices: () => [], hosted: hosted))
        {
            await push.SubscribeAsync(roles.CreateSteamPrincipal(payer), Subscription("payer"));
            await push.SubscribeAsync(roles.CreateSteamPrincipal(viewer), Subscription("viewer"));
            Assert.True(roles.CreateSteamPrincipal(payer).IsInRole(QuasarRoles.Admin), "Fixture payer retains its admin role before the deliberate removal.");
            Assert.Single(hosted.BillingNotices(QuasarAuthSchemes.Steam, payer));
            Assert.True(await push.IsRegisteredAsync(roles.CreateSteamPrincipal(payer), Subscription("payer").Endpoint));
            Assert.Single(await push.GetNoticesAsync(roles.CreateSteamPrincipal(payer)));
            Assert.Empty(await push.GetNoticesAsync(roles.CreateSteamPrincipal(viewer)));
            // The payer remains entitled to targeted billing notices after losing their Quasar role.
            await rbac.SaveAsync(new RbacConfig { SubjectRoleMappings = [new() { Subject = incoming, Roles = [QuasarRoles.Admin] }] });
            await Task.WhenAll(push.SendPendingAsync(default), push.SendPendingAsync(default));
            Assert.Single(sent);
        }
        using var restarted = new PushNotificationService(protection, roles, announcements, pushPath,
            (notice, _) => { sent.Add(notice); return Task.CompletedTask; }, otherNotices: () => [], hosted: hosted);
        await restarted.SendPendingAsync(default);
        Assert.Single(sent);
        handler.Status = "completed";
        await hosted.RefreshAsync();
        Assert.Equal(new[] { incoming }, handler.Administrators);
        await restarted.SendPendingAsync(default);
        Assert.Equal(2, sent.Count);
        Assert.EndsWith(":completed", sent[1].Key);
    }

    private static BrowserPushSubscription Subscription(string suffix) => new("https://fcm.googleapis.com/fcm/send/" + suffix,
        Base64(new byte[] { 4 }.Concat(new byte[64]).ToArray()), Base64(new byte[16]));
    private static string Base64(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed class AdministrationHandler(HostedPanelState state, string payer, string incoming) : HttpMessageHandler
    {
        public string[] Administrators = [];
        public string Status = "requested";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal(HostedPanelState.Origin, request.RequestUri!.GetLeftPart(UriPartial.Authority));
            Assert.Equal(state.InstallationSecret, request.Headers.Authorization!.Parameter);
            if (request.Method == HttpMethod.Post)
            {
                Assert.EndsWith("/" + state.EnrollmentId + "/administration", request.RequestUri.AbsolutePath);
                var data = await request.Content!.ReadFromJsonAsync<JsonElement>(ct);
                Administrators = data.GetProperty("steamIds").EnumerateArray().Select(v => v.GetString()!).ToArray();
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { notices = new[] { payer, incoming }.Select(id => new {
                    key = "nebula-billing:" + new string('a', 32) + ":" + Status, steamId = id, title = "Nebula billing update", body = "Review the billing update in Nebula."
                }) }) };
            }
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new {
                panelId = state.PanelId, ownerSteamId = payer, active = true, expired = false, credential = state.Credential
            }) };
        }
    }
}
