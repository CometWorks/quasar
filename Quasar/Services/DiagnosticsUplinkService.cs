using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using CometWorks.Diagnostics;
using Magnetar.Protocol.Runtime;
using HostContract = global::Quasar.Host.Contract.V1;

namespace Quasar.Services;

/// <summary>The sole backoffice uplink. Hosts hand over encrypted archives; Quasar never decrypts them.</summary>
public sealed class DiagnosticsUplinkService : BackgroundService
{
    private const long MaxSpoolBytes = 8L * 1024 * 1024 * 1024;
    private readonly DataHandlingConsentCatalog _consent;
    private readonly ClusterHostCatalog _hosts;
    private readonly Func<string, bool> _connected;
    private readonly ClusterHostClient _client;
    private readonly HttpClient _backoffice;
    private readonly ILogger<DiagnosticsUplinkService> _logger;
    private readonly Quasar.Diagnostics.DiagnosticCollector? _collector;
    private readonly Uri? _url;
    private string? _uploadToken;
    private readonly bool _configuredToken;
    private readonly SemaphoreSlim _enrollmentGate = new(1, 1);
    private readonly object _sync = new();
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly SemaphoreSlim _policyGate = new(1, 1);
    private BackofficeKey? _key;
    private CancellationTokenSource _transfers = new();
    private long _generation;
    private bool _disposed;
    private HostContract.HostDiagnosticPolicy? _policy;
    public string StateDirectory { get; }
    private readonly string _storageDirectory;
    public string InstallationId { get; }
    internal string SpoolDirectory => Path.Combine(_storageDirectory, "outbound");
    public HostContract.HostDiagnosticPolicy? CurrentPolicy
    {
        get
        {
            lock (_sync)
                return _policy is { } policy && policy.Generation == _consent.GetSettings().Generation
                    && policy.ExpiresAtUtc > DateTimeOffset.UtcNow ? policy : null;
        }
    }

    public DiagnosticsUplinkService(DataHandlingConsentCatalog consent, ClusterHostCatalog hosts, ClusterHostClient client,
        IHttpClientFactory factory, IConfiguration configuration, ILogger<DiagnosticsUplinkService> logger, Quasar.Diagnostics.DiagnosticCollector collector,
        ClusterHostTunnels tunnels)
        : this(consent, hosts, client, factory.CreateClient("DiagnosticsBackoffice"), configuration, logger,
            Path.Combine(MagnetarPaths.GetQuasarDirectory(), "Diagnostics"), collector, tunnels.IsConnected) { }

    internal DiagnosticsUplinkService(DataHandlingConsentCatalog consent, ClusterHostCatalog hosts, ClusterHostClient client,
        HttpClient backoffice, IConfiguration configuration, ILogger<DiagnosticsUplinkService> logger, string stateDirectory, Quasar.Diagnostics.DiagnosticCollector? collector = null,
        Func<string, bool>? connected = null)
    {
        (_consent, _hosts, _client, _backoffice, _logger, StateDirectory) = (consent, hosts, client, backoffice, logger, stateDirectory);
        _connected = connected ?? (_ => true);
        _collector = collector;
        _storageDirectory = collector?.RootDirectory ?? Quasar.Diagnostics.DiagnosticStorage.Resolve(
            Environment.GetEnvironmentVariable("QUASAR_DIAGNOSTICS_DIRECTORY")
                ?? configuration["Quasar:Diagnostics:StorageDirectory"], stateDirectory);
        _uploadToken = configuration["Quasar:Diagnostics:UploadToken"];
        _configuredToken = !string.IsNullOrWhiteSpace(_uploadToken);
        string? configured = configuration["Quasar:Diagnostics:BackofficeUrl"] ?? "https://log.se-servers.net/";
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (Uri.TryCreate(configured, UriKind.Absolute, out var uri) && IsAllowedEndpoint(uri)) _url = new Uri(uri.AbsoluteUri.TrimEnd('/') + "/");
            else logger.LogWarning("Diagnostic uplink disabled: configure an HTTPS backoffice URL (or loopback HTTP).");
        }
        Directory.CreateDirectory(StateDirectory);
        Quasar.Diagnostics.DiagnosticCollector.PrivateDirectory(_storageDirectory);
        Quasar.Diagnostics.DiagnosticCollector.PrivateDirectory(SpoolDirectory);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(StateDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var identityPath = Path.Combine(StateDirectory, "installation-id");
        if (!File.Exists(identityPath))
            AtomicFileWriter.WriteTextAsync(identityPath, Guid.NewGuid().ToString("N"), CancellationToken.None).GetAwaiter().GetResult();
        InstallationId = File.ReadAllText(identityPath).Trim();
        if (!DiagnosticsProtocol.IsReportId(InstallationId)) throw new InvalidDataException("Diagnostic installation identity is invalid.");
        var initialConsent = consent.GetSettings();
        _generation = initialConsent.Generation;
        if (_url is null || !initialConsent.DiagnosticsGranted
            || _collector?.CurrentPolicy is { } old && old.Generation != initialConsent.Generation)
            _collector?.Revoke(initialConsent.Generation);
        PurgeStale();
        consent.Changed += ConsentChanged;
    }

    internal static bool IsAllowedEndpoint(Uri uri) => string.IsNullOrEmpty(uri.UserInfo)
        && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment)
        && (uri.Scheme == "https" || uri.Scheme == "http" && uri.IsLoopback);

    internal Uri? BackofficeUrl => _url;

    internal async Task<HttpRequestMessage?> CreateAnnouncementRequestAsync(CancellationToken token)
    {
        if (_url?.Scheme != Uri.UriSchemeHttps || !_consent.GetSettings().AllowsAnnouncements) return null;
        await EnsureEnrolledAsync(token, announcements: true);
        return _consent.WithSettings(settings =>
        {
            token.ThrowIfCancellationRequested();
            return settings.AllowsAnnouncements ? Request(HttpMethod.Get, "v1/announcements") : null;
        });
    }

    internal void RejectAnnouncementCredential()
    {
        if (!_configuredToken) _uploadToken = null;
    }

    internal async Task<HttpRequestMessage?> CreateListingRequestAsync(HttpMethod method, string route, CancellationToken token)
    {
        if (_url is null) return null;
        // Withdrawal uses an already-negotiated credential; it does not enroll or
        // contact the backoffice for a new identity after diagnostic consent ends.
        if (string.IsNullOrWhiteSpace(_uploadToken))
        {
            if (!_consent.GetSettings().DiagnosticsGranted) return null;
            await EnsureEnrolledAsync(token);
        }
        return Request(method, route);
    }

    private async Task EnsureEnrolledAsync(CancellationToken token, bool announcements = false)
    {
        await _enrollmentGate.WaitAsync(token);
        try
        {
            if (!string.IsNullOrWhiteSpace(_uploadToken)) return;
            token.ThrowIfCancellationRequested();
            if (!_consent.GetSettings().DiagnosticsGranted) throw new OperationCanceledException(token);
            var path = Path.Combine(StateDirectory, "machine-key.pem");
            using var key = RSA.Create();
            if (File.Exists(path))
            {
                if (new FileInfo(path).Length > 8192) throw new InvalidDataException("Invalid diagnostic machine key.");
                key.ImportFromPem(await File.ReadAllTextAsync(path, token));
            }
            else
            {
                key.KeySize = 3072;
                await AtomicFileWriter.WriteTextAsync(path, key.ExportPkcs8PrivateKeyPem(), token);
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            if (key.KeySize != 3072) throw new InvalidDataException("Invalid diagnostic machine key.");
            var publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
            var issued = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var signed = System.Text.Encoding.UTF8.GetBytes($"CometWorks.Diagnostics.Enrollment.v1\n{InstallationId}\n{publicKey}\n{issued.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_url!, "v1/installations/enroll"))
            { Content = JsonContent.Create(new { installationId = InstallationId, publicKey, issuedAt = issued,
                signature = Convert.ToBase64String(key.SignData(signed, HashAlgorithmName.SHA256, RSASignaturePadding.Pss)) }) };
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(15));
            var sending = _consent.WithSettings(settings =>
            {
                timeout.Token.ThrowIfCancellationRequested();
                if (!settings.DiagnosticsGranted || announcements && !settings.AllowsAnnouncements)
                    throw new OperationCanceledException(timeout.Token);
                return _backoffice.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            });
            using var response = await sending;
            response.EnsureSuccessStatusCode();
            var bytes = new byte[4097];
            await using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
            int size = 0, read;
            while (size < bytes.Length && (read = await input.ReadAsync(bytes.AsMemory(size), timeout.Token)) > 0) size += read;
            if (size > 4096) throw new InvalidDataException("Enrollment response exceeds bound.");
            using var document = JsonDocument.Parse(bytes.AsMemory(0, size));
            if (document.RootElement.GetProperty("installationId").GetString() != InstallationId) throw new InvalidDataException("Enrollment identity mismatch.");
            var encrypted = Convert.FromBase64String(document.RootElement.GetProperty("encryptedUploadToken").GetString()!);
            var secret = key.Decrypt(encrypted, RSAEncryptionPadding.OaepSHA256);
            try
            {
                var value = System.Text.Encoding.ASCII.GetString(secret);
                if (value.Length != 64 || !value.All(char.IsAsciiHexDigit)) throw new InvalidDataException("Invalid negotiated credential.");
                token.ThrowIfCancellationRequested();
                _uploadToken = value;
            }
            finally { CryptographicOperations.ZeroMemory(secret); }
        }
        finally { _enrollmentGate.Release(); }
    }

    private void ConsentChanged()
    {
        lock (_sync)
        {
            var settings = _consent.GetSettings();
            if (settings.Generation == _generation) return;
            _generation = settings.Generation;
            _policy = null;
            _transfers.Cancel();
            _transfers.Dispose();
            _transfers = new();
            PurgeStale();
            try { _collector?.Revoke(settings.Generation); }
            catch (Exception exception)
            { _logger.LogWarning("Diagnostic local queue cleanup deferred ({FailureType}).", exception.GetType().Name); }
        }
        if (_wake.CurrentCount == 0) try { _wake.Release(); } catch (SemaphoreFullException) { }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var renewals = RenewLeasesAsync(stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await PollOnceAsync(stoppingToken); }
            catch (OperationCanceledException) { }
            catch (Exception exception)
            {
                // Exception messages may contain endpoint query strings or credentials from transport libraries.
                _logger.LogWarning("Diagnostic uplink retry pending ({FailureType}).", exception.GetType().Name);
            }
            try { await _wake.WaitAsync(TimeSpan.FromSeconds(30), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
        await renewals;
    }

    private async Task RenewLeasesAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
                using var cycle = CaptureCancellation(stoppingToken);
                await RefreshPolicyAsync(_consent.GetSettings(), cycle.Token);
            }
            catch (OperationCanceledException) { }
            catch (Exception exception)
            { _logger.LogWarning("Diagnostic consent renewal deferred ({FailureType}).", exception.GetType().Name); }
        }
    }

    private CancellationTokenSource CaptureCancellation(CancellationToken stoppingToken)
    {
        lock (_sync) return CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, _transfers.Token);
    }

    private async Task<HostContract.HostDiagnosticPolicy> RefreshPolicyAsync(DataHandlingConsentSettings consent, CancellationToken token)
    {
        await _policyGate.WaitAsync(token);
        try
        {
            BackofficeKey? key = null;
            if (_url is not null && consent.DiagnosticsGranted)
            {
                try
                {
                    await EnsureEnrolledAsync(token);
                    if (_key is null || _key.ExpiresAtUtc <= DateTimeOffset.UtcNow.AddMinutes(5))
                    {
                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                        timeout.CancelAfter(TimeSpan.FromSeconds(15));
                        _key = await FetchKeyAsync(timeout.Token);
                    }
                    key = _key;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception exception) { _logger.LogWarning("Diagnostic key unavailable ({FailureType}).", exception.GetType().Name); }
            }
            var policy = new HostContract.HostDiagnosticPolicy(InstallationId,
                consent.DiagnosticsGranted && key is not null, consent.DumpsGranted && key is not null, consent.Generation,
                consent.GrantedSinceUtc, DateTimeOffset.UtcNow.AddMinutes(2), key?.KeyId ?? "", key?.PublicKeyPem ?? "",
                key?.ExpiresAtUtc ?? DateTimeOffset.MinValue);
            lock (_sync) { token.ThrowIfCancellationRequested(); _policy = policy; }
            // Lease refresh is independent of transfers, including slow or unreachable hosts. An offline Host
            // gets the policy at the next refresh after it connects; asking for it would start the local Host.
            await Task.WhenAll(_hosts.GetAll().Where(host => _connected(host.Id)).Select(async host =>
            {
                try { await _client.SetDiagnosticsPolicyAsync(host, policy, token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception exception)
                { _logger.LogDebug("Diagnostic consent renewal for Host {HostId} deferred ({FailureType}).", host.Id, exception.GetType().Name); }
            }));
            if (_collector is not null) await _collector.ApplyPolicyAsync(policy, token);
            return policy;
        }
        finally { _policyGate.Release(); }
    }

    internal async Task PollOnceAsync(CancellationToken stoppingToken)
    {
        DataHandlingConsentSettings consent;
        CancellationTokenSource cycle;
        lock (_sync)
        {
            PurgeStale();
            consent = _consent.GetSettings();
            cycle = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, _transfers.Token);
        }
        using (cycle)
        {
            var token = cycle.Token;
            var policy = await RefreshPolicyAsync(consent, token);
            if (_collector is not null)
            {
                await _collector.CollectAsync(token);
                foreach (var envelope in _collector.GetReports())
                {
                    try
                    {
                        DiagnosticsProtocol.ValidateEnvelope(envelope);
                        if (!Allowed(envelope, consent)) continue;
                        if (!HasCapacity(envelope.ArchiveBytes)) break;
                        string metadata = Path.Combine(SpoolDirectory, envelope.ReportId + ".json");
                        if (File.Exists(metadata))
                        {
                            if (ReadEnvelope(metadata) != envelope) throw new InvalidDataException("Local report ID collision.");
                        }
                        else
                        {
                            string temporary = Path.ChangeExtension(metadata, ".zip.part");
                            TryDelete(temporary);
                            try
                            {
                                await using (var input = _collector.OpenReport(envelope.ReportId))
                                    await CopyVerifiedAsync(input, temporary, envelope, token);
                                PublishArchive(temporary, metadata, envelope, token);
                            }
                            finally { TryDelete(temporary); }
                        }
                        _collector.DeleteReport(envelope.ReportId);
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                    catch (Exception exception)
                    { _logger.LogWarning("Local diagnostic report will retry ({FailureType}).", exception.GetType().Name); }
                }
            }
            foreach (var host in _hosts.GetAll().Where(host => _connected(host.Id)))
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    if (!policy.DiagnosticsGranted) continue;
                    foreach (var envelope in (await _client.GetDiagnosticsReportsAsync(host, token)).Take(128))
                    {
                        try
                        {
                            DiagnosticsProtocol.ValidateEnvelope(envelope);
                            if (!Allowed(envelope, consent)) continue;
                            if (!HasCapacity(envelope.ArchiveBytes)) break;
                            string zip = Path.Combine(SpoolDirectory, envelope.ReportId + ".zip");
                            string metadata = Path.ChangeExtension(zip, ".json");
                            if (File.Exists(metadata))
                            {
                                if (ReadEnvelope(metadata) != envelope) throw new InvalidDataException("Report ID collision.");
                            }
                            else
                            {
                                string temporary = zip + ".part";
                                TryDelete(temporary);
                                try
                                {
                                    await _client.DownloadDiagnosticsReportAsync(host, envelope, temporary, token);
                                    PublishArchive(temporary, metadata, envelope, token);
                                }
                                finally { TryDelete(temporary); }
                            }
                            await _client.AcknowledgeDiagnosticsReportAsync(host, envelope.ReportId, token);
                        }
                        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                        catch (Exception exception)
                        { _logger.LogWarning("Diagnostic report from Host {HostId} will retry ({FailureType}).", host.Id, exception.GetType().Name); }
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception exception)
                { _logger.LogWarning("Diagnostic transfer from Host {HostId} will retry ({FailureType}).", host.Id, exception.GetType().Name); }
            }
            if (_url is not null && consent.DiagnosticsGranted) await UploadPendingAsync(consent, token);
            _logger.LogDebug("Diagnostic uplink backlog: {ReportCount} reports.", Directory.EnumerateFiles(SpoolDirectory, "*.json").Count());
        }
    }

    private bool HasCapacity(long bytes) => Directory.EnumerateFiles(SpoolDirectory, "*.json").Count() < 128
        && Directory.EnumerateFiles(SpoolDirectory, "*.zip").Sum(p => new FileInfo(p).Length) + bytes <= MaxSpoolBytes;

    private void PublishArchive(string temporary, string metadata, ReportEnvelope envelope, CancellationToken token)
    {
        lock (_sync)
        {
            token.ThrowIfCancellationRequested();
            if (!Allowed(envelope, _consent.GetSettings())) throw new OperationCanceledException(token);
            File.Move(temporary, Path.ChangeExtension(metadata, ".zip"), true);
            // The envelope is the durable completion marker; source acknowledgement follows it.
            AtomicFileWriter.WriteTextAsync(metadata, JsonSerializer.Serialize(envelope, DiagnosticsProtocol.Json), token)
                .GetAwaiter().GetResult();
        }
    }

    private async Task<BackofficeKey> FetchKeyAsync(CancellationToken token)
    {
        using var request = Request(HttpMethod.Get, "v1/key");
        using var response = await _backoffice.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        await using var input = await response.Content.ReadAsStreamAsync(token);
        using var json = new MemoryStream();
        byte[] buffer = new byte[4096]; int read;
        while ((read = await input.ReadAsync(buffer, token)) != 0)
        {
            if (json.Length + read > 16384) throw new InvalidDataException("Backoffice key response exceeds limit.");
            json.Write(buffer, 0, read);
        }
        var key = JsonSerializer.Deserialize<BackofficeKey>(json.ToArray(), DiagnosticsProtocol.Json)
            ?? throw new InvalidDataException("Missing backoffice key.");
        using var rsa = RSA.Create(); rsa.ImportFromPem(key.PublicKeyPem);
        if (rsa.KeySize is < 3072 or > 8192 || DiagnosticsProtocol.KeyId(rsa) != key.KeyId
            || key.ExpiresAtUtc <= DateTimeOffset.UtcNow.AddMinutes(2) || key.ExpiresAtUtc > DateTimeOffset.UtcNow.AddDays(400))
            throw new InvalidDataException("Invalid backoffice key.");
        return key;
    }

    private HttpRequestMessage Request(HttpMethod method, string route)
    {
        var request = new HttpRequestMessage(method, new Uri(_url!, route));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _uploadToken);
        request.Headers.Add("X-Installation-Id", InstallationId);
        return request;
    }

    private async Task UploadPendingAsync(DataHandlingConsentSettings consent, CancellationToken token)
    {
        foreach (string metadata in Directory.EnumerateFiles(SpoolDirectory, "*.json"))
        {
            token.ThrowIfCancellationRequested();
            var envelope = ReadEnvelope(metadata);
            DiagnosticsProtocol.ValidateEnvelope(envelope);
            if (!Allowed(envelope, consent)) continue;
            string archive = Path.ChangeExtension(metadata, ".zip");
            using var request = Request(HttpMethod.Put, "v1/reports/" + envelope.ReportId);
            string header = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(envelope, DiagnosticsProtocol.Json));
            if (header.Length > 8192) throw new InvalidDataException("Diagnostic envelope exceeds limit.");
            request.Headers.Add("X-Report-Envelope", header);
            await using var file = new FileStream(archive, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (file.Length != envelope.ArchiveBytes || Convert.ToHexStringLower(await SHA256.HashDataAsync(file, token)) != envelope.ArchiveSha256)
            {
                lock (_sync) { TryDelete(metadata); TryDelete(archive); }
                _logger.LogWarning("Discarded corrupt diagnostic archive {ReportId}.", envelope.ReportId);
                continue;
            }
            file.Position = 0;
            request.Content = new StreamContent(file);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            Task<HttpResponseMessage> sending;
            lock (_sync)
            {
                token.ThrowIfCancellationRequested();
                if (!Allowed(envelope, _consent.GetSettings())) throw new OperationCanceledException(token);
                sending = _backoffice.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            }
            using var response = await sending;
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized && !_configuredToken) _uploadToken = null;
            // A report-specific rejection must not hold the rest of the durable queue hostage.
            // Keep the original evidence for retry until normal retention/consent cleanup.
            if ((int)response.StatusCode is 400 or 409 or 413 or 415 or 422 or 507)
            {
                _logger.LogWarning("Diagnostic report {ReportId} remains queued after backoffice status {StatusCode}.",
                    envelope.ReportId, (int)response.StatusCode);
                continue;
            }
            response.EnsureSuccessStatusCode();
            lock (_sync) { TryDelete(metadata); TryDelete(archive); }
        }
    }

    private static bool Allowed(ReportEnvelope envelope, DataHandlingConsentSettings consent) =>
        consent.DiagnosticsGranted && envelope.ConsentGeneration == consent.Generation && (!envelope.ContainsDump || consent.DumpsGranted);
    private static ReportEnvelope ReadEnvelope(string path) => new FileInfo(path).Length <= 8192
        ? JsonSerializer.Deserialize<ReportEnvelope>(File.ReadAllBytes(path), DiagnosticsProtocol.Json)
            ?? throw new InvalidDataException("Missing diagnostic envelope.")
        : throw new InvalidDataException("Diagnostic envelope exceeds limit.");

    private void PurgeStale()
    {
        var consent = _consent.GetSettings();
        foreach (string metadata in Directory.EnumerateFiles(SpoolDirectory, "*.json"))
        {
            bool keep;
            try
            {
                var envelope = ReadEnvelope(metadata);
                DiagnosticsProtocol.ValidateEnvelope(envelope);
                keep = _url is not null && Allowed(envelope, consent)
                    && Path.GetFileNameWithoutExtension(metadata) == envelope.ReportId
                    && File.Exists(Path.ChangeExtension(metadata, ".zip"))
                    && File.GetLastWriteTimeUtc(metadata) >= DateTime.UtcNow.AddDays(-7);
            }
            catch { keep = false; }
            if (!keep) { TryDelete(metadata); TryDelete(Path.ChangeExtension(metadata, ".zip")); }
        }
        foreach (string file in Directory.EnumerateFiles(SpoolDirectory))
            if (file.EndsWith(".part", StringComparison.Ordinal) || file.EndsWith(".tmp", StringComparison.Ordinal)
                || file.EndsWith(".zip", StringComparison.Ordinal) && !File.Exists(Path.ChangeExtension(file, ".json"))) TryDelete(file);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    internal static async Task CopyVerifiedAsync(Stream input, string path, ReportEnvelope envelope, CancellationToken token)
    {
        DiagnosticsProtocol.ValidateEnvelope(envelope);
        try
        {
            await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Delete, 65536, FileOptions.Asynchronous);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] buffer = new byte[65536]; long bytes = 0; int read;
            while ((read = await input.ReadAsync(buffer, token)) != 0)
            {
                bytes += read;
                if (bytes > envelope.ArchiveBytes) throw new InvalidDataException("Diagnostic archive exceeds declared size.");
                hash.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), token);
            }
            await output.FlushAsync(token); output.Flush(true);
            if (bytes != envelope.ArchiveBytes || Convert.ToHexStringLower(hash.GetHashAndReset()) != envelope.ArchiveSha256)
                throw new InvalidDataException("Diagnostic archive failed integrity check.");
        }
        catch { TryDelete(path); throw; }
    }

    // Program registers this service as a singleton and again, through a factory, as a hosted service,
    // so the container disposes the same instance twice on shutdown.
    public override void Dispose()
    {
        _consent.Changed -= ConsentChanged;
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _transfers.Cancel(); _transfers.Dispose();
        }
        base.Dispose();
    }
}
