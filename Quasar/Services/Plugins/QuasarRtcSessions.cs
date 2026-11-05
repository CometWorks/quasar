using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Quasar.Hosting;
using Quasar.Plugin.Abstractions.Operations;
using Quasar.Services.Auth;

namespace Quasar.Services.Plugins;

public sealed record QuasarRtcOffer(int Version, string Nonce, QuasarRtcDescription Description);
public sealed record QuasarRtcSetup(string Id, string Grant, QuasarRtcDescription Description, int Version = 1, int SetupSeconds = 30);

/// <summary>Owns the exact core browser binding; native peer messages cannot supply a principal.</summary>
public sealed class QuasarRtcSessions(QuasarBrowserSessions sessions, QuasarPeerProcess native,
    IServiceScopeFactory scopes, IHttpClientFactory clients, IHostApplicationLifetime application,
    TimeProvider clock, ILogger<QuasarRtcSessions> logger) : IAsyncDisposable, IHostedService
{
    private readonly object gate = new();
    private readonly Dictionary<string, Peer> peers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Replay> replay = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource shutdown = CancellationTokenSource.CreateLinkedTokenSource(application.ApplicationStopping);
    internal int PeerCount { get { lock (gate) return peers.Count; } }

    public async Task<QuasarRtcSetup> CreateAsync(QuasarBrowserSession binding, bool hosted, QuasarRtcOffer request, CancellationToken token)
    {
        sessions.Verify(binding, token);
        await CheckCallerAsync(binding, token);
        if (request.Version != 1 || request.Nonce is not { Length: 64 } || request.Nonce.Any(c => !char.IsAsciiHexDigit(c))
            || request.Description?.Type != "offer")
            throw new QuasarOperationException(400, "Protocol 1, a fresh connection nonce and an offer are required.");
        var browserFingerprint = request.Description.Fingerprint();
        var state = hosted ? HostedPanelState.Read() : null;
        if (hosted && (state is not { Choice: "hosted", Credential: not null }
            || binding.PathBase != "/" + state.PanelId || binding.User.FindFirst(QuasarClaimTypes.Provider)?.Value != QuasarAuthSchemes.Steam))
            throw new QuasarOperationException(403, "A verified enrolled Quasar Steam session is required.");
        Peer peer;
        lock (gate)
        {
            foreach (var item in replay.Where(pair => clock.GetElapsedTime(pair.Value.Started) >= TimeSpan.FromMinutes(2)).ToArray()) replay.Remove(item.Key);
            var replayKey = binding.Id + "/" + HostedPanelState.Hash(request.Nonce.ToUpperInvariant());
            if (replay.ContainsKey(replayKey)) throw new QuasarOperationException(409, "This connection attempt has already been used.");
            if (replay.Count >= 4096 || replay.Values.Count(item => item.Session == binding.Id) >= 16
                || replay.Values.Count(item => item.Owner == binding.Owner) >= 64
                || peers.Count >= 64 || peers.Values.Count(item => item.Binding.Id == binding.Id) >= 4
                || peers.Values.Count(item => item.Binding.Owner == binding.Owner) >= 16)
                throw new QuasarOperationException(429, "Too many direct connection attempts. Close an existing connection first.");
            peer = new(binding, hosted, shutdown.Token, clock);
            peers.Add(peer.Id, peer);
            replay.Add(replayKey, new(binding.Id, binding.Owner, clock.GetTimestamp()));
        }
        using var setup = CancellationTokenSource.CreateLinkedTokenSource(peer.Token, token);
        try
        {
            var answer = await native.CreateAsync(peer.Id, request.Description, setup.Token, hosted);
            if (answer.Type != "answer") throw new QuasarOperationException(503, "Invalid direct connection answer.");
            var fingerprint = answer.Fingerprint();
            if (state is not null)
            {
                var leaseBinding = new HostingBinding(1, HostedPanelState.Hash(binding.Owner + "\n" + binding.PathBase + "\n" + binding.Id),
                    HostedPanelState.Hash(peer.Id), browserFingerprint, fingerprint);
                var grant = await HostingAsync(HttpMethod.Post, "/api/v1/connector/rtc/leases/", state.Credential!, leaseBinding, setup.Token);
                ValidateGrant(grant, 30, requireGrant: true);
                peer.Hosting = new(state.PanelId, state.Credential!, leaseBinding, grant.Id, grant.Grant!);
            }
            sessions.Verify(binding, setup.Token);
            peer.Monitor = WatchAsync(peer);
            return new(peer.Id, peer.Grant!, answer);
        }
        catch
        {
            peer.Cancel();
            await RetireAsync(peer);
            throw;
        }
    }

    public async Task ConfirmAsync(QuasarBrowserSession binding, string id, string grant, CancellationToken token)
    {
        var peer = Get(binding, id, token);
        await peer.ConfirmGate.WaitAsync(token);
        try
        {
            sessions.Verify(binding, peer.Token);
            await CheckCallerAsync(binding, peer.Token);
            if (peer.Active || !HostedPanelState.SecretEquals(peer.Grant, grant))
                throw new QuasarOperationException(409, "The peer setup grant is invalid or already consumed.");
            var status = await native.StatusAsync(peer.Id, peer.Token);
            if (status.State != "connected" || status.Path != "direct")
                throw new QuasarOperationException(409, "Direct connectivity is unavailable; no automatic relay is configured.");
            if (peer.Hosting is { } hosted)
            {
                CheckInstallation(hosted);
                var started = clock.GetTimestamp();
                var result = await HostingAsync(HttpMethod.Post, "/api/v1/connector/rtc/leases/" + hosted.Id + "/activate", hosted.Credential,
                    new { binding = hosted.Binding, grant = hosted.Grant }, peer.Token);
                ValidateGrant(result, 120, requireGrant: false, hosted.Id);
                peer.LeaseStarted = started;
                peer.LastRenewal = started;
                hosted.Grant = "";
            }
            peer.Token.ThrowIfCancellationRequested();
            peer.Grant = null;
            if (peer.Hosting is not null) ArmLease(peer, peer.LeaseStarted);
            else peer.LeaseExpiry.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            peer.Active = true;
            peer.Status = status;
            using (ExecutionContext.SuppressFlow()) peer.Application = Task.Run(() => RunApplicationAsync(peer));
            await peer.Ready.Task.WaitAsync(peer.Token);
        }
        finally { peer.ConfirmGate.Release(); }
    }

    public Peer Get(QuasarBrowserSession binding, string id, CancellationToken token = default)
    {
        sessions.Verify(binding, token);
        lock (gate)
        {
            if (!peers.TryGetValue(id, out var peer) || !ReferenceEquals(peer.Binding, binding) || peer.Token.IsCancellationRequested)
                throw new QuasarOperationException(404, "Direct session not found or expired.");
            return peer;
        }
    }

    public async Task CloseAsync(QuasarBrowserSession binding, string id, CancellationToken token)
    {
        var peer = Get(binding, id, token);
        peer.Cancel();
        await peer.Monitor;
    }

    private async Task WatchAsync(Peer peer)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), clock);
            do
            {
                sessions.Verify(peer.Binding, peer.Token);
                await CheckCallerAsync(peer.Binding, peer.Token);
                if (peer.Hosting is { } hosting)
                {
                    CheckInstallation(hosting);
                    if (peer.Active)
                    {
                        if (clock.GetElapsedTime(peer.LeaseStarted) >= TimeSpan.FromSeconds(120))
                            throw new QuasarOperationException(403, "Hosting authorization expired.");
                        if (clock.GetElapsedTime(peer.LastRenewal) >= TimeSpan.FromSeconds(30))
                        {
                            peer.LastRenewal = clock.GetTimestamp();
                            var started = clock.GetTimestamp();
                            try
                            {
                                var renewal = await HostingAsync(HttpMethod.Post, "/api/v1/connector/rtc/leases/" + hosting.Id + "/renew",
                                    hosting.Credential, hosting.Binding, peer.Token);
                                ValidateGrant(renewal, 120, requireGrant: false, hosting.Id);
                                ArmLease(peer, started);
                            }
                            catch (QuasarOperationException error) when (error.StatusCode == 503) { }
                        }
                    }
                }
                var status = await native.StatusAsync(peer.Id, peer.Token);
                if (status.State is "closed" or "failed" || status.Path == "relayed")
                    throw new QuasarOperationException(409, "Direct connectivity is unavailable.");
                peer.Status = status;
                await native.PulseAsync(peer.Id, peer.Token);
            } while (await timer.WaitForNextTickAsync(peer.Token));
        }
        catch (OperationCanceledException) when (peer.Token.IsCancellationRequested) { }
        catch (Exception error) { logger.LogDebug("Direct browser session closed ({Reason}).", error.GetType().Name); }
        finally { peer.Cancel(); await RetireAsync(peer); }
    }

    private async Task CheckCallerAsync(QuasarBrowserSession binding, CancellationToken token)
    {
        sessions.Verify(binding, token);
        using var scope = scopes.CreateScope();
        var user = scope.ServiceProvider.GetRequiredService<QuasarRoleMapper>().RefreshRoles(binding.User);
        if (!(await scope.ServiceProvider.GetRequiredService<IAuthorizationService>()
                .AuthorizeAsync(user, null, QuasarPolicyNames.CanView)).Succeeded)
            throw new QuasarOperationException(403, "Panel access is no longer permitted.");
    }

    private async Task RunApplicationAsync(Peer peer)
    {
        try
        {
            using var scope = scopes.CreateScope();
            await scope.ServiceProvider.GetRequiredService<QuasarRtcAdapter>().RunAsync(peer);
        }
        catch (OperationCanceledException) when (peer.Token.IsCancellationRequested) { }
        catch (Exception error)
        {
            logger.LogDebug("Direct SDK channel closed ({Reason}).", error.GetType().Name);
            peer.Ready.TrySetException(new QuasarOperationException(503, "The direct SDK channel is unavailable."));
        }
        finally { peer.Cancel(); peer.Ready.TrySetCanceled(peer.Token); }
    }

    private void ArmLease(Peer peer, long started)
    {
        var remaining = TimeSpan.FromSeconds(120) - clock.GetElapsedTime(started);
        if (remaining <= TimeSpan.Zero) throw new QuasarOperationException(403, "Hosting authorization expired before it arrived.");
        peer.Token.ThrowIfCancellationRequested();
        peer.LeaseStarted = started;
        peer.LeaseExpiry.Change(remaining, Timeout.InfiniteTimeSpan);
    }

    private static void CheckInstallation(HostedLease expected)
    {
        var current = HostedPanelState.Read();
        if (current is not { Choice: "hosted" } || current.PanelId != expected.Panel
            || !HostedPanelState.SecretEquals(current.Credential, expected.Credential))
            throw new QuasarOperationException(403, "The hosted installation changed.");
    }

    private async Task<HostingGrant> HostingAsync(HttpMethod method, string path, string credential, object binding, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            using var http = clients.CreateClient("Nebula");
            using var request = new HttpRequestMessage(method, HostedPanelState.Origin + path) { Content = JsonContent.Create(binding) };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
            using var response = await http.SendAsync(request, deadline.Token);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound or HttpStatusCode.Conflict)
                throw new QuasarOperationException(403, "Hosting authorization was revoked or replaced.");
            if (!response.IsSuccessStatusCode) throw new QuasarOperationException(503, "Hosting authorization is temporarily unavailable.");
            return await response.Content.ReadFromJsonAsync<HostingGrant>(deadline.Token)
                ?? throw new QuasarOperationException(503, "Hosting authorization returned no lease.");
        }
        catch (Exception error) when (error is HttpRequestException or JsonException || error is OperationCanceledException && !token.IsCancellationRequested)
        { throw new QuasarOperationException(503, "Hosting authorization is temporarily unavailable."); }
    }

    private static void ValidateGrant(HostingGrant grant, int lifetime, bool requireGrant, string? expectedId = null)
    {
        if (grant.Version != 1 || grant.ValidForSeconds != lifetime || grant.Id is not { Length: 32 } || grant.Id.Any(c => !char.IsAsciiHexDigit(c))
            || expectedId is not null && grant.Id != expectedId
            || requireGrant && (grant.Grant is not { Length: 64 } || grant.Grant.Any(c => !char.IsAsciiHexDigit(c))))
            throw new QuasarOperationException(503, "Invalid hosting authorization response.");
    }

    private async Task RetireAsync(Peer peer)
    {
        if (Interlocked.Exchange(ref peer.Retiring, 1) != 0) { await peer.Retired.Task; return; }
        await peer.ConfirmGate.WaitAsync();
        try
        {
            await native.CloseAsync(peer.Id);
            await peer.Application;
            if (peer.Hosting is { } hosting)
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                try
                {
                    using var http = clients.CreateClient("Nebula");
                    using var request = new HttpRequestMessage(HttpMethod.Delete, HostedPanelState.Origin + "/api/v1/connector/rtc/leases/" + hosting.Id);
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", hosting.Credential);
                    using var response = await http.SendAsync(request, deadline.Token);
                }
                catch (Exception error) when (error is HttpRequestException or OperationCanceledException) { }
            }
        }
        finally
        {
            await peer.LeaseExpiry.DisposeAsync();
            peer.Stop.Dispose();
            peer.ConfirmGate.Release();
            lock (gate) peers.Remove(peer.Id);
            peer.Retired.TrySetResult();
        }
    }

    private int disposed;
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => StopWorkAsync();
    private async Task StopWorkAsync()
    {
        try { shutdown.Cancel(); } catch (ObjectDisposedException) { return; }
        Peer[] active;
        lock (gate) active = peers.Values.ToArray();
        await Task.WhenAll(active.Select(peer => peer.Retired.Task));
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        await StopWorkAsync();
        shutdown.Dispose();
    }

    internal sealed record HostingBinding(int Version, string Session, string Attempt, string Browser, string Peer);
    private sealed record Replay(string Session, string Owner, long Started);
    private sealed record HostingGrant(string Id, string? Grant, int ValidForSeconds, int Version);
    internal sealed class HostedLease(string panel, string credential, HostingBinding binding, string id, string grant)
    {
        public string Panel { get; } = panel;
        public string Credential { get; } = credential;
        public HostingBinding Binding { get; } = binding;
        public string Id { get; } = id;
        public string Grant { get; set; } = grant;
    }

    public sealed class Peer
    {
        internal Peer(QuasarBrowserSession binding, bool hosted, CancellationToken stopping, TimeProvider clock)
        {
            Binding = binding;
            Hosted = hosted;
            Stop = CancellationTokenSource.CreateLinkedTokenSource(binding.Revoked, stopping);
            Token = Stop.Token;
            LeaseExpiry = clock.CreateTimer(_ => Cancel(), null, TimeSpan.FromSeconds(30), Timeout.InfiniteTimeSpan);
        }
        public string Id { get; } = Guid.NewGuid().ToString("N");
        public QuasarBrowserSession Binding { get; }
        public bool Hosted { get; }
        public CancellationToken Token { get; }
        public QuasarRtcPeerStatus Status { get; internal set; } = new("connecting", "unavailable", null, null);
        private int active;
        public bool Active { get => Volatile.Read(ref active) != 0; internal set => Volatile.Write(ref active, value ? 1 : 0); }
        internal CancellationTokenSource Stop { get; }
        internal void Cancel() { try { Stop.Cancel(); } catch (ObjectDisposedException) { } }
        internal SemaphoreSlim ConfirmGate { get; } = new(1, 1);
        internal string? Grant { get; set; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        internal Task Monitor { get; set; } = Task.CompletedTask;
        internal Task Application { get; set; } = Task.CompletedTask;
        internal TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal HostedLease? Hosting { get; set; }
        internal ITimer LeaseExpiry { get; }
        internal int Retiring;
        internal TaskCompletionSource Retired { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal long LeaseStarted;
        internal long LastRenewal;
    }
}
