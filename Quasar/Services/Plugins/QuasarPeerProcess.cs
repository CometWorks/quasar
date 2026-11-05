using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Quasar.Plugin.Abstractions.Operations;

namespace Quasar.Services.Plugins;

public sealed class QuasarRtcOptions
{
    public string[] StunUrls { get; set; } = ["stun:rtc.se-servers.net:3478"];
    public string[] ValidatedStunUrls() => StunUrls.Length <= 2 && StunUrls.All(url => url is { Length: > 5 and <= 256 } && url.StartsWith("stun:", StringComparison.Ordinal)
        && Uri.TryCreate("http://" + url[5..], UriKind.Absolute, out var address)
        && address.UserInfo.Length == 0 && address.AbsolutePath == "/" && address.Query.Length == 0 && address.Fragment.Length == 0)
        ? StunUrls : throw new QuasarOperationException(503, "Invalid self-hosted STUN configuration.");
    public string PeerExecutable { get; set; } = Path.Combine(AppContext.BaseDirectory, "Peer",
        OperatingSystem.IsWindows() ? "quasar-peer.exe" : "quasar-peer");
}

public sealed record QuasarRtcDescription(string Type, string Sdp)
{
    public string Fingerprint()
    {
        if (Sdp is null || System.Text.Encoding.UTF8.GetByteCount(Sdp) > 48 * 1024)
            throw new QuasarOperationException(400, "The peer description exceeds 48 KiB.");
        var lines = Sdp.Split('\n').Select(line => line.TrimEnd('\r')).ToArray();
        var values = lines.Where(line => line.StartsWith("a=fingerprint:", StringComparison.Ordinal)).ToArray();
        if (lines.Count(line => line.StartsWith("m=application ", StringComparison.Ordinal)) != 1
            || lines.Any(line => line.StartsWith("m=", StringComparison.Ordinal) && !line.StartsWith("m=application ", StringComparison.Ordinal))
            || values.Length == 0)
            throw new QuasarOperationException(400, "A data-only peer description and SHA-256 fingerprint are required.");
        var fingerprints = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            const string prefix = "a=fingerprint:sha-256 ";
            if (!value.StartsWith(prefix, StringComparison.Ordinal)) throw new QuasarOperationException(400, "Unsupported DTLS fingerprint algorithm.");
            var bytes = value[prefix.Length..].Split(':');
            if (bytes.Length != 32 || bytes.Any(part => part.Length != 2 || part.Any(c => !char.IsAsciiHexDigit(c))))
                throw new QuasarOperationException(400, "Invalid DTLS fingerprint.");
            fingerprints.Add(string.Concat(bytes).ToUpperInvariant());
        }
        if (fingerprints.Count != 1) throw new QuasarOperationException(400, "A peer attempt must bind one DTLS identity.");
        return fingerprints.Single();
    }
}

public sealed record QuasarRtcPeerStatus(string State, string Path, string? LocalCandidateType, string? RemoteCandidateType, long ReceivedBytes = 0, long SentBytes = 0, long MaximumBufferedBytes = 0);

/// <summary>Private, fixed-route IPC to the worker's native peer; never an application HTTP proxy.</summary>
public sealed class QuasarPeerProcess(IOptions<QuasarRtcOptions> options) : IAsyncDisposable
{
    private readonly SemaphoreSlim startGate = new(1, 1);
    private Connection? connection;

    public async Task<QuasarRtcDescription> CreateAsync(string id, QuasarRtcDescription offer, CancellationToken token, bool hosted = false, QuasarRtcIce[]? relay = null)
    {
        var current = await StartAsync(token);
        using var request = new HttpRequestMessage(HttpMethod.Post, Route(id)) { Content = JsonContent.Create(new { offer.Type, offer.Sdp, iceServers = relay ?? (hosted ? options.Value.ValidatedStunUrls().Select(url => new QuasarRtcIce([url])).ToArray() : []) }) };
        using var response = await current.Http.SendAsync(request, token);
        if (!response.IsSuccessStatusCode) throw new QuasarOperationException((int)response.StatusCode, "The direct peer could not accept this connection.");
        return await response.Content.ReadFromJsonAsync<QuasarRtcDescription>(token)
            ?? throw new QuasarOperationException(503, "The direct peer returned no answer.");
    }

    public async Task<QuasarRtcPeerStatus> StatusAsync(string id, CancellationToken token)
    {
        var current = Current();
        using var response = await current.Http.GetAsync(Route(id), token);
        if (!response.IsSuccessStatusCode) throw new QuasarOperationException(503, "The direct peer is unavailable.");
        return await response.Content.ReadFromJsonAsync<QuasarRtcPeerStatus>(token)
            ?? throw new QuasarOperationException(503, "The direct peer returned no status.");
    }

    public async Task PulseAsync(string id, CancellationToken token)
    {
        using var response = await Current().Http.PostAsync(Route(id) + "/pulse", null, token);
        if (!response.IsSuccessStatusCode) throw new QuasarOperationException(503, "The direct peer is unavailable.");
    }

    public async Task CloseAsync(string id)
    {
        var current = Volatile.Read(ref connection);
        if (current is null || current.Process.HasExited) return;
        try { using var response = await current.Http.DeleteAsync(Route(id)); }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException or ObjectDisposedException) { }
    }

    public async Task<ClientWebSocket> ConnectChannelAsync(string id, string channel, CancellationToken token)
    {
        if (channel is not ("control" or "bulk")) throw new ArgumentException("Unknown private peer channel.");
        var current = Current();
        var uri = new UriBuilder(new Uri(current.Http.BaseAddress!, Route(id) + "/channels/" + channel)) { Scheme = "ws" }.Uri;
        var socket = new ClientWebSocket();
        socket.Options.Proxy = null;
        socket.Options.AddSubProtocol("quasar.peer.v1");
        socket.Options.SetRequestHeader("Authorization", current.Http.DefaultRequestHeaders.Authorization!.ToString());
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        try { await socket.ConnectAsync(uri, deadline.Token); return socket; }
        catch { socket.Dispose(); throw; }
    }

    private Connection Current()
    {
        var current = Volatile.Read(ref connection);
        return current is not null && !current.Process.HasExited ? current
            : throw new QuasarOperationException(503, "The direct peer is unavailable.");
    }

    private async Task<Connection> StartAsync(CancellationToken token)
    {
        await startGate.WaitAsync(token);
        try
        {
            if (connection is { } existing && !existing.Process.HasExited) return existing;
            if (connection is { } stopped) { connection = null; await stopped.DisposeAsync(); }
            var executable = Path.GetFullPath(options.Value.PeerExecutable);
            if (!File.Exists(executable)) throw new QuasarOperationException(503, "The direct connection component is not installed.");
            await VerifyComponentAsync(executable, token);
            var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            var inherited = new[] { "SystemRoot", "WINDIR", "TEMP", "TMP" }.Select(name => (name, value: Environment.GetEnvironmentVariable(name))).ToArray();
            start.Environment.Clear();
            foreach (var (name, value) in inherited) if (value is not null) start.Environment[name] = value;
            start.Environment["QUASAR_PEER_TOKEN"] = secret;
            var process = Process.Start(start) ?? throw new QuasarOperationException(503, "Cannot start the direct connection component.");
            var diagnostics = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
            try
            {
                var line = await process.StandardOutput.ReadLineAsync(token).AsTask().WaitAsync(TimeSpan.FromSeconds(10), token);
                using var ready = JsonDocument.Parse(line ?? throw new IOException("Peer exited during startup."));
                if (ready.RootElement.GetProperty("version").GetInt32() != 1
                    || !Uri.TryCreate(ready.RootElement.GetProperty("url").GetString(), UriKind.Absolute, out var url)
                    || url.Scheme != "http" || url.Host != "127.0.0.1" || url.AbsolutePath != "/" || url.UserInfo.Length != 0 || url.Query.Length != 0 || url.Fragment.Length != 0)
                    throw new IOException("Invalid private peer endpoint.");
                var http = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false })
                { BaseAddress = url, Timeout = TimeSpan.FromSeconds(5), MaxResponseContentBufferSize = QuasarSdkLimits.MaximumJsonBytes };
                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
                connection = new(process, http, diagnostics);
                return connection;
            }
            catch
            {
                process.StandardInput.Close();
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
                await diagnostics;
                process.Dispose();
                throw;
            }
        }
        finally { startGate.Release(); }
    }

    private sealed record ComponentManifest(int Protocol, string Runtime, string Sha256);
    private static async Task VerifyComponentAsync(string executable, CancellationToken token)
    {
        try
        {
            var path = executable + ".json";
            if (new FileInfo(path).Length > 4096) throw new IOException("Invalid peer manifest.");
            await using var metadata = File.OpenRead(path);
            var manifest = await JsonSerializer.DeserializeAsync<ComponentManifest>(metadata, new JsonSerializerOptions(JsonSerializerDefaults.Web), token);
            var runtime = OperatingSystem.IsWindows() ? "win-x64" : "linux-x64";
            if (manifest is not { Protocol: 1, Sha256.Length: 64 } || manifest.Runtime != runtime) throw new IOException("Invalid peer manifest.");
            await using var binary = File.OpenRead(executable);
            if (!string.Equals(manifest.Sha256, Convert.ToHexString(await SHA256.HashDataAsync(binary, token)), StringComparison.OrdinalIgnoreCase))
                throw new IOException("Peer integrity mismatch.");
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        { throw new QuasarOperationException(503, "The direct connection component failed its integrity check."); }
    }

    private static string Route(string id) => id.Length == 32 && id.All(char.IsAsciiHexDigit)
        ? "peers/" + id : throw new ArgumentException("Invalid peer identity.");

    public async ValueTask DisposeAsync()
    {
        await startGate.WaitAsync();
        try { if (connection is { } current) { connection = null; await current.DisposeAsync(); } }
        finally { startGate.Release(); }
    }

    private sealed record Connection(Process Process, HttpClient Http, Task Diagnostics) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Http.Dispose();
            Process.StandardInput.Close();
            try { await Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (TimeoutException) { if (!Process.HasExited) Process.Kill(entireProcessTree: true); await Process.WaitForExitAsync(); }
            await Diagnostics;
            var exitCode = Process.ExitCode;
            Process.Dispose();
            if (exitCode != 0) throw new IOException("The direct connection component exited unexpectedly.");
        }
    }
}
