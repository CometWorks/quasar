using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;
using Quasar.Plugin.Abstractions.Operations;
using Quasar.Services.Auth;

namespace Quasar.Services.Plugins;

// Public ASP.NET endpoint/features adapt the framework-owned circuit. No internal
// ComponentHub access, application URL proxy or sidecar identity is involved.
public sealed class QuasarRtcCircuits(EndpointDataSource endpoints, QuasarBrowserSessions sessions,
    IServiceScopeFactory scopes, IHttpContextFactory contexts, TimeProvider clock)
{
    private readonly object gate = new();
    private readonly SemaphoreSlim negotiating = new(1, 1);
    private readonly Dictionary<string, Ticket> pending = new(StringComparer.Ordinal);
    private sealed record Ticket(QuasarBrowserSession Binding, string Token, string Scheme, HostString Host, string Origin,
        IPAddress? RemoteAddress, int RemotePort, long Created);

    public async Task NegotiateAsync(HttpContext http, IAntiforgery csrf)
    {
        await csrf.ValidateRequestAsync(http);
        var binding = await sessions.BindHttpSessionAsync(http);
        var origin = http.Request.Headers.Origin;
        if (origin.Count > 1 || origin.Count == 1 && (!Uri.TryCreate(origin[0], UriKind.Absolute, out var browserOrigin)
            || browserOrigin.UserInfo.Length != 0 || browserOrigin.AbsolutePath != "/" || browserOrigin.Query.Length != 0 || browserOrigin.Fragment.Length != 0
            || !string.Equals(browserOrigin.GetLeftPart(UriPartial.Authority), http.Request.Scheme + "://" + http.Request.Host, StringComparison.OrdinalIgnoreCase)))
            throw new QuasarOperationException(403, "The circuit origin must match this panel origin.");
        if (http.Request.Query["negotiateVersion"] != "1") throw new QuasarOperationException(426, "Circuit transport requires negotiation version 1.");
        using var body = new MemoryStream();
        await negotiating.WaitAsync(http.RequestAborted);
        try
        {
            lock (gate)
            {
                Sweep();
                if (pending.Count >= 64 || pending.Values.Count(t => t.Binding.Id == binding.Id) >= 4
                    || pending.Values.Count(t => t.Binding.Owner == binding.Owner) >= 16)
                    throw new QuasarOperationException(429, "Too many pending direct circuits.");
            }
            var endpoint = CoreEndpoint("/_blazor/negotiate");
            await AuthorizeAsync(endpoint, http);
            var originalPath = http.Request.Path; var originalEndpoint = http.GetEndpoint(); var originalBody = http.Response.Body;
            try
            {
                http.Request.Path = "/_blazor/negotiate"; http.SetEndpoint(endpoint); http.Response.Body = body;
                await endpoint.RequestDelegate!(http);
            }
            finally { http.Request.Path = originalPath; http.SetEndpoint(originalEndpoint); http.Response.Body = originalBody; }
            if (body.Length > QuasarSdkLimits.MaximumJsonBytes) throw new QuasarOperationException(502, "Invalid framework negotiation response.");
            if (http.Response.StatusCode == 200)
            {
                using var json = JsonDocument.Parse(body.GetBuffer().AsMemory(0, (int)body.Length));
                var token = json.RootElement.GetProperty("connectionToken").GetString();
                if (token is not { Length: >= 16 and <= 128 } || token.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')))
                    throw new QuasarOperationException(502, "Invalid framework circuit token.");
                sessions.Verify(binding, http.RequestAborted);
                lock (gate) pending.Add(token, new(binding, token, http.Request.Scheme, http.Request.Host, origin.ToString(),
                    http.Connection.RemoteIpAddress, http.Connection.RemotePort, clock.GetTimestamp()));
            }
        }
        finally { negotiating.Release(); }
        // A slow HTTP client never holds the admission gate.
        body.Position = 0; await body.CopyToAsync(http.Response.Body, http.RequestAborted);
    }

    public async Task RunAsync(QuasarBrowserSession binding, string token, QuasarRtcCircuitSocket socket, CancellationToken stop)
    {
        sessions.Verify(binding, stop);
        Ticket ticket;
        lock (gate)
        {
            Sweep();
            if (!pending.TryGetValue(token, out ticket!) || !ReferenceEquals(ticket.Binding, binding))
                throw new QuasarOperationException(404, "This browser session does not own the pending circuit.");
            pending.Remove(token); // Single-use across every peer and reconnect.
        }
        await using var scope = scopes.CreateAsyncScope();
        var seed = new DefaultHttpContext();
        var http = contexts.Create(seed.Features);
        var rbac = scope.ServiceProvider.GetRequiredService<RbacConfigCatalog>();
        rbac.Changed += socket.Revoke;
        try
        {
            http.RequestServices = scope.ServiceProvider;
            http.User = scope.ServiceProvider.GetRequiredService<QuasarRoleMapper>().RefreshRoles(binding.User);
            http.RequestAborted = stop;
            http.Request.Method = "GET"; http.Request.Protocol = "HTTP/1.1";
            http.Request.PathBase = binding.PathBase; http.Request.Path = "/_blazor";
            http.Request.QueryString = new("?id=" + Uri.EscapeDataString(ticket.Token));
            http.Request.Scheme = ticket.Scheme; http.Request.Host = ticket.Host;
            if (ticket.Origin.Length != 0) http.Request.Headers.Origin = ticket.Origin;
            http.Connection.RemoteIpAddress = ticket.RemoteAddress; http.Connection.RemotePort = ticket.RemotePort;
            http.Features.Set<IHttpWebSocketFeature>(socket);
            http.Response.Body = Stream.Null;
            var endpoint = CoreEndpoint("/_blazor"); http.SetEndpoint(endpoint);
            await AuthorizeAsync(endpoint, http);
            await endpoint.RequestDelegate!(http);
            if (!socket.Accepted.Task.IsCompletedSuccessfully)
                throw new QuasarOperationException(503, "The framework did not accept its circuit connection.");
        }
        finally { rbac.Changed -= socket.Revoke; contexts.Dispose(http); }
    }

    private static async Task AuthorizeAsync(RouteEndpoint endpoint, HttpContext http)
    {
        var provider = http.RequestServices.GetRequiredService<IAuthorizationPolicyProvider>();
        var policy = await AuthorizationPolicy.CombineAsync(provider, endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>());
        if (policy is not null && !(await http.RequestServices.GetRequiredService<IAuthorizationService>().AuthorizeAsync(http.User, http, policy)).Succeeded)
            throw new QuasarOperationException(403, "The caller cannot access the framework circuit endpoint.");
    }

    private RouteEndpoint CoreEndpoint(string path) => endpoints.Endpoints.OfType<RouteEndpoint>()
        .SingleOrDefault(endpoint => "/" + endpoint.RoutePattern.RawText?.TrimStart('/') == path)
        ?? throw new QuasarOperationException(503, "The Blazor endpoint is unavailable.");
    private void Sweep()
    {
        foreach (var item in pending.Where(pair => pair.Value.Binding.Revoked.IsCancellationRequested
            || clock.GetElapsedTime(pair.Value.Created) >= TimeSpan.FromSeconds(30)).ToArray()) pending.Remove(item.Key);
    }
}

public sealed class QuasarRtcCircuitSocket(PipeReader input, Stream output, Func<Task> end, Func<string, long, Task> messageEnd, Action cancel, Action closePeer, CancellationToken lifetime) : WebSocket, IHttpWebSocketFeature
{
    private long messageStart;
    private int state = (int)WebSocketState.Connecting;
    public TaskCompletionSource Accepted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool IsWebSocketRequest => true;
    public Task<WebSocket> AcceptAsync(WebSocketAcceptContext context)
    {
        if (Interlocked.CompareExchange(ref state, (int)WebSocketState.Open, (int)WebSocketState.Connecting) != (int)WebSocketState.Connecting)
            throw new InvalidOperationException("Circuit socket is single-use.");
        Accepted.TrySetResult(); return Task.FromResult<WebSocket>(this);
    }
    public override WebSocketCloseStatus? CloseStatus => State is WebSocketState.Closed or WebSocketState.CloseReceived ? WebSocketCloseStatus.NormalClosure : null;
    public override string? CloseStatusDescription => null;
    public override WebSocketState State => (WebSocketState)Volatile.Read(ref state);
    public override string? SubProtocol => null;
    public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken token)
    {
        var read = await ReceiveAsync(buffer.AsMemory(), token);
        return new(read.Count, read.MessageType, read.EndOfMessage);
    }
    public override async ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken token = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime);
        var read = await input.ReadAsync(linked.Token);
        var bytes = read.Buffer;
        if (read.IsCanceled) { input.AdvanceTo(bytes.Start); throw new OperationCanceledException(token); }
        if (bytes.IsEmpty && read.IsCompleted)
        {
            input.AdvanceTo(bytes.End); Interlocked.Exchange(ref state, (int)WebSocketState.CloseReceived);
            return new(0, WebSocketMessageType.Close, true);
        }
        // The framework uses zero-byte receives to await readiness before asking
        // its pipeline for writable memory. A zero-byte buffer is never EOF.
        var count = (int)Math.Min(bytes.Length, buffer.Length);
        bytes.Slice(0, count).CopyTo(buffer.Span);
        var consumed = bytes.GetPosition(count); input.AdvanceTo(consumed, consumed);
        return new(count, WebSocketMessageType.Binary, count != 0);
    }
    public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType type, bool endOfMessage, CancellationToken token)
    {
        if (type is not (WebSocketMessageType.Binary or WebSocketMessageType.Text)) throw new WebSocketException("Invalid circuit message type.");
        using var aborted = token.Register(cancel);
        if (output.Position - messageStart + buffer.Count > 8 * 1024 * 1024) throw new WebSocketException("Circuit message exceeds 8 MiB.");
        await output.WriteAsync(buffer.AsMemory(), token);
        if (endOfMessage) { await messageEnd(type == WebSocketMessageType.Text ? "text" : "binary", output.Position); messageStart = output.Position; }
    }
    public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        => CloseOutputAsync(closeStatus, statusDescription, cancellationToken);
    public override async Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref state, (int)WebSocketState.CloseSent, (int)WebSocketState.Open) == (int)WebSocketState.Open) await end();
    }
    public void Revoke() { Abort(); closePeer(); }
    public override void Abort() { Interlocked.Exchange(ref state, (int)WebSocketState.Aborted); cancel(); input.CancelPendingRead(); }
    public override void Dispose() => Abort();
}
