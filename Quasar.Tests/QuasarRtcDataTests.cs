using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Quasar.Hosting;
using Quasar.Plugin.Abstractions.Operations;
using Quasar.Services.Auth;
using Xunit;

namespace Quasar.Tests;

public sealed partial class QuasarRtcTests
{
    private long dataRead;
    private int dataClosed, dataCommits, subscriptionsClosed;
    private void RegisterDataOperations(IServiceCollection services)
    {
        services.AddQuasarOperation<JsonElement, object>("rtc.fixture", "echo", [QuasarPolicyNames.CanView],
            (context, input, token) => Task.FromResult<object>(input));
        services.AddQuasarOperation<JsonElement, string>("rtc.fixture", "large", [QuasarPolicyNames.CanView],
            (context, input, token) => Task.FromResult(new string('x', 150000)));
        services.AddQuasarOperation<JsonElement, object>("rtc.fixture", "stats", [QuasarPolicyNames.CanView],
            (context, input, token) => Task.FromResult<object>(new { read = Interlocked.Read(ref dataRead), closed = dataClosed, commits = dataCommits, subscriptionsClosed }));
        services.AddQuasarRead<JsonElement>("rtc.fixture", "file", [QuasarPolicyNames.CanView],
            (context, input, token) => Task.FromResult(new QuasarRead(_ => Task.FromResult<Stream>(new DataFile(this)),
                "application/octet-stream", "rtc.bin", DataFile.Size, EnableRanges: true)));
        services.AddQuasarUpload<JsonElement, object>("rtc.fixture", "upload", [QuasarPolicyNames.CanManageSecurity], 1024 * 1024,
            (context, input, staged, token) => Task.FromResult(new QuasarUpload<object>(async commit =>
            {
                await context.EnsureAuthorizedAsync(commit);
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(staged, commit));
                Interlocked.Increment(ref dataCommits);
                return new { length = staged.Length, hash };
            })));
        services.AddQuasarSubscription<JsonElement, int>("rtc.fixture", "watch", [QuasarPolicyNames.CanView],
            (context, input, token) => DataItems(token));
    }
    private async IAsyncEnumerable<int> DataItems([EnumeratorCancellation] CancellationToken token)
    {
        try { for (var i = 0; ; i++) { token.ThrowIfCancellationRequested(); yield return i; await Task.Delay(10, token); } }
        finally { Interlocked.Increment(ref subscriptionsClosed); }
    }
    [RtcPeerFact]
    public async Task RealSdkDirectStreamsUploadsSubscriptionsAndDownloads()
    {
        await HostedPanelState.SaveAsync(new HostedPanelState { Choice = "hosted", PanelId = Prefix[1..], Credential = Prefix[1..] + "." + new string('a', 64) });
        await using var app = await StartAsync(network: true, hosted: true);
        await BrowserAsync(app, "data");
        await UntilAsync(() => app.Services.GetRequiredService<Quasar.Services.Plugins.QuasarRtcSessions>().PeerCount == 0);
    }
    private sealed class DataFile(QuasarRtcTests owner) : Stream
    {
        public const int Size = 32 * 1024 * 1024;
        private long position;
        private bool closed;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            var count = (int)Math.Min(buffer.Length, Size - position);
            for (var i = 0; i < count; i++) buffer.Span[i] = (byte)(position + i);
            position += count; Interlocked.Add(ref owner.dataRead, count);
            return ValueTask.FromResult(count);
        }
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).Result;
        protected override void Dispose(bool disposing) { if (!closed) { closed = true; Interlocked.Increment(ref owner.dataClosed); } base.Dispose(disposing); }
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => Size;
        public override long Position { get => position; set => position = value; }
        public override long Seek(long offset, SeekOrigin origin) => position = origin switch { SeekOrigin.Begin => offset, SeekOrigin.Current => position + offset, _ => Size + offset };
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
