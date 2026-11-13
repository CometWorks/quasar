using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
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
    private readonly RbacConfigCatalog _rbac;
    private readonly List<(string Path, string Body)> _sent = [];
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private int _collections;

    public ServerListPublisherTests()
    {
        _previous = _cache.GetValue(null);
        _cache.SetValue(null, _root);
        _rbac = new(NullLogger<RbacConfigCatalog>.Instance);
    }

    private Task SetAdminAsync(string subject = "76561198000000001") => _rbac.SaveAsync(new()
    {
        SubjectRoleMappings = [new() { Provider = QuasarAuthSchemes.Steam, Subject = subject, Roles = [QuasarRoles.Admin] }],
    });

    private ServerListPublisher Publisher(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? send = null,
        Func<CancellationToken, Task<IReadOnlyList<ServerListListing>>>? collect = null, ServerListSettingsCatalog? settings = null,
        bool enabled = true)
    {
        var http = new HttpClient(new Handler(async (request, token) =>
        {
            Assert.Equal(HttpMethod.Put, request.Method);
            Assert.Null(request.Headers.Authorization);
            var envelope = (await request.Content!.ReadFromJsonAsync<SignedServerListPublication>(Json, token))!;
            using var key = RSA.Create(); key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(envelope.PublicKey), out _);
            var payload = Convert.FromBase64String(envelope.Payload);
            byte[] signed = [.. Encoding.ASCII.GetBytes("CometWorks.Directory.Publication.v2\n"), .. payload];
            Assert.True(key.VerifyData(signed,
                Convert.FromBase64String(envelope.Signature), HashAlgorithmName.SHA256, RSASignaturePadding.Pss));
            _sent.Add((request.RequestUri!.AbsolutePath, Encoding.UTF8.GetString(payload)));
            return send is not null ? await send(request, token) : new(HttpStatusCode.NoContent);
        }));
        return new(_rbac, http, (options, token) =>
        {
            if (collect is not null) return collect(token);
            _collections++;
            return Task.FromResult<IReadOnlyList<ServerListListing>>(settings is not null
                ? options.Listings.Where(e => e.Enabled).Select(ServerListProtocol.Unknown).ToArray()
                : [new(Guid.NewGuid().ToString("N"), "Public server", "play.example", 27016, DateTimeOffset.UtcNow, true, 3, 16, 1)]);
        }, _root, NullLogger<ServerListPublisher>.Instance, settings, new() { Enabled = enabled });
    }

    private ServerListPublication Last() => JsonSerializer.Deserialize<ServerListPublication>(_sent[^1].Body, Json)!;
    private ServerListSettingsCatalog Settings() => new(Path.Combine(_root, "server-list.json"),
        new ConfigurationBuilder().Build(), NullLogger<ServerListSettingsCatalog>.Instance);
    private static ServerListEntry Entry(string name = "solo") => new() { UniqueName = name, Name = name, PublicHost = "play.example" };

    [Fact]
    public async Task PublishesDirectlyWithoutDiagnosticsConsentEnrollmentOrAttestation()
    {
        await SetAdminAsync();
        using var publisher = Publisher();
        await publisher.PublishOnceAsync(default);
        Assert.Equal("/v2/listings", Assert.Single(_sent).Path);
        Assert.Single(Last().Listings); Assert.Single(Last().Admins);
        Assert.Equal(2, Last().SchemaVersion);
        Assert.False(Directory.Exists(Path.Combine(_root, "Diagnostics")));
        Assert.DoesNotContain("consent", _sent[0].Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("challenge", _sent[0].Body);
        using var state = JsonDocument.Parse(File.ReadAllText(Path.Combine(_root, "ServerList", "identity.json")));
        using var key = RSA.Create(); key.ImportFromPem(state.RootElement.GetProperty("PrivateKeyPem").GetString());
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(key.ExportSubjectPublicKeyInfo()))[..32], Last().InstallationId);
        if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite,
            File.GetUnixFileMode(Path.Combine(_root, "ServerList", "identity.json")));
    }

    [Fact]
    public async Task NeverSelectedInstallationsDoNotContactTheDirectory()
    {
        using var publisher = Publisher(enabled: false);
        await publisher.PublishOnceAsync(default);
        Assert.Empty(_sent); Assert.False(Directory.Exists(Path.Combine(_root, "ServerList")));
    }

    [Fact]
    public async Task SettingsSelectAddAndWithdrawWithoutChangingIdentityOrDiagnosticConsent()
    {
        await SetAdminAsync();
        var settings = Settings();
        using var publisher = Publisher(settings: settings);
        await publisher.PublishOnceAsync(default);
        Assert.Empty(_sent);
        var solo = Entry();
        await settings.SaveAsync(new() { Enabled = true, Listings = [solo] }, 0);
        await publisher.PublishOnceAsync(default);
        var id = Assert.Single(Last().Listings).ListingId;
        var installation = Last().InstallationId;
        Assert.True(ServerListProtocol.IsId(id));
        await settings.SaveAsync(new() { Enabled = true, Listings = [solo, Entry("fleet")] }, 1);
        await publisher.PublishOnceAsync(default);
        Assert.Equal(2, Last().Listings.Count); Assert.Equal(id, Last().Listings.Single(l => l.Name == "solo").ListingId);
        solo.Enabled = false;
        await settings.SaveAsync(new() { Enabled = true, Listings = [solo, Entry("fleet")] }, 2);
        await publisher.PublishOnceAsync(default);
        Assert.Equal("fleet", Assert.Single(Last().Listings).Name);
        await settings.SaveAsync(new() { Enabled = false, Listings = [solo] }, 3);
        await publisher.PublishOnceAsync(default);
        Assert.Empty(Last().Listings); Assert.Empty(Last().Admins); Assert.Equal(installation, Last().InstallationId);
        Assert.False(File.Exists(Path.Combine(_root, "data-handling-consent.json")));
    }

    [Fact]
    public async Task SettingsChangesCancelAnInFlightUpdateAndTheNextUpdateWithdraws()
    {
        await SetAdminAsync();
        var settings = Settings();
        await settings.SaveAsync(new() { Enabled = true, Listings = [Entry()] }, 0);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var publisher = Publisher(async (_, token) =>
        {
            if (_sent.Count == 1) { started.SetResult(); await Task.Delay(Timeout.Infinite, token); }
            return new(HttpStatusCode.NoContent);
        }, settings: settings);
        var sending = publisher.PublishOnceAsync(default);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await settings.SaveAsync(new(), 1);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sending);
        await publisher.PublishOnceAsync(default);
        Assert.Empty(Last().Listings); Assert.Equal(2, Last().Sequence);
    }

    [Fact]
    public async Task AdminChangesDuringCollectionPreventAnOutdatedSend()
    {
        await SetAdminAsync();
        using var publisher = Publisher(collect: async _ =>
        {
            await SetAdminAsync("76561198000000002");
            return [new(Guid.NewGuid().ToString("N"), "Public", "play.example", 27016, null, null, null, null, null)];
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => publisher.PublishOnceAsync(default));
        Assert.Empty(_sent);
    }

    [Fact]
    public async Task RemovingAdminsCancelsAnInFlightUpdateThenWithdraws()
    {
        await SetAdminAsync();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var publisher = Publisher(async (_, token) =>
        {
            if (_sent.Count == 1) { started.SetResult(); await Task.Delay(Timeout.Infinite, token); }
            return new(HttpStatusCode.NoContent);
        });
        var sending = publisher.PublishOnceAsync(default);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await _rbac.SaveAsync(new() { SubjectRoleMappings =
            [new() { Provider = "Oidc", Subject = "operator", Roles = [QuasarRoles.Admin] }] });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sending);
        await publisher.PublishOnceAsync(default);
        Assert.Empty(Last().Listings); Assert.Empty(Last().Admins);
    }

    [Fact]
    public async Task RestartPreservesTheKeyAndIdentityAndAdvancesTheEpoch()
    {
        await SetAdminAsync();
        using (var first = Publisher()) await first.PublishOnceAsync(default);
        var before = Last();
        var identity = File.ReadAllText(Path.Combine(_root, "ServerList", "identity.json"));
        using var next = Publisher();
        await next.PublishOnceAsync(default);
        Assert.Equal(before.InstallationId, Last().InstallationId);
        Assert.True(Last().Epoch > before.Epoch);
        Assert.Equal(identity, File.ReadAllText(Path.Combine(_root, "ServerList", "identity.json")));
    }

    [Fact]
    public async Task LegacyMachineKeyAndListingIdsAreAdoptedWithoutContactingDiagnostics()
    {
        await SetAdminAsync();
        using var key = RSA.Create(3072);
        var installation = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(Path.Combine(_root, "Diagnostics"));
        File.WriteAllText(Path.Combine(_root, "Diagnostics", "installation-id"), installation);
        File.WriteAllText(Path.Combine(_root, "Diagnostics", "machine-key.pem"), key.ExportPkcs8PrivateKeyPem());
        var settings = Settings();
        await settings.SaveAsync(new() { Enabled = true, Listings = [Entry()] }, 0);
        using var publisher = Publisher(settings: settings);
        await publisher.PublishOnceAsync(default);
        Assert.Equal(installation, Last().InstallationId);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(installation + "\nserver\nsolo"))[..16]),
            Assert.Single(Last().Listings).ListingId);
    }

    [Fact]
    public async Task CorruptSavedIdentityCannotSilentlyRotateOwnership()
    {
        Directory.CreateDirectory(Path.Combine(_root, "ServerList"));
        File.WriteAllText(Path.Combine(_root, "ServerList", "identity.json"), "broken json");
        using var publisher = Publisher();
        await Assert.ThrowsAsync<JsonException>(() => publisher.PublishOnceAsync(default));
        Assert.Empty(_sent);
    }

    [Fact]
    public async Task FailedDeliveryRetriesWithAHigherSequence()
    {
        await SetAdminAsync();
        using var publisher = Publisher((_, _) => Task.FromResult(new HttpResponseMessage(
            _sent.Count == 1 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.NoContent)));
        await Assert.ThrowsAsync<HttpRequestException>(() => publisher.PublishOnceAsync(default));
        await publisher.PublishOnceAsync(default);
        Assert.Equal(2, Last().Sequence);
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
    [InlineData(0f, 0f)]
    [InlineData(0.75f, 0.75f)]
    [InlineData(1f, 1f)]
    [InlineData(1.01f, 1f)]
    [InlineData(float.MaxValue, 1f)]
    [InlineData(-0.01f, null)]
    [InlineData(float.NaN, null)]
    [InlineData(float.PositiveInfinity, null)]
    [InlineData(float.NegativeInfinity, null)]
    public async Task PublishedSimulationSpeedIsCappedAndInvalidReadingsStayUnknown(float speed, float? expected)
    {
        await SetAdminAsync();
        var now = DateTimeOffset.UtcNow;
        var entry = new ServerListEntry { ListingId = Guid.NewGuid().ToString("N"), UniqueName = "local",
            Name = "Public", PublicHost = "join.example" };
        var agent = new AgentRuntimeState { IsConnected = true, LastSnapshotReceivedUtc = now,
            Snapshot = new() { UniqueName = "local", CapturedAtUtc = now, IsRunning = true,
                Metrics = new() { SimSpeed = speed } } };
        using var publisher = Publisher(collect: _ => Task.FromResult<IReadOnlyList<ServerListListing>>(
            [ServerListProtocol.FromAgent(entry, [agent], now)]));
        await publisher.PublishOnceAsync(default);
        var publication = JsonSerializer.Deserialize<ServerListPublication>(_sent[^1].Body,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(expected, Assert.Single(publication.Listings).SimSpeed);
        Assert.Equal(expected ?? speed, agent.Snapshot.Metrics.SimSpeed);
    }

    public void Dispose()
    {
        _rbac.Dispose();
        _cache.SetValue(null, _previous);
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token);
    }

}
