using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Magnetar.Protocol.Runtime;

namespace Quasar.Services.Updates;

internal sealed record Announcement(string Id, string Severity, string Title, string Text, string? Link,
    DateTimeOffset PublishedAtUtc, DateTimeOffset ExpiresAtUtc);

/// <summary>Consent-fenced full snapshots for the existing notification feed.</summary>
public sealed class AnnouncementService : BackgroundService
{
    internal const int MaxResponseBytes = 2 * 1024 * 1024;
    internal const string KeyPrefix = "announcement:";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new UtcTimestampConverter() },
    };
    private readonly DataHandlingConsentCatalog _consent;
    private readonly DiagnosticsUplinkService _diagnostics;
    private readonly HttpClient _http;
    private readonly ILogger<AnnouncementService> _logger;
    private readonly string _path;
    private readonly Func<DateTimeOffset> _now;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _pollGate = new(1, 1);
    private readonly SemaphoreSlim _wake = new(0, 1);
    private CancellationTokenSource _changes = new();
    private CachedSnapshot? _snapshot;
    private bool _disposed;
    public event Action? Changed;

    public AnnouncementService(DataHandlingConsentCatalog consent, DiagnosticsUplinkService diagnostics,
        IHttpClientFactory factory, ILogger<AnnouncementService> logger)
        : this(consent, diagnostics, factory.CreateClient("Announcements"), logger,
            Path.Combine(MagnetarPaths.GetQuasarDirectory(), "Announcements.json")) { }

    internal AnnouncementService(DataHandlingConsentCatalog consent, DiagnosticsUplinkService diagnostics,
        HttpClient http, ILogger<AnnouncementService> logger, string path, Func<DateTimeOffset>? now = null)
    {
        (_consent, _diagnostics, _http, _logger, _path, _now) = (consent, diagnostics, http, logger, path, now ?? (() => DateTimeOffset.UtcNow));
        // Subscribe before reading. All snapshot access uses the same consent -> snapshot lock order.
        consent.Changed += ConsentChanged;
        consent.WithSettings(settings =>
        {
            lock (_sync)
            {
                try
                {
                    if (File.Exists(path))
                    {
                        if (new FileInfo(path).Length > MaxResponseBytes) throw new InvalidDataException("Announcement cache exceeds limit.");
                        var cache = JsonSerializer.Deserialize<CachedSnapshot>(File.ReadAllBytes(path), Json);
                        if (cache is not null && Matches(cache, settings))
                        {
                            Validate(cache.Announcements);
                            _snapshot = cache;
                        }
                    }
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
                { logger.LogWarning("Announcement cache unavailable ({FailureType}).", error.GetType().Name); }
                return true;
            }
        });
    }

    private bool Matches(CachedSnapshot cache, DataHandlingConsentSettings settings) => settings.AllowsAnnouncements
        && _diagnostics.BackofficeUrl?.Scheme == Uri.UriSchemeHttps
        && cache.Origin == _diagnostics.BackofficeUrl.AbsoluteUri
        && cache.Generation == settings.Generation && cache.Decision == settings.DecisionDateUtc
        && cache.FetchedAtUtc <= _now() && cache.FetchedAtUtc.AddMinutes(6) > _now();

    internal UpdateNotice[] GetNotices() => _consent.WithSettings(settings =>
    {
        lock (_sync)
            return _snapshot is { } cache && Matches(cache, settings)
                ? cache.Announcements.Where(a => a.ExpiresAtUtc > _now()).Select(ToNotice).ToArray() : [];
    });

    internal bool IsCurrent(UpdateNotice notice) => !notice.Key.StartsWith(KeyPrefix, StringComparison.Ordinal)
        || GetNotices().Contains(notice);

    private static UpdateNotice ToNotice(Announcement a) => new(KeyPrefix + a.Id, a.Title, a.Text,
        a.Link ?? "/notifications", a.PublishedAtUtc, a.Severity);

    // The transport must start inside the consent fence, not after an async eligibility check.
    internal Task? StartDelivery(UpdateNotice notice, Func<CancellationToken, Task> send, CancellationToken token) =>
        _consent.WithSettings(settings =>
        {
            lock (_sync)
            {
                if (_disposed || _snapshot is not { } cache || !Matches(cache, settings)
                    || !cache.Announcements.Any(a => a.ExpiresAtUtc > _now() && ToNotice(a) == notice)) return null;
                token.ThrowIfCancellationRequested();
                var expires = cache.Announcements.First(a => KeyPrefix + a.Id == notice.Key).ExpiresAtUtc;
                var remaining = (expires < cache.FetchedAtUtc.AddMinutes(6) ? expires : cache.FetchedAtUtc.AddMinutes(6)) - _now();
                if (remaining <= TimeSpan.Zero) return null;
                var delivery = CancellationTokenSource.CreateLinkedTokenSource(token, _changes.Token);
                delivery.CancelAfter(remaining);
                try { return FinishDeliveryAsync(send(delivery.Token), delivery); }
                catch { delivery.Dispose(); throw; }
            }
        });

    private static async Task FinishDeliveryAsync(Task sending, CancellationTokenSource cancellation)
    {
        using (cancellation) await sending;
    }

    private void ConsentChanged()
    {
        _consent.WithSettings(settings =>
        {
            lock (_sync)
            {
                if (_disposed) return false;
                _changes.Cancel();
                _changes.Dispose();
                _changes = new();
                if (_snapshot is { } cache && !Matches(cache, settings))
                {
                    _snapshot = null;
                    try { File.Delete(_path); }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                    { _logger.LogWarning("Announcement cache cleanup deferred ({FailureType}).", error.GetType().Name); }
                }
                return true;
            }
        });
        Changed?.Invoke();
        if (_wake.CurrentCount == 0) try { _wake.Release(); } catch (SemaphoreFullException) { }
    }

    internal async Task PollOnceAsync(CancellationToken stoppingToken = default)
    {
        await _pollGate.WaitAsync(stoppingToken);
        try
        {
            DataHandlingConsentSettings? captured = null;
            using var cycle = _consent.WithSettings(settings =>
            {
                lock (_sync)
                {
                    if (_disposed || !settings.AllowsAnnouncements || _diagnostics.BackofficeUrl?.Scheme != Uri.UriSchemeHttps) return null;
                    captured = settings;
                    return CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, _changes.Token);
                }
            });
            if (cycle is null) return;
            cycle.CancelAfter(TimeSpan.FromSeconds(30));
            var token = cycle.Token;
            using var request = await _diagnostics.CreateAnnouncementRequestAsync(token);
            if (request is null) return;
            var sending = _consent.WithSettings(settings =>
            {
                token.ThrowIfCancellationRequested();
                if (!SameDecision(settings, captured!)) throw new OperationCanceledException(token);
                return _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            });
            using var response = await sending;
            if (response.StatusCode == HttpStatusCode.Unauthorized) _diagnostics.RejectAnnouncementCredential();
            // Redirects are errors. The named client also disables automatic redirect following.
            if (response.StatusCode != HttpStatusCode.OK) throw new HttpRequestException("Announcement snapshot unavailable.", null, response.StatusCode);
            var items = await ReadSnapshotAsync(response.Content, token);
            _consent.WithSettings(settings =>
            {
                lock (_sync)
                {
                    token.ThrowIfCancellationRequested();
                    if (!SameDecision(settings, captured!)) throw new OperationCanceledException(token);
                    var next = new CachedSnapshot(_diagnostics.BackofficeUrl!.AbsoluteUri, settings.Generation,
                        settings.DecisionDateUtc, _now(), items.Where(a => a.ExpiresAtUtc > _now()).OrderByDescending(a => a.PublishedAtUtc)
                            .ThenBy(a => a.Id, StringComparer.Ordinal).ToArray());
                    AtomicFileWriter.WriteTextAsync(_path, JsonSerializer.Serialize(next, Json), token).GetAwaiter().GetResult();
                    if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(_path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                    _snapshot = next;
                    // Cancel queued/in-flight delivery from the previous snapshot, including withdrawals.
                    _changes.Cancel(); _changes.Dispose(); _changes = new();
                    return true;
                }
            });
            Changed?.Invoke();
        }
        finally { _pollGate.Release(); }
    }

    private static bool SameDecision(DataHandlingConsentSettings current, DataHandlingConsentSettings captured) =>
        current.AllowsAnnouncements && current.Generation == captured.Generation && current.DecisionDateUtc == captured.DecisionDateUtc;

    internal static async Task<Announcement[]> ReadSnapshotAsync(HttpContent content, CancellationToken token)
    {
        if (content.Headers.ContentLength > MaxResponseBytes) throw new InvalidDataException("Announcement response exceeds limit.");
        await using var input = await content.ReadAsStreamAsync(token);
        using var output = new MemoryStream();
        byte[] buffer = new byte[8192];
        int read;
        while ((read = await input.ReadAsync(buffer, token)) != 0)
        {
            if (output.Length + read > MaxResponseBytes) throw new InvalidDataException("Announcement response exceeds limit.");
            output.Write(buffer, 0, read);
        }
        var snapshot = JsonSerializer.Deserialize<WireSnapshot>(output.ToArray(), Json)
            ?? throw new InvalidDataException("Missing announcement snapshot.");
        if (snapshot.SchemaVersion != 1) throw new InvalidDataException("Unsupported announcement schema.");
        Validate(snapshot.Announcements);
        return snapshot.Announcements;
    }

    private static void Validate(Announcement[]? items)
    {
        if (items is null || items.Length > 32) throw new InvalidDataException("Invalid announcement count.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var a in items)
        {
            if (a is null || a.Id is not { Length: 32 } || !Guid.TryParseExact(a.Id, "N", out _) || a.Id != a.Id.ToLowerInvariant()
                || !ids.Add(a.Id) || a.Severity is not ("info" or "warning" or "critical")
                || string.IsNullOrWhiteSpace(a.Title) || a.Title.Length > 160 || a.Title.Any(char.IsControl)
                || string.IsNullOrWhiteSpace(a.Text) || a.Text.Length > 4000
                || a.Text.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t'))
                || a.PublishedAtUtc == default || a.PublishedAtUtc.Offset != TimeSpan.Zero
                || a.ExpiresAtUtc.Offset != TimeSpan.Zero || a.ExpiresAtUtc <= a.PublishedAtUtc
                || a.Link is not null && (a.Link.Length > 2048 || !Uri.TryCreate(a.Link, UriKind.Absolute, out var link)
                    || link.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(link.UserInfo)
                    || a.Link.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c == '\\')))
                throw new InvalidDataException("Invalid announcement fields.");
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await PollOnceAsync(stoppingToken); }
            catch (OperationCanceledException) { }
            catch (Exception error)
            { _logger.LogWarning("Announcement poll deferred ({FailureType}).", error.GetType().Name); }
            // Expiry invalidates open UIs promptly, independently of the five-minute network poll.
            var deadline = _now().AddSeconds(300 + Random.Shared.Next(-30, 31));
            try
            {
                while (_now() < deadline)
                {
                    var delay = deadline - _now();
                    if (delay <= TimeSpan.Zero) break;
                    var expiry = GetNotices().Select(n => n.Key).ToArray();
                    if (await _wake.WaitAsync(delay < TimeSpan.FromSeconds(1) ? delay : TimeSpan.FromSeconds(1), stoppingToken)) break;
                    if (!expiry.SequenceEqual(GetNotices().Select(n => n.Key))) Changed?.Invoke();
                }
            }
            catch (OperationCanceledException) { break; }
        }
    }

    public override void Dispose()
    {
        _consent.Changed -= ConsentChanged;
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _changes.Cancel(); _changes.Dispose();
        }
        base.Dispose();
    }

    private sealed record WireSnapshot(int SchemaVersion, Announcement[] Announcements);
    private sealed record CachedSnapshot(string Origin, long Generation, string? Decision, DateTimeOffset FetchedAtUtc, Announcement[] Announcements);

    private sealed class UtcTimestampConverter : JsonConverter<DateTimeOffset>
    {
        public override DateTimeOffset Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.String || reader.GetString() is not { } text
                || !(text.EndsWith('Z') || text.EndsWith("+00:00", StringComparison.Ordinal))
                || !reader.TryGetDateTimeOffset(out var value) || value.Offset != TimeSpan.Zero)
                throw new JsonException("Announcement timestamps must be explicit UTC ISO timestamps.");
            return value;
        }

        public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value);
    }
}
