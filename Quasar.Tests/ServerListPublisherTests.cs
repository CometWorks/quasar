using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Magnetar.Protocol.Model;
using Magnetar.Protocol.Runtime;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Quasar.Services;
using Quasar.Services.Auth;
using Quasar.Services.ServerList;
using Xunit;

namespace Quasar.Tests;

[Collection("Exact server creation")]
public sealed class ServerListPublisherTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "quasar-list-" + Guid.NewGuid().ToString("N"));
    private readonly FieldInfo _cache = typeof(MagnetarPaths).GetField("_cachedQuasarDirectory", BindingFlags.NonPublic | BindingFlags.Static)!;
    private readonly object? _previous;
    private readonly DataHandlingConsentCatalog _consent;
    private readonly RbacConfigCatalog _rbac;
    private readonly string _installation = Guid.NewGuid().ToString("N");
    private readonly List<(string Path, string Body)> _sent = [];
    private int _collections;

    public ServerListPublisherTests()
    {
        _previous = _cache.GetValue(null);
        _cache.SetValue(null, _root);
        Directory.CreateDirectory(Path.Combine(_root, "Diagnostics"));
        File.WriteAllText(Path.Combine(_root, "Diagnostics", "installation-id"), _installation);
        _consent = new(NullLogger<DataHandlingConsentCatalog>.Instance);
        _rbac = new(NullLogger<RbacConfigCatalog>.Instance);
    }

    private Task GrantAsync() => _consent.SaveAsync(true, true, false);

    private Task SetAdminAsync(string subject = "76561198000000001") => _rbac.SaveAsync(new()
    {
        SubjectRoleMappings = [new() { Provider = QuasarAuthSchemes.Steam, Subject = subject, Roles = [QuasarRoles.Admin] }],
    });

    private ServerListPublisher Publisher(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? send = null,
        Func<CancellationToken, Task<IReadOnlyList<ServerListListing>>>? collect = null)
    {
        var http = new HttpClient(new Handler(async (request, token) =>
        {
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal(_installation, request.Headers.GetValues("X-Installation-Id").Single());
            _sent.Add((request.RequestUri!.AbsolutePath, request.Content is null ? "" : await request.Content.ReadAsStringAsync(token)));
            if (send is not null) return await send(request, token);
            return request.Method == HttpMethod.Get
                ? new(HttpStatusCode.OK) { Content = JsonContent.Create(new ServerListChallenge(new string('a', 32),
                    DateTimeOffset.UtcNow.AddSeconds(25), _consent.GetSettings().Generation)) }
                : new(HttpStatusCode.NoContent);
        }));
        return new(new() { Enabled = true, DirectoryUrl = "https://directory.example/", InstallationToken = "private-token" },
            _consent, _rbac, http, collect ?? (_ =>
            {
                _collections++;
                return Task.FromResult<IReadOnlyList<ServerListListing>>([new(Guid.NewGuid().ToString("N"),
                    "Public server", "play.example", 27016, DateTimeOffset.UtcNow, true, 3, 16, 1)]);
            }), _root, NullLogger<ServerListPublisher>.Instance);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyConsentCannotPublish(bool? decision)
    {
        if (decision.HasValue) await _consent.SaveAsync(decision.Value);
        await SetAdminAsync();
        using var publisher = Publisher();
        await publisher.PublishOnceAsync(default);
        Assert.Equal("/v1/quasar/withdraw", Assert.Single(_sent).Path);
        Assert.Equal(0, _collections);
        Assert.DoesNotContain("Public server", _sent[0].Body);
    }

    [Fact]
    public async Task MasterConsentStillRequiredWhenDiagnosticsGranted()
    {
        await _consent.SaveAsync(false, true, false);
        await SetAdminAsync();
        using var publisher = Publisher();
        await publisher.PublishOnceAsync(default);
        Assert.Equal("/v1/quasar/withdraw", Assert.Single(_sent).Path);
    }

    [Fact]
    public async Task PublishesOnlyAfterDiagnosticsVerificationThenWithdraws()
    {
        await GrantAsync();
        await SetAdminAsync();
        using var publisher = Publisher();
        await publisher.PublishOnceAsync(default);
        Assert.Equal(["/v1/quasar/challenge", "/v1/quasar/publication"], _sent.Select(x => x.Path));
        using var body = JsonDocument.Parse(_sent[1].Body);
        Assert.Equal(_installation, body.RootElement.GetProperty("installationId").GetString());
        Assert.Equal("76561198000000001", body.RootElement.GetProperty("admins")[0].GetProperty("subject").GetString());
        Assert.Equal(_consent.GetSettings().Generation, body.RootElement.GetProperty("diagnosticsGeneration").GetInt64());
        await _consent.SaveAsync(false, false, false);
        await publisher.PublishOnceAsync(default);
        using var withdrawal = JsonDocument.Parse(_sent[^1].Body);
        Assert.True(withdrawal.RootElement.GetProperty("sequence").GetInt64() > body.RootElement.GetProperty("sequence").GetInt64());
        Assert.Equal("/v1/quasar/withdraw", _sent[^1].Path);
        Assert.Equal(1, _collections);
    }

    [Theory]
    [InlineData("unavailable")]
    [InlineData("expired")]
    [InlineData("wrong-generation")]
    [InlineData("overlong")]
    [InlineData("oversized")]
    public async Task InvalidOrUnavailableVerifierNeverCollectsOrPublishes(string failure)
    {
        await GrantAsync();
        await SetAdminAsync();
        using var publisher = Publisher((_, _) => Task.FromResult(failure == "unavailable"
            ? new HttpResponseMessage(HttpStatusCode.Forbidden)
            : new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = failure == "oversized" ? new StringContent(new string('a', 4097))
                    : JsonContent.Create(new ServerListChallenge(new string('a', 32),
                        failure == "expired" ? DateTimeOffset.UtcNow.AddSeconds(-1)
                            : DateTimeOffset.UtcNow.AddSeconds(failure == "overlong" ? 120 : 25),
                        _consent.GetSettings().Generation + (failure == "wrong-generation" ? 1 : 0))),
            }));
        await Assert.ThrowsAnyAsync<Exception>(() => publisher.PublishOnceAsync(default));
        Assert.Equal("/v1/quasar/challenge", Assert.Single(_sent).Path);
        Assert.Equal(0, _collections);
    }

    [Fact]
    public async Task ConsentWithdrawalDuringCollectionPreventsSend()
    {
        await GrantAsync();
        await SetAdminAsync();
        using var publisher = Publisher(collect: async _ =>
        {
            await _consent.SaveAsync(false, false, false);
            return [];
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => publisher.PublishOnceAsync(default));
        Assert.Equal("/v1/quasar/challenge", Assert.Single(_sent).Path);
        await publisher.PublishOnceAsync(default);
        Assert.Equal("/v1/quasar/withdraw", _sent[^1].Path);
    }

    [Fact]
    public async Task RevocationCancelsInFlightPublication()
    {
        await GrantAsync();
        await SetAdminAsync();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var publisher = Publisher(async (request, token) =>
        {
            if (request.Method == HttpMethod.Get)
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(new ServerListChallenge(new string('a', 32),
                    DateTimeOffset.UtcNow.AddSeconds(25), _consent.GetSettings().Generation)) };
            if (request.Method == HttpMethod.Put)
            {
                started.SetResult();
                await Task.Delay(Timeout.Infinite, token);
            }
            return new(HttpStatusCode.NoContent);
        });
        var sending = publisher.PublishOnceAsync(default);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await _consent.SaveAsync(false, false, false);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sending);
        await publisher.PublishOnceAsync(default);
        Assert.Equal("/v1/quasar/withdraw", _sent[^1].Path);
    }

    [Fact]
    public async Task RemovedAdminIsNotSentAgainAndRestartFencesEarlierTraffic()
    {
        await GrantAsync();
        await SetAdminAsync();
        using (var first = Publisher())
        {
            await first.PublishOnceAsync(default);
            await SetAdminAsync("76561198000000002");
            await first.PublishOnceAsync(default);
            Assert.DoesNotContain("76561198000000001", _sent[^1].Body);
            Assert.Contains("76561198000000002", _sent[^1].Body);
        }
        using var before = JsonDocument.Parse(_sent[^1].Body);
        using var next = Publisher();
        await next.PublishOnceAsync(default);
        using var after = JsonDocument.Parse(_sent[^1].Body);
        Assert.True(after.RootElement.GetProperty("epoch").GetInt64() > before.RootElement.GetProperty("epoch").GetInt64());
    }

    [Fact]
    public async Task ExternalInvalidConsentFailsClosedAndStatisticsSavePreservesDiagnostics()
    {
        await GrantAsync();
        long generation = _consent.GetSettings().Generation;
        await _consent.SaveAsync(false);
        Assert.True(_consent.GetSettings().DiagnosticsGranted);
        Assert.Equal(generation, _consent.GetSettings().Generation);
        File.WriteAllText(_consent.SettingsPath, "broken json");
        _consent.ReloadFromDisk();
        Assert.False(ServerListProtocol.HasConsent(_consent.GetSettings()));
        using var publisher = Publisher();
        await publisher.PublishOnceAsync(default);
        Assert.Equal("/v1/quasar/withdraw", Assert.Single(_sent).Path);
    }

    [Fact]
    public async Task MissingDiagnosticsIdentityCannotEnrollOrPublish()
    {
        await GrantAsync();
        await SetAdminAsync();
        File.Delete(Path.Combine(_root, "Diagnostics", "installation-id"));
        using var publisher = Publisher();
        await publisher.PublishOnceAsync(default);
        Assert.Empty(_sent);
        Assert.Equal(0, _collections);
    }

    [Fact]
    public async Task NoTransferableAdminWithdrawsEvenWithConsent()
    {
        await GrantAsync();
        await _rbac.SaveAsync(new() { SubjectRoleMappings =
            [new() { Provider = "Oidc", Subject = "operator", Roles = ["admin"] }] });
        using var publisher = Publisher();
        await publisher.PublishOnceAsync(default);
        Assert.Equal("/v1/quasar/withdraw", Assert.Single(_sent).Path);
        Assert.Equal(0, _collections);
    }

    [Fact]
    public async Task ExternalRevocationAdvancesDurableDiagnosticsGeneration()
    {
        await GrantAsync();
        var settings = _consent.GetSettings();
        long generation = settings.Generation;
        settings.DiagnosticsGranted = false;
        File.WriteAllText(_consent.SettingsPath, JsonSerializer.Serialize(settings, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        _consent.ReloadFromDisk();
        Assert.False(_consent.GetSettings().DiagnosticsGranted);
        Assert.True(_consent.GetSettings().Generation > generation);
        using var reloaded = new DataHandlingConsentCatalog(NullLogger<DataHandlingConsentCatalog>.Instance, _consent.SettingsPath);
        Assert.Equal(_consent.GetSettings().Generation, reloaded.GetSettings().Generation);
    }

    [Fact]
    public void OnlyExplicitSteamAdminsReceiveDescriptionAuthority()
    {
        var roster = ServerListProtocol.GetAdmins(new() { SubjectRoleMappings =
        [
            new() { Provider = "Steam", Subject = "76561198000000001", Roles = ["admin"] },
            new() { Provider = "Steam", Subject = "76561198000000002", Roles = ["editor"] },
            new() { Provider = "TrustedNetwork", Subject = "76561198000000003", Roles = ["admin"] },
            new() { Provider = "Oidc", Subject = "76561198000000004", Roles = ["admin"] },
        ] });
        Assert.Equal("76561198000000001", Assert.Single(roster).Subject);
    }

    [Fact]
    public void PayloadExcludesPrivateSnapshotAndRejectsStaleOrAmbiguousMetrics()
    {
        var now = DateTimeOffset.UtcNow;
        var entry = new ServerListEntry { ListingId = Guid.NewGuid().ToString("N"), UniqueName = "local",
            Name = "Public", PublicHost = "join.example" };
        var agent = new AgentRuntimeState { IsConnected = true, LastSnapshotReceivedUtc = now,
            Snapshot = new() { UniqueName = "local", HostName = "PRIVATE-HOST", ServerName = "PRIVATE-NAME",
                CapturedAtUtc = now, IsRunning = true, HiddenPlayerSteamIds = [123],
                RecentChat = [new() { Content = "PRIVATE-CHAT" }], Metrics = new() { PlayersOnline = 3, MaxPlayers = 16, SimSpeed = float.NaN } } };
        var payload = ServerListProtocol.FromAgent(entry, [agent], now);
        Assert.Equal(3, payload.PlayersOnline);
        Assert.Null(payload.SimSpeed);
        Assert.DoesNotContain("PRIVATE", JsonSerializer.Serialize(payload));
        Assert.Null(ServerListProtocol.FromAgent(entry, [agent, agent], now).PlayersOnline);
        Assert.Null(ServerListProtocol.FromAgent(entry, [agent], now.AddSeconds(61)).Running);
        Assert.Null(ServerListProtocol.FromAgent(entry, [agent], now.AddSeconds(-1)).Running);
    }

    [Theory]
    [InlineData("http://directory.example")]
    [InlineData("https://secret@directory.example")]
    [InlineData("https://directory.example/?secret=key")]
    [InlineData("https://directory.example/#fragment")]
    public void RejectsInsecureOrCredentialBearingEndpoints(string url)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Quasar:ServerList:Enabled"] = "true", ["Quasar:ServerList:DirectoryUrl"] = url,
            ["Quasar:ServerList:InstallationToken"] = "private-token" }).Build();
        Assert.Throws<InvalidDataException>(() => ServerListOptions.Create(config));
    }

    public void Dispose()
    {
        _consent.Dispose();
        _rbac.Dispose();
        _cache.SetValue(null, _previous);
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token);
    }
}
