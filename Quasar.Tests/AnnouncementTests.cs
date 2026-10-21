using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bunit;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Quasar.Components.Shared;
using Quasar.Services;
using Quasar.Services.Auth;
using Quasar.Services.Updates;
using Xunit;

namespace Quasar.Tests;

public sealed class AnnouncementTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "announcements-" + Guid.NewGuid().ToString("N"));
    private readonly List<IDisposable> _owned = [];
    private readonly EphemeralDataProtectionProvider _protection = new();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private DateTimeOffset _now = DateTimeOffset.UtcNow;
    private string Cache => Path.Combine(_root, "announcements.json");
    private string PushPath => Path.Combine(_root, "push.json");
    private T Own<T>(T item) where T : IDisposable { _owned.Add(item); return item; }
    private DataHandlingConsentCatalog Consent() => Own(new DataHandlingConsentCatalog(
        NullLogger<DataHandlingConsentCatalog>.Instance, Path.Combine(_root, "consent.json")));
    private HttpClient Http(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) => Own(new HttpClient(new Handler(send)));
    private DiagnosticsUplinkService Diagnostics(DataHandlingConsentCatalog consent, HttpClient http,
        string origin = "https://backoffice.example/configured/", string? credential = "fixture-credential")
    {
        var credentials = new ClusterCredentialStore(_protection, Path.Combine(_root, "credentials.json"));
        var hosts = new ClusterHostCatalog(credentials, Path.Combine(_root, "hosts"));
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Quasar:Diagnostics:BackofficeUrl"] = origin, ["Quasar:Diagnostics:UploadToken"] = credential }).Build();
        return Own(new DiagnosticsUplinkService(consent, hosts, new ClusterHostClient(http, credentials), http, config,
            NullLogger<DiagnosticsUplinkService>.Instance, Path.Combine(_root, "diagnostics")));
    }
    private AnnouncementService Service(DataHandlingConsentCatalog consent, DiagnosticsUplinkService diagnostics, HttpClient http) =>
        Own(new AnnouncementService(consent, diagnostics, http, NullLogger<AnnouncementService>.Instance, Cache, () => _now));
    private Announcement Message(string severity = "critical") => new(Guid.NewGuid().ToString("N"), severity,
        "<script>urgent</script>", "Plain <img src=x onerror=alert(1)>\nsecond line", "https://example.org/details?x=1&y=2", _now.AddMinutes(-1), _now.AddDays(1));
    private static HttpResponseMessage Snapshot(params Announcement[] items) => new(HttpStatusCode.OK)
    { Content = JsonContent.Create(new { schemaVersion = 1, announcements = items }, options: Json) };

    [Fact]
    public async Task CurrentYesAndDiagnosticsRequiredEvenWithConfiguredCredential()
    {
        using var consent = Consent();
        int requests = 0;
        var http = Http((_, _) => { requests++; return Task.FromResult(Snapshot(Message())); });
        var service = Service(consent, Diagnostics(consent, http), http);
        await service.PollOnceAsync(); Assert.Equal(0, requests);
        await consent.SaveAsync(false, true, false);
        await service.PollOnceAsync(); Assert.Equal(0, requests);
        await consent.SaveAsync(true, false, false);
        await service.PollOnceAsync(); Assert.Equal(0, requests);
        await consent.SaveAsync(true, true, false);
        await service.PollOnceAsync(); Assert.Equal(1, requests); Assert.Single(service.GetNotices());
        await consent.SaveAsync(false); // Statistics withdrawal alone also fences announcements.
        Assert.Empty(service.GetNotices()); Assert.False(File.Exists(Cache));
        await service.PollOnceAsync(); Assert.Equal(1, requests);
    }

    [Fact]
    public async Task CredentialAndInstallationHeadersOnlyGoToConfiguredHttpsEndpoint()
    {
        var consent = Consent(); await consent.SaveAsync(true, true, false);
        string? id = null;
        var http = Http((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("https://backoffice.example/configured/v1/announcements", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("fixture-credential", request.Headers.Authorization.Parameter);
            id = Assert.Single(request.Headers.GetValues("X-Installation-Id"));
            Assert.Equal(Guid.ParseExact(id, "N").ToString("N"), id);
            return Task.FromResult(Snapshot(Message()));
        });
        var diagnostics = Diagnostics(consent, http);
        await Service(consent, diagnostics, http).PollOnceAsync(); Assert.Equal(diagnostics.InstallationId, id);
        var insecure = Diagnostics(consent, Http((_, _) => throw new Exception("Must not enroll or fetch over HTTP")), "http://localhost/");
        await Service(consent, insecure, http).PollOnceAsync();
    }

    [Fact]
    public async Task EnrollmentCredentialIsReusedForFetchAndRenegotiatedAfter401()
    {
        var consent = Consent(); await consent.SaveAsync(true, true, false);
        int enrollments = 0, gets = 0;
        var http = Http(async (request, token) =>
        {
            if (request.Method == HttpMethod.Post)
            {
                enrollments++;
                using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
                using var key = RSA.Create();
                key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(json.RootElement.GetProperty("publicKey").GetString()!), out _);
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(new
                {
                    installationId = json.RootElement.GetProperty("installationId").GetString(),
                    encryptedUploadToken = Convert.ToBase64String(key.Encrypt(Encoding.ASCII.GetBytes(new string('a', 64)), RSAEncryptionPadding.OaepSHA256)),
                }) };
            }
            gets++;
            Assert.Equal(new string('a', 64), request.Headers.Authorization!.Parameter);
            Assert.Equal("Bearer", request.Headers.Authorization.Scheme);
            Assert.Equal(32, Assert.Single(request.Headers.GetValues("X-Installation-Id")).Length);
            return gets == 1 ? new(HttpStatusCode.Unauthorized) : Snapshot(Message());
        });
        var service = Service(consent, Diagnostics(consent, http, credential: null), http);
        await Assert.ThrowsAsync<HttpRequestException>(() => service.PollOnceAsync());
        await service.PollOnceAsync(); Assert.Equal(2, enrollments); Assert.Single(service.GetNotices());
        await service.PollOnceAsync(); Assert.Equal(2, enrollments); Assert.Equal(3, gets);
    }

    [Theory]
    [InlineData(301)] [InlineData(302)] [InlineData(307)] [InlineData(308)]
    [InlineData(401)] [InlineData(403)] [InlineData(500)] [InlineData(204)]
    public async Task RedirectsAndErrorsCannotReplaceActiveSnapshot(int status)
    {
        var consent = Consent(); await consent.SaveAsync(true, true, false);
        int requests = 0;
        var http = Http((_, _) =>
        {
            if (++requests == 1) return Task.FromResult(Snapshot(Message()));
            var response = new HttpResponseMessage((HttpStatusCode)status);
            response.Headers.Location = new Uri("http://untrusted.example/credentials");
            return Task.FromResult(response);
        });
        var service = Service(consent, Diagnostics(consent, http), http);
        await service.PollOnceAsync();
        await Assert.ThrowsAsync<HttpRequestException>(() => service.PollOnceAsync());
        Assert.Single(service.GetNotices()); Assert.Equal(2, requests);
        _now = _now.AddMinutes(6); Assert.Empty(service.GetNotices());
    }

    [Fact]
    public async Task RevocationDuringFetchDiscardsResponseEvenWhenTransportIgnoresCancellation()
    {
        var consent = Consent(); await consent.SaveAsync(true, true, false);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var response = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken inFlight = default;
        var http = Http((_, token) => { inFlight = token; entered.SetResult(); return response.Task; });
        var service = Service(consent, Diagnostics(consent, http), http);
        var polling = service.PollOnceAsync(); await entered.Task;
        await consent.SaveAsync(false); Assert.True(inFlight.IsCancellationRequested);
        await consent.SaveAsync(true, true, false); // Regrant must not admit the old response.
        response.SetResult(Snapshot(Message()));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => polling);
        Assert.Empty(service.GetNotices()); Assert.False(File.Exists(Cache));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task RevocationDuringEnrollmentCancelsAndNeverFetches(bool redirect)
    {
        var consent = Consent(); await consent.SaveAsync(true, true, false);
        int requests = 0;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reply = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken inFlight = default;
        var http = Http(async (request, token) =>
        {
            requests++; Assert.Equal(HttpMethod.Post, request.Method); Assert.Null(request.Headers.Authorization);
            Assert.Equal("/configured/v1/installations/enroll", request.RequestUri!.AbsolutePath);
            inFlight = token;
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            using var key = RSA.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(body.RootElement.GetProperty("publicKey").GetString()!), out _);
            var response = redirect ? new HttpResponseMessage(HttpStatusCode.TemporaryRedirect)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new
                { installationId = body.RootElement.GetProperty("installationId").GetString(),
                    encryptedUploadToken = Convert.ToBase64String(key.Encrypt(Encoding.ASCII.GetBytes(new string('a', 64)), RSAEncryptionPadding.OaepSHA256)) }) };
            entered.SetResult(); await reply.Task; return response;
        });
        var service = Service(consent, Diagnostics(consent, http, credential: null), http);
        var polling = service.PollOnceAsync(); await entered.Task;
        await consent.SaveAsync(false); Assert.True(inFlight.IsCancellationRequested);
        reply.SetResult(new(HttpStatusCode.OK));
        var error = await Assert.ThrowsAnyAsync<Exception>(() => polling);
        Assert.True(error is OperationCanceledException or HttpRequestException, error.GetType().Name);
        Assert.Equal(1, requests); Assert.Empty(service.GetNotices());
    }

    [Fact]
    public async Task SnapshotWithdrawalExclusiveExpiryRestartAndStaleDeadline()
    {
        var consent = Consent(); await consent.SaveAsync(true, true, false);
        var first = Message(); var second = Message("warning") with { ExpiresAtUtc = _now.AddMinutes(1) };
        Announcement[] items = [first, second];
        var http = Http((_, _) => Task.FromResult(Snapshot(items)));
        var diagnostics = Diagnostics(consent, http);
        var service = Service(consent, diagnostics, http); await service.PollOnceAsync();
        Assert.Equal(2, service.GetNotices().Length);
        var restarted = Service(consent, diagnostics, http); Assert.Equal(service.GetNotices(), restarted.GetNotices());
        _now = second.ExpiresAtUtc; Assert.Single(service.GetNotices());
        items = []; await service.PollOnceAsync(); Assert.Empty(service.GetNotices());
        items = [first]; await service.PollOnceAsync(); Assert.Single(service.GetNotices());
        _now = _now.AddMinutes(6); Assert.Empty(service.GetNotices());
        Assert.Empty(Service(consent, diagnostics, http).GetNotices());
    }

    [Fact]
    public async Task DeliveryRechecksCurrentSnapshotAndCancelsOnRevocationOrWithdrawal()
    {
        var consent = Consent(); await consent.SaveAsync(true, true, false);
        var message = Message(); Announcement[] items = [message];
        var http = Http((_, _) => Task.FromResult(Snapshot(items)));
        var service = Service(consent, Diagnostics(consent, http), http); await service.PollOnceAsync();
        var notice = Assert.Single(service.GetNotices());
        CancellationToken delivery = default;
        Task Send(CancellationToken token) { delivery = token; return Task.Delay(Timeout.Infinite, token); }
        var sending = service.StartDelivery(notice, Send, default)!;
        items = []; await service.PollOnceAsync(); Assert.True(delivery.IsCancellationRequested);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sending);
        Assert.Null(service.StartDelivery(notice, _ => throw new Exception("withdrawn"), default));
        items = [message]; await service.PollOnceAsync();
        sending = service.StartDelivery(notice, Send, default)!;
        await consent.SaveAsync(false); Assert.True(delivery.IsCancellationRequested);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sending);
        Assert.False(service.IsCurrent(notice));
        Assert.Null(service.StartDelivery(notice, _ => throw new Exception("revoked"), default));
    }

    [Fact]
    public async Task CacheFromAnotherConsentOrOriginCannotDisplayAfterRestart()
    {
        var consent = Consent(); await consent.SaveAsync(true, true, false);
        var http = Http((_, _) => Task.FromResult(Snapshot(Message())));
        var diagnostics = Diagnostics(consent, http);
        var service = Service(consent, diagnostics, http); await service.PollOnceAsync();
        var otherOrigin = Diagnostics(consent, http, "https://other.example/");
        Assert.Empty(Service(consent, otherOrigin, http).GetNotices());
        // Preserve old disk data to prove the reload fence, independent of cleanup succeeding.
        var oldCache = await File.ReadAllBytesAsync(Cache);
        await consent.SaveAsync(false);
        await consent.SaveAsync(true, true, false);
        await File.WriteAllBytesAsync(Cache, oldCache);
        Assert.Empty(Service(consent, diagnostics, http).GetNotices());
    }

    private async Task SeedPushAsync()
    {
        var state = new PushNotificationService.PushState("public", "private",
            [new(QuasarAuthSchemes.TrustedNetwork, QuasarAuthSchemes.TrustedNetwork, "https://push.example/fixture", "key", "auth", null)],
            [new(QuasarAuthSchemes.TrustedNetwork, QuasarAuthSchemes.TrustedNetwork, new() { Updates = false })]);
        await AtomicFileWriter.WriteTextAsync(PushPath, _protection.CreateProtector("Quasar.PushNotifications.v1")
            .Protect(JsonSerializer.Serialize(state, Json)));
    }
    private PushNotificationService Push(AnnouncementService service, Func<UpdateNotice, CancellationToken, Task> send) =>
        Own(new PushNotificationService(_protection,
            new QuasarRoleMapper(new QuasarAuthOptions { TrustedNetworkBypass = new() { Roles = [QuasarRoles.Viewer] } }, null!),
            service, PushPath, send));
    private static ClaimsPrincipal Viewer => new(new ClaimsIdentity([
        new Claim(ClaimTypes.Role, QuasarRoles.Viewer), new Claim(QuasarClaimTypes.Provider, QuasarAuthSchemes.TrustedNetwork)], "fixture"));

    [Fact]
    public async Task RealFeedAndPushPersistReceiptsWithoutHidingActiveNoticesOrReplayingWithdrawnIds()
    {
        var consent = Consent(); await consent.SaveAsync(true, true, false);
        var message = Message(); Announcement[] items = [message];
        var http = Http((_, _) => Task.FromResult(Snapshot(items)));
        var diagnostics = Diagnostics(consent, http); var service = Service(consent, diagnostics, http);
        await service.PollOnceAsync(); await SeedPushAsync();
        int sends = 0;
        Task Send(UpdateNotice notice, CancellationToken _) { sends++; Assert.Equal("critical", notice.Severity); return Task.CompletedTask; }
        var push = Push(service, Send);
        Assert.Single(await push.GetNoticesAsync(Viewer));
        Assert.Empty(await push.GetNoticesAsync(new()));
        await Task.WhenAll(push.SendPendingAsync(default), push.SendPendingAsync(default)); Assert.Equal(1, sends);
        var restarted = Push(Service(consent, diagnostics, http), Send);
        await restarted.SendPendingAsync(default); Assert.Equal(1, sends); Assert.Single(await restarted.GetNoticesAsync(Viewer));
        items = []; await service.PollOnceAsync(); await push.SendPendingAsync(default);
        items = [message]; await service.PollOnceAsync(); await push.SendPendingAsync(default); Assert.Equal(1, sends);
        await consent.SaveAsync(false); Assert.Empty(await push.GetNoticesAsync(Viewer));
        await consent.SaveAsync(true, true, false); await service.PollOnceAsync();
        await Push(service, Send).SendPendingAsync(default); Assert.Equal(1, sends);
    }

    [Fact]
    public async Task FailedPushAttemptIsDurablyReservedAndExpiryPreventsLateDelivery()
    {
        var consent = Consent(); await consent.SaveAsync(true, true, false);
        var message = Message() with { ExpiresAtUtc = _now.AddSeconds(2) };
        var http = Http((_, _) => Task.FromResult(Snapshot(message)));
        var service = Service(consent, Diagnostics(consent, http), http); await service.PollOnceAsync(); await SeedPushAsync();
        int attempts = 0;
        var push = Push(service, (_, _) => { attempts++; throw new IOException("fixture failure"); });
        await push.SendPendingAsync(default);
        await Push(service, (_, _) => { attempts++; return Task.CompletedTask; }).SendPendingAsync(default);
        Assert.Equal(1, attempts); Assert.Single(await push.GetNoticesAsync(Viewer));
        var notice = Assert.Single(service.GetNotices()); _now = message.ExpiresAtUtc;
        Assert.Null(service.StartDelivery(notice, _ => throw new Exception("expired"), default));
        Assert.Empty(await push.GetNoticesAsync(Viewer));
    }

    [Fact]
    public async Task BoundsRejectBothDeclaredAndStreamingOversizeResponses()
    {
        using var declared = new ByteArrayContent([]); declared.Headers.ContentLength = AnnouncementService.MaxResponseBytes + 1;
        await Assert.ThrowsAsync<InvalidDataException>(() => AnnouncementService.ReadSnapshotAsync(declared, default));
        using var streamed = new StreamingContent(new byte[AnnouncementService.MaxResponseBytes + 1]);
        Assert.Null(streamed.Headers.ContentLength);
        await Assert.ThrowsAsync<InvalidDataException>(() => AnnouncementService.ReadSnapshotAsync(streamed, default));
    }

    public static IEnumerable<object[]> Malformed()
    {
        string[] fields = ["id", "severity", "title", "text", "link", "publishedAtUtc", "expiresAtUtc"];
        foreach (var field in fields) yield return [field, "missing"];
        yield return ["id", "BAD"]; yield return ["severity", "error"]; yield return ["title", new string('x', 161)];
        yield return ["text", new string('x', 4001)]; yield return ["text", " "]; yield return ["title", " "]; yield return ["text", "null"];
        yield return ["link", "javascript:alert(1)"]; yield return ["link", "http://example.org/"];
        yield return ["link", "/relative"]; yield return ["link", "https://example.org/has space"]; yield return ["link", "https://user:secret@example.org/"];
        yield return ["link", "https://example.org/" + new string('x', 2048)];
        yield return ["publishedAtUtc", "nonsense"]; yield return ["publishedAtUtc", "2026-10-21T10:00:00"]; yield return ["publishedAtUtc", "2026-10-21T10:00:00+01:00"];
        yield return ["expiresAtUtc", "2020-01-01T00:00:00Z"];
    }
    [Theory] [MemberData(nameof(Malformed))]
    public async Task MalformedFieldsAreRejected(string field, string value)
    {
        var node = JsonSerializer.SerializeToNode(Message(), Json)!.AsObject();
        if (value == "missing") node.Remove(field); else node[field] = value == "null" ? null : JsonValue.Create(value);
        using var content = new StringContent(new JsonObject { ["schemaVersion"] = 1, ["announcements"] = new JsonArray(node) }.ToJsonString());
        var error = await Assert.ThrowsAnyAsync<Exception>(() => AnnouncementService.ReadSnapshotAsync(content, default));
        Assert.True(error is JsonException or InvalidDataException, error.GetType().Name);
    }

    [Theory]
    [InlineData("null")] [InlineData("{}")] [InlineData("{\"schemaVersion\":2,\"announcements\":[]}")]
    [InlineData("{\"schemaVersion\":1,\"announcements\":null}")] [InlineData("{\"schemaVersion\":1,\"announcements\":[null]}")]
    public async Task MalformedEnvelopesAreRejected(string json)
    {
        using var content = new StringContent(json);
        var error = await Assert.ThrowsAnyAsync<Exception>(() => AnnouncementService.ReadSnapshotAsync(content, default));
        Assert.True(error is JsonException or InvalidDataException, error.GetType().Name);
    }

    [Fact]
    public async Task DuplicateIdsAndCountOverflowRejectedButMaximumEscapedPayloadAccepted()
    {
        var message = Message(); using var duplicate = Snapshot(message, message);
        await Assert.ThrowsAsync<InvalidDataException>(() => AnnouncementService.ReadSnapshotAsync(duplicate.Content, default));
        using var tooMany = Snapshot(Enumerable.Range(0, 33).Select(_ => Message()).ToArray());
        await Assert.ThrowsAsync<InvalidDataException>(() => AnnouncementService.ReadSnapshotAsync(tooMany.Content, default));
        using var max = Snapshot(Enumerable.Range(0, 32).Select(_ => Message() with
        { Title = new string('<', 160), Text = new string('<', 4000), Link = "https://example.org/" + new string('x', 2048 - "https://example.org/".Length) }).ToArray());
        Assert.Equal(32, (await AnnouncementService.ReadSnapshotAsync(max.Content, default)).Length);
    }

    [Fact]
    public async Task ExistingNotificationCardEscapesTextAndUsesCriticalStylingAndSafeLink()
    {
        var consent = Consent(); await consent.SaveAsync(true, true, false);
        var http = Http((_, _) => Task.FromResult(Snapshot(Message("info"), Message("critical"), Message("warning"))));
        var service = Service(consent, Diagnostics(consent, http), http); await service.PollOnceAsync();
        var notices = PushNotificationService.MergeAnnouncements([new("update", "Update", "Body", "/settings/updates")], service);
        Assert.Equal("critical", notices[0].Severity);
        await using var context = new BunitContext();
        var bell = context.Render<NotificationBell>(p => p.Add(c => c.Notice, notices[0]));
        Assert.Contains("quasar-text-error", bell.Find("a").ClassList);
        Assert.Equal("/notifications", bell.Find("a").GetAttribute("href"));
        Assert.Contains("CRITICAL", bell.Find("a").GetAttribute("title"));
        var card = context.Render<NotificationCard>(p => p.Add(c => c.Notice, notices[0]));
        Assert.Empty(card.FindAll("script, img")); Assert.Contains("<script>urgent</script>", card.Find("h2").TextContent);
        Assert.Contains("quasar-notice-error", card.Find("section").ClassList);
        Assert.Contains("CRITICAL announcement", card.Markup);
        Assert.Equal("https://example.org/details?x=1&y=2", card.Find("a").GetAttribute("href"));
        Assert.Equal("noopener noreferrer", card.Find("a").GetAttribute("rel"));
        card.Render(p => p.Add(c => c.Notice, notices[0] with { Url = "/notifications" })); Assert.Empty(card.FindAll("a"));
    }

    [Fact]
    public void PushPreviewFitsTransportBoundEvenWithMaximumEscapedText()
    {
        var notice = new UpdateNotice(AnnouncementService.KeyPrefix + Guid.NewGuid().ToString("N"),
            new string('<', 160), new string('<', 4000), "https://external.example/", Severity: "critical");
        var payload = PushNotificationService.CreatePayload(notice);
        Assert.True(Encoding.UTF8.GetByteCount(payload) < 3800);
        using var json = JsonDocument.Parse(payload);
        Assert.Equal("/notifications", json.RootElement.GetProperty("url").GetString());
        Assert.Equal("CRITICAL: " + notice.Title, json.RootElement.GetProperty("title").GetString());
        Assert.Equal(401, json.RootElement.GetProperty("body").GetString()!.Length);
    }

    private sealed class StreamingContent(byte[] bytes) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask();
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new MemoryStream(bytes));
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
    public void Dispose()
    {
        foreach (var item in Enumerable.Reverse(_owned)) item.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
