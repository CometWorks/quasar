using System.Buffers.Binary;
using System.Net.WebSockets;
using System.Text.Json;
using Quasar.Plugin.Abstractions.Operations;

namespace Quasar.Services.Plugins;

internal static class QuasarRtcWire
{
    public const int ControlBytes = 66 * 1024;
    public const int ChunkBytes = 16 * 1024;
    public const int WindowBytes = 256 * 1024;
    public const int SmallJsonBytes = 8 * 1024;
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { MaxDepth = 128 };

    public static async Task<int> ReceiveAsync(WebSocket socket, Memory<byte> buffer, WebSocketMessageType expected, CancellationToken token)
    {
        int count = 0;
        while (true)
        {
            if (count == buffer.Length) throw new QuasarOperationException(413, "Peer frame exceeds its negotiated limit.");
            var read = await socket.ReceiveAsync(buffer[count..], token);
            if (read.MessageType == WebSocketMessageType.Close) throw new IOException("Direct connection closed; a write outcome may be unknown.");
            if (read.MessageType != expected) throw new QuasarOperationException(400, "Unexpected peer frame type.");
            count += read.Count;
            if (read.EndOfMessage) return count;
        }
    }

    internal sealed class Credit
    {
        private readonly object gate = new();
        private TaskCompletionSource changed = NewSignal();
        private bool initialized;
        public long Sent { get; private set; }
        private long consumed;
        public long MaximumOutstanding { get; private set; }
        public void Acknowledge(long bytes)
        {
            lock (gate)
            {
                if (bytes < consumed || bytes > Sent) throw new QuasarOperationException(400, "Invalid stream credit.");
                consumed = bytes;
                initialized = true;
                var wake = changed;
                changed = NewSignal();
                wake.TrySetResult();
            }
        }
        public async Task ReserveAsync(int bytes, CancellationToken token)
        {
            while (true)
            {
                Task wait;
                lock (gate)
                {
                    if (initialized && Sent - consumed + bytes <= WindowBytes)
                    {
                        Sent += bytes;
                        MaximumOutstanding = Math.Max(MaximumOutstanding, Sent - consumed);
                        return;
                    }
                    wait = changed.Task;
                }
                await wait.WaitAsync(token);
            }
        }
        private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    internal sealed class Output(uint id, Credit credit, Func<ReadOnlyMemory<byte>, CancellationToken, Task> send,
        Func<object, CancellationToken, Task> control, CancellationToken lifetime) : Stream
    {
        private uint sequence;
        public long Bytes => credit.Sent;
        public async Task EndAsync() => await control(new { v = 1, type = "end", id, bytes = Bytes }, lifetime);
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
        {
            while (!buffer.IsEmpty)
            {
                lifetime.ThrowIfCancellationRequested();
                var count = Math.Min(ChunkBytes, buffer.Length);
                await credit.ReserveAsync(count, lifetime);
                var frame = new byte[count + 8];
                BinaryPrimitives.WriteUInt32LittleEndian(frame, id);
                BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(4), sequence);
                sequence = checked(sequence + 1);
                buffer[..count].CopyTo(frame.AsMemory(8));
                await send(frame, lifetime);
                buffer = buffer[count..];
            }
        }
        public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer.AsMemory(offset, count), lifetime).AsTask().GetAwaiter().GetResult();
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => Bytes;
        public override long Position { get => Bytes; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    // Serialize once. Small command results stay off the bulk queue; large scene
    // and analytics JSON promotes to a credited stream without whole-body buffering.
    internal sealed class JsonOutput(uint id, Output bulk, Func<object, CancellationToken, Task> control, CancellationToken lifetime) : Stream
    {
        private readonly MemoryStream prefix = new(SmallJsonBytes);
        private bool streaming;
        public async Task CompleteAsync()
        {
            if (streaming) await bulk.EndAsync();
            else
            {
                using var document = JsonDocument.Parse(prefix.GetBuffer().AsMemory(0, (int)prefix.Length));
                await control(new { v = 1, type = "result", id, value = document.RootElement }, lifetime);
            }
        }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
        {
            if (!streaming && prefix.Length + buffer.Length <= SmallJsonBytes) { await prefix.WriteAsync(buffer, lifetime); return; }
            if (!streaming)
            {
                streaming = true;
                await control(new { v = 1, type = "json", id, contentType = "application/json" }, lifetime);
                await bulk.WriteAsync(prefix.GetBuffer().AsMemory(0, (int)prefix.Length), lifetime);
                prefix.SetLength(0);
            }
            await bulk.WriteAsync(buffer, lifetime);
        }
        protected override void Dispose(bool disposing) { if (disposing) prefix.Dispose(); base.Dispose(disposing); }
        public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer.AsMemory(offset, count), lifetime).AsTask().GetAwaiter().GetResult();
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
