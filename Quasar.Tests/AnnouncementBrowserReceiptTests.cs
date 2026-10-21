using System.Collections.Concurrent;
using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;
using Quasar.Services;
using Quasar.Services.Auth;
using Quasar.Services.Updates;
using Xunit;

namespace Quasar.Tests;

public sealed partial class AnnouncementTests
{
    private const string ReceiptEndpoint = "https://fcm.googleapis.com/fcm/send/receipt-fixture";
    private static string ReceiptKey() => AnnouncementService.KeyPrefix + Guid.NewGuid().ToString("N");
    private static PushNotificationService.PushState ReceiptState(string[] keys, string? last = null) => new("public", "private",
        [new(QuasarAuthSchemes.TrustedNetwork, QuasarAuthSchemes.TrustedNetwork, ReceiptEndpoint, "key", "auth", last, keys)],
        [new(QuasarAuthSchemes.TrustedNetwork, QuasarAuthSchemes.TrustedNetwork, new() { Updates = false })]);
    private async Task WriteReceiptStateAsync(PushNotificationService.PushState state) =>
        await AtomicFileWriter.WriteTextAsync(PushPath, _protection.CreateProtector("Quasar.PushNotifications.v1").Protect(JsonSerializer.Serialize(state, Json)));
    private async Task<PushNotificationService.PushState> ReadReceiptStateAsync() => JsonSerializer.Deserialize<PushNotificationService.PushState>(
        _protection.CreateProtector("Quasar.PushNotifications.v1").Unprotect(await File.ReadAllTextAsync(PushPath)), Json)!;
    private PushNotificationService ReceiptPush(AnnouncementService service, Func<UpdateNotice, CancellationToken, Task> send,
        ReceiptLogger? logger = null, Func<UpdateNotice[]>? ordinary = null, IDataProtectionProvider? protection = null) =>
        Own(new PushNotificationService(protection ?? _protection,
            new QuasarRoleMapper(new QuasarAuthOptions { TrustedNetworkBypass = new() { Roles = [QuasarRoles.Viewer] } }, null!),
            service, PushPath, send, logger, ordinary));

    [Fact]
    public async Task BrowserAdmissionReachesExactCapUnderConcurrentSweepsWithoutEvictionOrRestartReplay()
    {
        var consent = Consent(); await consent.SaveAsync(true, true, false);
        var messages = new[] { Message(), Message() };
        var http = Http((_, _) => Task.FromResult(Snapshot(messages)));
        var service = Service(consent, Diagnostics(consent, http), http); await service.PollOnceAsync();
        var original = Enumerable.Range(0, PushNotificationService.MaxAnnouncementReceiptsPerBrowser - 1).Select(_ => ReceiptKey()).ToArray();
        await WriteReceiptStateAsync(ReceiptState(original));
        var sent = new ConcurrentBag<string>(); var logger = new ReceiptLogger();
        var push = ReceiptPush(service, (notice, _) => { sent.Add(notice.Key); return Task.CompletedTask; }, logger);
        await Task.WhenAll(push.SendPendingAsync(default), push.SendPendingAsync(default));
        var delivered = Assert.Single(sent);
        var stored = Assert.Single((await ReadReceiptStateAsync()).Subscriptions);
        var keys = PushNotificationService.DeliveredKeys(stored);
        Assert.Equal(PushNotificationService.MaxAnnouncementReceiptsPerBrowser, keys.Length);
        Assert.All(original, key => Assert.Contains(key, keys)); Assert.Contains(delivered, keys);
        Assert.Single(logger.Warnings);
        Assert.Equal(2, (await push.GetNoticesAsync(Viewer)).Length);
        var ciphertext = await File.ReadAllTextAsync(PushPath);
        await consent.SaveAsync(false); await consent.SaveAsync(true, true, false); await service.PollOnceAsync();
        await ReceiptPush(service, (notice, _) => { sent.Add(notice.Key); return Task.CompletedTask; }).SendPendingAsync(default);
        Assert.Single(sent); Assert.Equal(ciphertext, await File.ReadAllTextAsync(PushPath));
        var restartedKeys = PushNotificationService.DeliveredKeys(Assert.Single((await ReadReceiptStateAsync()).Subscriptions)).ToHashSet();
        Assert.All(original, key => Assert.Contains(key, restartedKeys));
    }

    [Fact]
    public async Task FullBrowserHistoryDoesNotStarveOrChangeOrdinaryPushesAndRefreshKeepsAcceptedIds()
    {
        var consent = Consent(); await consent.SaveAsync(true, true, false);
        var messages = Enumerable.Range(0, 32).Select(_ => Message()).ToArray();
        var http = Http((_, _) => Task.FromResult(Snapshot(messages)));
        var service = Service(consent, Diagnostics(consent, http), http); await service.PollOnceAsync();
        var history = Enumerable.Range(0, PushNotificationService.MaxAnnouncementReceiptsPerBrowser).Select(_ => ReceiptKey()).ToArray();
        await WriteReceiptStateAsync(ReceiptState(history));
        var ordinary = new UpdateNotice("ordinary:fixture", "Update", "Available", "/settings/updates");
        var sent = new List<UpdateNotice>(); var logger = new ReceiptLogger();
        var push = ReceiptPush(service, (notice, _) => { sent.Add(notice); return Task.CompletedTask; }, logger, () => [ordinary]);
        await push.SendPendingAsync(default); await push.SendPendingAsync(default);
        Assert.Equal(ordinary, Assert.Single(sent)); Assert.Single(logger.Warnings);
        var state = await ReadReceiptStateAsync(); var stored = Assert.Single(state.Subscriptions);
        Assert.All(history, key => Assert.Contains(key, PushNotificationService.DeliveredKeys(stored)));
        Assert.Contains(ordinary.Key, PushNotificationService.DeliveredKeys(stored));
        Assert.True(PushNotificationService.ReceiptStateWithinBounds(state));
        var valid = new BrowserPushSubscription(ReceiptEndpoint,
            Convert.ToBase64String(new byte[] { 4 }.Concat(new byte[64]).ToArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_'),
            Convert.ToBase64String(new byte[16]).TrimEnd('=').Replace('+', '-').Replace('/', '_'));
        await push.SubscribeAsync(Viewer, valid); // Refreshing browser keys must retain its history.
        var refreshed = Assert.Single((await ReadReceiptStateAsync()).Subscriptions);
        Assert.Equal(PushNotificationService.DeliveredKeys(stored), PushNotificationService.DeliveredKeys(refreshed));
        await ReceiptPush(service, (notice, _) => { sent.Add(notice); return Task.CompletedTask; }, ordinary: () => [ordinary]).SendPendingAsync(default);
        Assert.Single(sent);
        await push.UnsubscribeAsync(Viewer, ReceiptEndpoint); // Only explicit removal releases this lifetime cap.
        Assert.Empty((await ReadReceiptStateAsync()).Subscriptions);
    }

    [Theory]
    [InlineData("over-cap")] [InlineData("invalid-id")] [InlineData("null-key")] [InlineData("too-many-keys")]
    public async Task ReadableLegacyReceiptDamagePausesAnnouncementsButPreservesSubscriptionsAndOrdinaryDelivery(string kind)
    {
        var consent = Consent(); await consent.SaveAsync(true, true, false);
        var message = Message(); var http = Http((_, _) => Task.FromResult(Snapshot(message)));
        var service = Service(consent, Diagnostics(consent, http), http); await service.PollOnceAsync();
        var accepted = ReceiptKey();
        string[] history = kind switch
        {
            "over-cap" => new[] { accepted }.Concat(Enumerable.Range(0, PushNotificationService.MaxAnnouncementReceiptsPerBrowser).Select(_ => ReceiptKey())).ToArray(),
            "invalid-id" => [accepted, "announcement:not-a-guid"],
            "null-key" => [accepted, null!],
            _ => Enumerable.Repeat(accepted, PushNotificationService.MaxReceiptKeysPerBrowser + 1).ToArray(),
        };
        await WriteReceiptStateAsync(ReceiptState(history)); var before = await File.ReadAllBytesAsync(PushPath);
        var ordinary = new UpdateNotice("ordinary:legacy", "Update", "Available", "/settings/updates");
        var sent = new List<UpdateNotice>(); var logger = new ReceiptLogger();
        var push = ReceiptPush(service, (notice, _) => { sent.Add(notice); return Task.CompletedTask; }, logger, () => [ordinary]);
        Assert.True(await push.IsRegisteredAsync(Viewer, ReceiptEndpoint)); Assert.Equal("public", await push.GetPublicKeyAsync());
        Assert.False((await push.GetPreferencesAsync(Viewer)).Updates);
        await push.SendPendingAsync(default); await push.SendPendingAsync(default);
        Assert.Equal(ordinary, Assert.Single(sent)); Assert.Single(logger.Warnings);
        Assert.Equal(before, await File.ReadAllBytesAsync(PushPath));
        Assert.Equal(2, (await push.GetNoticesAsync(Viewer)).Length);
        var restarted = ReceiptPush(service, (_, _) => throw new Exception("New announcement must stay paused"));
        await restarted.SendPendingAsync(default);
        Assert.Equal(before, await File.ReadAllBytesAsync(PushPath));
    }

    [Theory]
    [InlineData("oversize")] [InlineData("corrupt-protection")] [InlineData("corrupt-json")]
    public async Task UnreadablePushStateHasByteBoundNeverResetsAndKeepsAnnouncementFeed(string kind)
    {
        var consent = Consent(); await consent.SaveAsync(true, true, false);
        var message = Message(); var http = Http((_, _) => Task.FromResult(Snapshot(message)));
        var service = Service(consent, Diagnostics(consent, http), http); await service.PollOnceAsync();
        string content = kind switch
        {
            "oversize" => new string('x', PushNotificationService.MaxStateBytes + 1),
            "corrupt-protection" => "invalid-protected-state",
            _ => _protection.CreateProtector("Quasar.PushNotifications.v1").Protect("{invalid"),
        };
        await File.WriteAllTextAsync(PushPath, content);
        var logger = new ReceiptLogger();
        var push = ReceiptPush(service, (_, _) => throw new Exception("No transport is safe without readable subscriptions"), logger);
        await push.SendPendingAsync(default); await push.SendPendingAsync(default);
        Assert.Single(await push.GetNoticesAsync(Viewer)); Assert.Single(logger.Warnings);
        await Assert.ThrowsAsync<InvalidDataException>(() => push.GetPublicKeyAsync());
        Assert.Equal(content, await File.ReadAllTextAsync(PushPath));
    }

    [Fact]
    public async Task DecryptedPushJsonHasIndependentByteBoundAndOriginalFileIsPreserved()
    {
        var consent = Consent(); await consent.SaveAsync(true, true, false);
        var http = Http((_, _) => Task.FromResult(Snapshot(Message())));
        var service = Service(consent, Diagnostics(consent, http), http); await service.PollOnceAsync();
        var protection = new OversizedJsonProtector();
        var content = protection.Protect("tiny encrypted envelope");
        await File.WriteAllTextAsync(PushPath, content);
        var logger = new ReceiptLogger();
        var push = ReceiptPush(service, (_, _) => throw new Exception("Oversize plaintext must not reach transport"), logger, protection: protection);
        await push.SendPendingAsync(default); Assert.Single(await push.GetNoticesAsync(Viewer));
        Assert.Single(logger.Warnings); Assert.Equal(content, await File.ReadAllTextAsync(PushPath));
    }

    [Fact]
    public async Task LegacyLastNoticeKeyAndMixedArrayHistoryBothRemainDeduplicated()
    {
        var consent = Consent(); await consent.SaveAsync(true, true, false);
        var message = Message(); var http = Http((_, _) => Task.FromResult(Snapshot(message)));
        var service = Service(consent, Diagnostics(consent, http), http); await service.PollOnceAsync();
        var key = AnnouncementService.KeyPrefix + message.Id;
        foreach (var array in new string[]?[] { null, [ReceiptKey()] })
        {
            var state = ReceiptState([]);
            state = state with { Subscriptions = [state.Subscriptions[0] with { LastNoticeKey = key, DeliveredNoticeKeys = array }] };
            await WriteReceiptStateAsync(state);
            var push = ReceiptPush(service, (_, _) => throw new Exception("Accepted legacy ID must not replay"));
            await push.SendPendingAsync(default);
            Assert.True(await push.IsRegisteredAsync(Viewer, ReceiptEndpoint));
            Assert.Single(await push.GetNoticesAsync(Viewer));
            var accepted = PushNotificationService.DeliveredKeys(Assert.Single((await ReadReceiptStateAsync()).Subscriptions));
            Assert.Contains(key, accepted);
            if (array is not null) Assert.Contains(array[0], accepted);
        }
    }

    [Fact]
    public async Task GlobalPersistedReceiptCountPreventsNewReservationWithoutDroppingAnyBrowserHistory()
    {
        var consent = Consent(); await consent.SaveAsync(true, true, false);
        var http = Http((_, _) => Task.FromResult(Snapshot(Message())));
        var service = Service(consent, Diagnostics(consent, http), http); await service.PollOnceAsync();
        var state = ReceiptState([]);
        state = state with { Subscriptions = Enumerable.Range(0,
            PushNotificationService.MaxReceiptKeys / PushNotificationService.MaxAnnouncementReceiptsPerBrowser).Select(index =>
                state.Subscriptions[0] with { Endpoint = ReceiptEndpoint + index, DeliveredNoticeKeys = Enumerable.Range(0,
                    PushNotificationService.MaxAnnouncementReceiptsPerBrowser - 1).Select(_ => ReceiptKey()).Append("ordinary:retained").ToArray() }).ToList() };
        await WriteReceiptStateAsync(state); Assert.True(PushNotificationService.ReceiptStateWithinBounds(state));
        var before = await File.ReadAllBytesAsync(PushPath); var logger = new ReceiptLogger();
        int ordinarySends = 0;
        var ordinary = new UpdateNotice("ordinary:global-cap", "Update", "Available", "/settings/updates");
        var push = ReceiptPush(service, (notice, _) =>
        {
            Assert.False(notice.IsAnnouncement); ordinarySends++; return Task.CompletedTask;
        }, logger, () => [new("ordinary:retained", "Already delivered", "Retain this active ordinary receipt", "/"), ordinary]);
        await push.SendPendingAsync(default); await push.SendPendingAsync(default);
        Assert.Equal(state.Subscriptions.Count, ordinarySends); Assert.Single(logger.Warnings);
        Assert.Equal(before, await File.ReadAllBytesAsync(PushPath));
        Assert.True(before.Length < PushNotificationService.MaxStateBytes);
    }

    [Fact]
    public async Task PersistedCiphertextByteBoundRejectsAdmissionBeforeAnySendOrRewrite()
    {
        var consent = Consent(); await consent.SaveAsync(true, true, false);
        var http = Http((_, _) => Task.FromResult(Snapshot(Message())));
        var service = Service(consent, Diagnostics(consent, http), http); await service.PollOnceAsync();
        var state = ReceiptState([]); var protector = _protection.CreateProtector("Quasar.PushNotifications.v1");
        int payload = PushNotificationService.MaxStateBytes * 3 / 4 - 1024;
        string encrypted = "";
        // Data Protection adds fixed framing and base64 expansion. Find a valid file just below the bound.
        for (int attempt = 0; attempt < 16; attempt++)
        {
            var candidate = state with { Subscriptions = [state.Subscriptions[0] with { Auth = new string('x', payload) }] };
            encrypted = protector.Protect(JsonSerializer.Serialize(candidate, Json));
            int remaining = PushNotificationService.MaxStateBytes - Encoding.UTF8.GetByteCount(encrypted);
            if (remaining is >= 0 and < 48) break;
            payload += remaining > 0 ? Math.Max(1, remaining * 3 / 4 - 8) : -16;
        }
        Assert.InRange(Encoding.UTF8.GetByteCount(encrypted), PushNotificationService.MaxStateBytes - 47, PushNotificationService.MaxStateBytes);
        await File.WriteAllTextAsync(PushPath, encrypted); var logger = new ReceiptLogger();
        var push = ReceiptPush(service, (_, _) => throw new Exception("Reservation must fit before transport starts"), logger);
        await push.SendPendingAsync(default); await push.SendPendingAsync(default);
        Assert.Single(logger.Warnings); Assert.Single(await push.GetNoticesAsync(Viewer));
        Assert.Equal(encrypted, await File.ReadAllTextAsync(PushPath));
        Assert.Empty(PushNotificationService.DeliveredKeys(Assert.Single((await ReadReceiptStateAsync()).Subscriptions)));
    }

    private sealed class ReceiptLogger : ILogger<PushNotificationService>
    {
        public ConcurrentQueue<string> Warnings { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        { if (level >= LogLevel.Warning) Warnings.Enqueue(formatter(state, exception)); }
    }
    private sealed class OversizedJsonProtector : IDataProtectionProvider, IDataProtector
    {
        public IDataProtector CreateProtector(string purpose) => this;
        public byte[] Protect(byte[] plaintext) => plaintext;
        public byte[] Unprotect(byte[] protectedData) => new byte[PushNotificationService.MaxStateBytes + 1];
    }
}
