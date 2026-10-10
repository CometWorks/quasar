using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using CometWorks.Diagnostics;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Quasar.Host.Contract.V1;
using Quasar.Services;
using Xunit;

namespace Quasar.Tests;

public sealed class DiagnosticsUplinkTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "quasar-diagnostics-" + Guid.NewGuid().ToString("N"));
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task EnrollmentIsAutomaticConsentGatedAndMachineIdentitySurvivesRestart()
    {
        using var consent = Catalog(Path.Combine(_root, "consent.json"));
        var hosts = Hosts(out var credentials);
        using var backofficeKey = RSA.Create(3072);
        var key = new BackofficeKey(DiagnosticsProtocol.KeyId(backofficeKey), backofficeKey.ExportSubjectPublicKeyInfoPem(), DateTimeOffset.UtcNow.AddDays(1));
        int enrollments = 0;
        string? firstPublic = null;
        using var backend = new HttpClient(new Handler(async (request, token) =>
        {
            if (request.Method == HttpMethod.Post)
            {
                Assert.EndsWith("/v1/installations/enroll", request.RequestUri!.AbsolutePath);
                Assert.Null(request.Headers.Authorization);
                using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
                var body = document.RootElement;
                var installation = body.GetProperty("installationId").GetString()!;
                var publicKey = body.GetProperty("publicKey").GetString()!;
                var issued = body.GetProperty("issuedAt").GetInt64();
                firstPublic ??= publicKey; Assert.Equal(firstPublic, publicKey);
                using var machine = RSA.Create(); machine.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out _);
                Assert.True(machine.VerifyData(System.Text.Encoding.UTF8.GetBytes($"CometWorks.Diagnostics.Enrollment.v1\n{installation}\n{publicKey}\n{issued}"),
                    Convert.FromBase64String(body.GetProperty("signature").GetString()!), HashAlgorithmName.SHA256, RSASignaturePadding.Pss));
                enrollments++;
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { installationId = installation,
                    encryptedUploadToken = Convert.ToBase64String(machine.Encrypt(System.Text.Encoding.ASCII.GetBytes(new string('a', 64)), RSAEncryptionPadding.OaepSHA256)) }) };
            }
            Assert.Equal(new string('a', 64), request.Headers.Authorization?.Parameter);
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(key) };
        }));
        string installationId;
        using (var service = Service(consent, hosts, credentials, new Handler((_, _) => throw new Exception("No Hosts expected")), backend, uploadToken: null))
        {
            installationId = service.InstallationId;
            await service.PollOnceAsync(default);
            Assert.Equal(0, enrollments); Assert.False(File.Exists(Path.Combine(service.StateDirectory, "machine-key.pem")));
            await consent.SaveAsync(false, true, false);
            await service.PollOnceAsync(default);
            Assert.Equal(1, enrollments); Assert.True(service.CurrentPolicy!.DiagnosticsGranted);
            var generation = consent.GetSettings().Generation;
            await consent.SaveListingVisibilityAsync(true);
            Assert.Equal(generation, consent.GetSettings().Generation);
            Assert.False(consent.GetSettings().ConsentGranted); Assert.True(consent.GetSettings().DiagnosticsGranted);
            await service.PollOnceAsync(default); Assert.Equal(1, enrollments);
        }
        using var restarted = Service(consent, hosts, credentials, new Handler((_, _) => throw new Exception("No Hosts expected")), backend, uploadToken: null);
        Assert.Equal(installationId, restarted.InstallationId);
        await restarted.PollOnceAsync(default); Assert.Equal(2, enrollments);
        if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(restarted.StateDirectory, "machine-key.pem")));
    }

    [Fact]
    public void MovingEvidenceDirectoryPreservesInstallationIdentity()
    {
        using var consent = Catalog(Path.Combine(_root, "consent.json"));
        var hosts = Hosts(out var credentials);
        var hostHandler = new Handler((_, _) => throw new Exception("No hosts expected"));
        using var backend = new HttpClient(new Handler((_, _) => throw new Exception("No upload expected")));
        string installation;
        using (var original = Service(consent, hosts, credentials, hostHandler, backend)) installation = original.InstallationId;
        string storage = Path.Combine(_root, "large disk", "logs");
        using var relocated = Service(consent, hosts, credentials, hostHandler, backend, storage);
        Assert.Equal(installation, relocated.InstallationId);
        Assert.Equal(Path.Combine(storage, "outbound"), relocated.SpoolDirectory);
        Assert.True(File.Exists(Path.Combine(relocated.StateDirectory, "installation-id")));
        Assert.False(File.Exists(Path.Combine(storage, "installation-id")));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(relocated.SpoolDirectory));
    }

    [Fact]
    public async Task LegacyYesDoesNotGrantDiagnosticsAndStatisticsSavePreservesCategories()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "consent.json");
        await File.WriteAllTextAsync(path, "{\"consentGranted\":true,\"diagnosticsGranted\":true,\"dumpsGranted\":true}");
        using var consent = Catalog(path);
        Assert.True(consent.GetSettings().ConsentGranted);
        Assert.False(consent.GetSettings().DiagnosticsGranted);
        Assert.False(consent.GetSettings().DumpsGranted);
        var before = DateTimeOffset.UtcNow;
        await consent.SaveAsync(true, true, true);
        var enabled = consent.GetSettings();
        Assert.True(enabled.GrantedSinceUtc >= before);
        Assert.True(enabled.Generation > 0);
        await consent.SaveAsync(false);
        Assert.True(consent.GetSettings().DiagnosticsGranted);
        Assert.True(consent.GetSettings().DumpsGranted);
        Assert.Equal(enabled.Generation, consent.GetSettings().Generation);
        await consent.SaveAsync(false, false, true);
        Assert.False(consent.GetSettings().DumpsGranted);
        Assert.True(consent.GetSettings().Generation > enabled.Generation);
        using var reloaded = Catalog(path);
        Assert.Equal(consent.GetSettings().Generation, reloaded.GetSettings().Generation);
    }

    [Fact]
    public async Task ExternalRevocationAdvancesAndPersistsGeneration()
    {
        var path = Path.Combine(_root, "consent.json");
        using var consent = Catalog(path);
        await consent.SaveAsync(true, true, true);
        var enabled = consent.GetSettings();
        var edited = enabled.Clone(); edited.DiagnosticsGranted = false;
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(edited, WebJson));
        consent.ReloadFromDisk();
        Assert.False(consent.GetSettings().DiagnosticsGranted);
        Assert.False(consent.GetSettings().DumpsGranted);
        Assert.True(consent.GetSettings().Generation > enabled.Generation);
        using var reloaded = Catalog(path);
        Assert.Equal(consent.GetSettings().Generation, reloaded.GetSettings().Generation);
    }

    [Theory]
    [InlineData("https://diagnostics.example/", true)]
    [InlineData("http://127.0.0.1:5000/", true)]
    [InlineData("http://[::1]:5000/", true)]
    [InlineData("http://diagnostics.example/", false)]
    [InlineData("https://user:secret@diagnostics.example/", false)]
    [InlineData("https://diagnostics.example/?token=secret", false)]
    public void BackofficeTransportRequiresTlsOrLoopback(string url, bool valid) =>
        Assert.Equal(valid, DiagnosticsUplinkService.IsAllowedEndpoint(new Uri(url)));

    [Fact]
    public async Task OpaqueHandoverIsDurableBeforeAckAndRetriesIdenticalIdAfterBackendFailure()
    {
        using var consent = Catalog(Path.Combine(_root, "consent.json"));
        await consent.SaveAsync(true, true, false);
        using var rsa = RSA.Create(3072);
        var key = new BackofficeKey(DiagnosticsProtocol.KeyId(rsa), rsa.ExportSubjectPublicKeyInfoPem(), DateTimeOffset.UtcNow.AddDays(1));
        byte[] bytes = "opaque encrypted zip bytes"u8.ToArray();
        var envelope = Envelope(bytes, key.KeyId, consent.GetSettings().Generation);
        var hosts = Hosts(out var credentials);
        await hosts.RegisterAsync("host-a", "Host A", "127.0.0.1", 28400, default);
        await hosts.RegisterAsync("host-b", "Host B", "127.0.0.2", 28400, default);
        int acknowledgements = 0, policies = 0, puts = 0;
        var uploaded = new List<byte[]>();
        var pending = new HashSet<string> { "host-a" };
        var hostHandler = new Handler(async (request, token) =>
        {
            string host = request.RequestUri!.Host.Split('.')[0];
            if (request.Method == HttpMethod.Put)
            {
                policies++;
                return HostResponse(await request.Content!.ReadFromJsonAsync<HostDiagnosticPolicy>(WebJson, token));
            }
            if (request.Method == HttpMethod.Delete)
            {
                Assert.True(File.Exists(Path.Combine(_root, "state", "outbound", envelope.ReportId + ".json")));
                Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(_root, "state", "outbound", envelope.ReportId + ".zip"), token));
                acknowledgements++; pending.Remove(host);
                return HostResponse(new { acknowledged = true });
            }
            if (request.RequestUri.AbsolutePath.EndsWith("/reports"))
                return HostResponse(pending.Contains(host) ? new[] { envelope } : Array.Empty<ReportEnvelope>());
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        });
        var backend = new HttpClient(new Handler(async (request, token) =>
        {
            Assert.Equal("upload-secret", request.Headers.Authorization!.Parameter);
            if (request.Method == HttpMethod.Get) return new(HttpStatusCode.OK) { Content = JsonContent.Create(key) };
            Assert.EndsWith(envelope.ReportId, request.RequestUri!.AbsolutePath);
            Assert.Equal("application/octet-stream", request.Content!.Headers.ContentType!.MediaType);
            uploaded.Add(await request.Content!.ReadAsByteArrayAsync(token));
            return new(++puts == 1 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK);
        }));
        string installationId;
        using (var first = Service(consent, hosts, credentials, hostHandler, backend))
        {
            installationId = first.InstallationId;
            await Assert.ThrowsAsync<HttpRequestException>(() => first.PollOnceAsync(default));
            Assert.Equal(1, acknowledgements);
            Assert.Single(Directory.GetFiles(first.SpoolDirectory, "*.json"));
        }
        using var service = Service(consent, hosts, credentials, hostHandler, backend);
        Assert.Equal(installationId, service.InstallationId);
        await service.PollOnceAsync(default);
        Assert.Equal(4, policies);
        Assert.Equal(2, puts);
        Assert.All(uploaded, actual => Assert.Equal(bytes, actual));
        Assert.Empty(Directory.GetFiles(service.SpoolDirectory));
    }

    [Fact]
    public async Task BadHostReportDoesNotBlockLaterValidReportsOrAcknowledgeCorruptEvidence()
    {
        using var consent = Catalog(Path.Combine(_root, "consent.json"));
        await consent.SaveAsync(true, true, false);
        using var rsa = RSA.Create(3072);
        var key = new BackofficeKey(DiagnosticsProtocol.KeyId(rsa), rsa.ExportSubjectPublicKeyInfoPem(), DateTimeOffset.UtcNow.AddDays(1));
        byte[] bytes = [1, 2, 3];
        var good = Envelope(bytes, key.KeyId, consent.GetSettings().Generation);
        var bad = good with { ReportId = Guid.NewGuid().ToString("N") };
        var invalid = bad with { ReportId = "invalid" };
        var hosts = Hosts(out var credentials);
        await hosts.RegisterAsync("host-a", "Host A", "127.0.0.1", 28400, default);
        var acknowledgments = new List<string>();
        var hostHandler = new Handler(async (request, token) =>
        {
            if (request.Method == HttpMethod.Put)
                return HostResponse(await request.Content!.ReadFromJsonAsync<HostDiagnosticPolicy>(WebJson, token));
            if (request.Method == HttpMethod.Delete)
            {
                acknowledgments.Add(request.RequestUri!.Segments.Last());
                return HostResponse(new { acknowledged = true });
            }
            if (request.RequestUri!.AbsolutePath.EndsWith("/reports")) return HostResponse(new[] { invalid, bad, good });
            return new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new ByteArrayContent(request.RequestUri.AbsolutePath.EndsWith(bad.ReportId) ? [1, 2, 4] : bytes) };
        });
        var uploaded = new List<string>();
        var backend = new HttpClient(new Handler((request, _) =>
        {
            if (request.Method == HttpMethod.Get)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(key) });
            uploaded.Add(request.RequestUri!.Segments.Last());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }));
        using var service = Service(consent, hosts, credentials, hostHandler, backend);
        await service.PollOnceAsync(default);
        Assert.Equal(good.ReportId, Assert.Single(acknowledgments));
        Assert.Equal(good.ReportId, Assert.Single(uploaded));
        Assert.Empty(Directory.GetFiles(service.SpoolDirectory));
    }

    [Theory]
    [InlineData(400)]
    [InlineData(409)]
    [InlineData(413)]
    [InlineData(415)]
    [InlineData(422)]
    [InlineData(507)]
    public async Task RejectedBackendReportRetainsEvidenceAndAllowsLaterDelivery(int status)
    {
        using var consent = Catalog(Path.Combine(_root, "consent.json"));
        await consent.SaveAsync(true, true, false);
        using var rsa = RSA.Create(3072);
        var key = new BackofficeKey(DiagnosticsProtocol.KeyId(rsa), rsa.ExportSubjectPublicKeyInfoPem(), DateTimeOffset.UtcNow.AddDays(1));
        var hosts = Hosts(out var credentials);
        string? rejected = null;
        var attempts = new List<string>();
        var backend = new HttpClient(new Handler((request, _) =>
        {
            if (request.Method == HttpMethod.Get)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(key) });
            string id = request.RequestUri!.Segments.Last();
            attempts.Add(id);
            rejected ??= id;
            return Task.FromResult(new HttpResponseMessage(id == rejected ? (HttpStatusCode)status : HttpStatusCode.Created));
        }));
        using var service = Service(consent, hosts, credentials, new Handler((_, _) => throw new Exception("No hosts expected")), backend);
        byte[] bytes = [1, 2, 3];
        for (int i = 0; i < 2; i++)
        {
            var envelope = Envelope(bytes, key.KeyId, consent.GetSettings().Generation);
            await File.WriteAllBytesAsync(Path.Combine(service.SpoolDirectory, envelope.ReportId + ".zip"), bytes);
            await File.WriteAllTextAsync(Path.Combine(service.SpoolDirectory, envelope.ReportId + ".json"), JsonSerializer.Serialize(envelope));
        }
        await service.PollOnceAsync(default);
        Assert.Equal(2, attempts.Count);
        Assert.Equal(rejected, Path.GetFileNameWithoutExtension(Assert.Single(Directory.GetFiles(service.SpoolDirectory, "*.json"))));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(service.SpoolDirectory, rejected + ".zip")));
        await service.PollOnceAsync(default);
        Assert.Equal(rejected, attempts.Last());
        Assert.Single(Directory.GetFiles(service.SpoolDirectory, "*.json"));
    }

    [Fact]
    public async Task RevocationCancelsActiveUploadAndPurgesSpoolImmediately()
    {
        using var consent = Catalog(Path.Combine(_root, "consent.json"));
        await consent.SaveAsync(true, true, true);
        using var rsa = RSA.Create(3072);
        var key = new BackofficeKey(DiagnosticsProtocol.KeyId(rsa), rsa.ExportSubjectPublicKeyInfoPem(), DateTimeOffset.UtcNow.AddDays(1));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var backend = new HttpClient(new Handler(async (request, token) =>
        {
            if (request.Method == HttpMethod.Get) return new(HttpStatusCode.OK) { Content = JsonContent.Create(key) };
            entered.SetResult();
            await Task.Delay(Timeout.Infinite, token);
            return new(HttpStatusCode.OK);
        }));
        var hosts = Hosts(out var credentials);
        using var service = Service(consent, hosts, credentials, new Handler((_, _) => throw new Exception("No hosts expected")), backend);
        byte[] bytes = [1, 2, 3]; var envelope = Envelope(bytes, key.KeyId, consent.GetSettings().Generation);
        await File.WriteAllBytesAsync(Path.Combine(service.SpoolDirectory, envelope.ReportId + ".zip"), bytes);
        await File.WriteAllTextAsync(Path.Combine(service.SpoolDirectory, envelope.ReportId + ".json"), JsonSerializer.Serialize(envelope));
        var cycle = service.PollOnceAsync(default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await consent.SaveAsync(true, false, false);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cycle);
        Assert.Empty(Directory.GetFiles(service.SpoolDirectory));
        Assert.Null(service.CurrentPolicy);
    }

    [Fact]
    public async Task CorruptTransferNeverLeavesAnAcknowledgableFile()
    {
        Directory.CreateDirectory(_root);
        byte[] bytes = [1, 2, 3];
        var envelope = Envelope(bytes, new string('a', 64), 1);
        string path = Path.Combine(_root, "corrupt.part");
        await Assert.ThrowsAsync<InvalidDataException>(() => DiagnosticsUplinkService.CopyVerifiedAsync(
            new MemoryStream([1, 2, 4]), path, envelope, default));
        Assert.False(File.Exists(path));
    }

    private DataHandlingConsentCatalog Catalog(string path) => new(NullLogger<DataHandlingConsentCatalog>.Instance, path);
    private ClusterHostCatalog Hosts(out ClusterCredentialStore credentials)
    {
        credentials = new(new EphemeralDataProtectionProvider(), Path.Combine(_root, "credentials.json"));
        return new(credentials, Path.Combine(_root, "hosts"));
    }
    private DiagnosticsUplinkService Service(DataHandlingConsentCatalog consent, ClusterHostCatalog hosts,
        ClusterCredentialStore credentials, Handler hostHandler, HttpClient backend, string? storageDirectory = null, string? uploadToken = "upload-secret") => new(consent, hosts,
            new ClusterHostClient(new HttpClient(hostHandler), credentials), backend,
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["Quasar:Diagnostics:BackofficeUrl"] = "https://backoffice.example/", ["Quasar:Diagnostics:UploadToken"] = uploadToken,
                ["Quasar:Diagnostics:StorageDirectory"] = storageDirectory }).Build(),
            NullLogger<DiagnosticsUplinkService>.Instance, Path.Combine(_root, "state"));
    private static ReportEnvelope Envelope(byte[] bytes, string key, long generation) => new(1, Guid.NewGuid().ToString("N"), key,
        "wrapped-password", Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes.Length, false, generation);
    private static HttpResponseMessage HostResponse<T>(T value)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        { Content = JsonContent.Create(new HostEnvelope<T>(HostProtocol.Version, DateTimeOffset.UtcNow, value), options: WebJson) };
        response.Headers.Add(HostProtocol.HeaderName, HostProtocol.Version.ToString()); return response;
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
