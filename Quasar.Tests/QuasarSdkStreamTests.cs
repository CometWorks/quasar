using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Quasar.Plugin.Abstractions.Operations;
using Quasar.Services.Auth;
using Quasar.Services.Plugins;
using Xunit;

namespace Quasar.Tests;

public sealed partial class QuasarSdkTests
{
    private long _downloaded;
    private int _largestRead;
    private int _closedReads;
    private int _commits;
    private int _subscriptionsClosed;
    private readonly TaskCompletionSource _reading = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void RegisterTransfers(IServiceCollection services)
    {
        services.AddQuasarRead<JsonElement>("example.plugin", "download", [QuasarPolicyNames.CanView],
            (context, input, token) => Task.FromResult(new QuasarRead(_ => Task.FromResult<Stream>(new GeneratedStream(8 * 1024 * 1024, this)),
                "application/octet-stream", "sample.bin", 8 * 1024 * 1024, EnableRanges: true, EntityTag: "\"sample\"")));
        services.AddQuasarUpload<JsonElement, long>("example.plugin", "upload", [QuasarPolicyNames.CanManageSecurity], 1024 * 1024,
            (context, input, staged, token) => Task.FromResult(new QuasarUpload<long>(async commit =>
            { await context.EnsureAuthorizedAsync(commit); Interlocked.Increment(ref _commits); return staged.Length; })));
        services.AddQuasarSubscription<JsonElement, int>("example.plugin", "watch", [QuasarPolicyNames.CanView],
            (context, input, token) => CountAsync(token));
    }

    private async IAsyncEnumerable<int> CountAsync([EnumeratorCancellation] CancellationToken token)
    {
        try
        {
            for (var value = 0; ; value++) { yield return value; await Task.Delay(20, token); }
        }
        finally { Interlocked.Increment(ref _subscriptionsClosed); }
    }

    [Fact]
    public async Task DownloadsAreBoundedRangedAndDoNotBlockControlRequests()
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Test-Role", QuasarRoles.Viewer);
        var url = await ResourceAsync(client, "download");
        using (var request = new HttpRequestMessage(HttpMethod.Get, url))
        {
            request.Headers.Range = new(10, 19);
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
            Assert.Equal(Enumerable.Range(10, 10).Select(value => (byte)value), await response.Content.ReadAsByteArrayAsync());
            Assert.Equal("sample.bin", response.Content.Headers.ContentDisposition?.FileNameStar ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        }
        url = await ResourceAsync(client, "download");
        using var slow = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        await _reading.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using (var control = await InvokeAsync(client, "read").WaitAsync(TimeSpan.FromSeconds(5))) Assert.Equal(HttpStatusCode.OK, control.StatusCode);
        Assert.InRange(_largestRead, 1, 65536);
        Assert.InRange(Interlocked.Read(ref _downloaded), 10, 512 * 1024);
        using var stream = await slow.Content.ReadAsStreamAsync();
        var buffer = new byte[100];
        Assert.True(await stream.ReadAsync(buffer) > 0);
        slow.Dispose();
    }

    [Fact]
    public async Task UploadsCommitOnlyAfterCompleteBoundedStagingAndCleanUp()
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Test-Role", QuasarRoles.Admin);
        var tokens = await SessionTokensAsync(client);
        var url = await ResourceAsync(client, "upload");
        using (var response = await PutAsync(client, url, new ByteArrayContent(new byte[300000]), tokens))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(300000, await response.Content.ReadFromJsonAsync<long>());
        }
        Assert.Equal(1, _commits);
        url = await ResourceAsync(client, "upload");
        using (var oversized = await PutAsync(client, url, new ByteArrayContent(new byte[1024 * 1024 + 1]), tokens))
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, oversized.StatusCode);
        Assert.Equal(1, _commits);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_root, "SdkUploads"), "*.upload"));
        url = await ResourceAsync(client, "upload");
        using (var interrupted = new InterruptedUpload())
        {
            var sending = PutAsync(client, url, new StreamContent(interrupted), tokens);
            await interrupted.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            interrupted.Abort.Cancel();
            await Assert.ThrowsAnyAsync<Exception>(async () => await sending.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        for (var attempt = 0; attempt < 50 && Directory.EnumerateFiles(Path.Combine(_root, "SdkUploads"), "*.upload").Any(); attempt++)
            await Task.Delay(20);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_root, "SdkUploads"), "*.upload"));
        Assert.Equal(1, _commits);
        url = await ResourceAsync(client, "upload");
        using var scope = app.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<QuasarOperationService>().SaveRuleAsync(User(QuasarRoles.Admin), "example.plugin", "upload",
            new() { Enabled = false, RequiredPolicies = [QuasarPolicyNames.CanManageSecurity] });
        using (var denied = await PutAsync(client, url, new ByteArrayContent(new byte[10]), tokens)) Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal(1, _commits);
    }

    [Fact]
    public async Task ResourceLeasesAreSessionBoundLimitedAndCancellable()
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Test-Role", QuasarRoles.Viewer);
        var tokens = await SessionTokensAsync(client);
        var urls = new List<string>();
        for (var index = 0; index < 4; index++) urls.Add(await ResourceAsync(client, "download"));
        using (var denied = await SendSdkAsync(client, "/resources/example.plugin/download", "{}")) Assert.Equal(HttpStatusCode.TooManyRequests, denied.StatusCode);
        using var other = app.GetTestClient();
        other.DefaultRequestHeaders.Add("X-Test-Role", QuasarRoles.Viewer);
        await SessionTokensAsync(other);
        using (var denied = await other.GetAsync(urls[0])) Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
        using (var request = new HttpRequestMessage(HttpMethod.Delete, urls[0]))
        {
            request.Headers.Add(tokens.GetProperty("headerName").GetString()!, tokens.GetProperty("requestToken").GetString());
            using var cancelled = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.NoContent, cancelled.StatusCode);
        }
        using (var missing = await client.GetAsync(urls[0])) Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.NotNull(await ResourceAsync(client, "download"));
    }

    [Fact]
    public async Task RevokedPermissionsCancelAnActiveSubscription()
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Test-Role", QuasarRoles.Viewer);
        var url = await ResourceAsync(client, "watch");
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
        Assert.Equal("0", await reader.ReadLineAsync());
        using var scope = app.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<QuasarOperationService>().SaveRuleAsync(User(QuasarRoles.Admin), "example.plugin", "watch",
            new() { Enabled = false, RequiredPolicies = [QuasarPolicyNames.CanView] });
        try { while (await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)) is not null) { } }
        catch (Exception error) when (error is IOException or OperationCanceledException) { }
        Assert.Equal(1, _subscriptionsClosed);
    }

    [Fact]
    public async Task BrowserBindingsUseCookieExpiryAndRevokePeerAndTransferTokens()
    {
        await using var app = await StartAsync();
        using var scope = app.Services.CreateScope();
        var expiry = DateTimeOffset.UtcNow.AddMinutes(2);
        using var loginScope = app.Services.CreateScope();
        var login = new DefaultHttpContext { RequestServices = loginScope.ServiceProvider };
        await login.SignInAsync("test", User(QuasarRoles.Admin), new AuthenticationProperties { ExpiresUtc = expiry });
        var request = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        request.Request.PathBase = Prefix;
        request.Request.Headers.Cookie = string.Join("; ", login.Response.Headers.SetCookie.Select(value => value!.Split(';')[0]));
        request.User = (await request.AuthenticateAsync("test")).Principal!;
        var sessions = scope.ServiceProvider.GetRequiredService<QuasarBrowserSessions>();
        var binding = await sessions.BindHttpSessionAsync(request);
        Assert.InRange(Math.Abs((binding.ExpiresAt - expiry).TotalSeconds), 0, 2);
        sessions.Verify(binding);
        var registry = scope.ServiceProvider.GetRequiredService<QuasarTransferRegistry>();
        var transfer = registry.Create(binding, "example.plugin", "download", QuasarOperationKind.Read, JsonSerializer.SerializeToElement(new { }));
        await sessions.RevokeAsync(request);
        Assert.True(binding.Revoked.IsCancellationRequested);
        Assert.True(transfer.Token.IsCancellationRequested);
        Assert.Throws<QuasarOperationException>(() => sessions.Verify(binding));
    }

    private static async Task<string> ResourceAsync(HttpClient client, string operation)
    {
        using var result = await SendSdkAsync(client, "/resources/example.plugin/" + operation, "{}");
        result.EnsureSuccessStatusCode();
        return (await result.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("url").GetString()!;
    }

    private static async Task<HttpResponseMessage> PutAsync(HttpClient client, string url, HttpContent content, JsonElement tokens)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, url) { Content = content };
        request.Headers.Add(tokens.GetProperty("headerName").GetString()!, tokens.GetProperty("requestToken").GetString());
        return await client.SendAsync(request);
    }

    private sealed class GeneratedStream(long length, QuasarSdkTests owner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get; set; }
        public override int Read(byte[] buffer, int offset, int count)
        {
            count = (int)Math.Min(count, length - Position);
            for (var index = 0; index < count; index++) buffer[offset + index] = (byte)((Position + index) % 251);
            Position += count; Interlocked.Add(ref owner._downloaded, count);
            owner._largestRead = Math.Max(owner._largestRead, count); owner._reading.TrySetResult();
            return count;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        { token.ThrowIfCancellationRequested(); var bytes = new byte[buffer.Length]; var count = Read(bytes, 0, bytes.Length); bytes.AsMemory(0, count).CopyTo(buffer); return ValueTask.FromResult(count); }
        public override long Seek(long offset, SeekOrigin origin) => Position = origin switch { SeekOrigin.Begin => offset, SeekOrigin.Current => Position + offset, _ => length + offset };
        protected override void Dispose(bool disposing) { if (disposing) Interlocked.Increment(ref owner._closedReads); base.Dispose(disposing); }
        public override void Flush() => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class InterruptedUpload : Stream
    {
        public CancellationTokenSource Abort { get; } = new();
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _sent;
        private bool _disposed;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            if (!_sent) { _sent = true; buffer.Span.Fill(1); Started.TrySetResult(); return buffer.Length; }
            await Task.Delay(Timeout.Infinite, Abort.Token);
            return 0;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) => ReadAsync(buffer.AsMemory(offset, count), token).AsTask();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing && !_disposed) { _disposed = true; Abort.Cancel(); Abort.Dispose(); } base.Dispose(disposing); }
    }
}
