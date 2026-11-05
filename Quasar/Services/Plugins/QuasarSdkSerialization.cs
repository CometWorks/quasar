using System.Text.Json;
using Quasar.Plugin.Abstractions.Operations;

namespace Quasar.Services.Plugins;

internal static class QuasarSdkSerialization
{
    internal static readonly JsonSerializerOptions Items = new(JsonSerializerDefaults.Web) { MaxDepth = QuasarSdkLimits.MaximumJsonDepth };

    internal static async Task<byte[]> BoundedAsync(object? value, int maximum, JsonSerializerOptions options, CancellationToken token)
    {
        using var buffer = new MemoryStream();
        await using (var bounded = new LimitedWriteStream(buffer, maximum))
            await JsonSerializer.SerializeAsync(bounded, value, options, token);
        return buffer.ToArray();
    }

    private sealed class LimitedWriteStream(Stream inner, int limit) : Stream
    {
        private int written;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => written;
        public override long Position { get => written; set => throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (buffer.Length > limit - written) throw new QuasarOperationException(413, "SDK serialized message exceeds its limit.");
            inner.Write(buffer); written += buffer.Length;
        }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
        { token.ThrowIfCancellationRequested(); Write(buffer.Span); return ValueTask.CompletedTask; }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
