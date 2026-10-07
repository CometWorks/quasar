using System;

namespace Quasar.Agent
{
    /// <summary>One synchronous server receive operation, including its recipient fan-out.</summary>
    internal sealed class ChatCaptureScope : IDisposable
    {
        [ThreadStatic] private static ChatCaptureScope _current;
        private readonly ChatCaptureScope _previous;
        private readonly string _content;
        private readonly byte _channel;
        private readonly long _targetId;
        private bool _accepted;
        private bool _captured;
        private bool _disposed;

        public ulong Sender { get; }
        public string AuthorName { get; }
        public bool IsServerMessage { get; }

        public ChatCaptureScope(ulong sender, string authorName, bool isServerMessage,
            string content, byte channel, long targetId)
        {
            Sender = sender;
            AuthorName = authorName;
            IsServerMessage = isServerMessage;
            _content = content ?? string.Empty;
            _channel = channel;
            _targetId = targetId;
            _previous = _current;
            _current = this;
        }

        public static void MarkAccepted()
        {
            if (_current != null)
                _current._accepted = true;
        }

        public static bool TryCapture(string content, byte channel, long targetId, out ChatCaptureScope source)
        {
            source = _current;
            if (source == null || !source._accepted || source._captured || source._channel != channel || source._targetId != targetId ||
                !string.Equals(source._content, content ?? string.Empty, StringComparison.Ordinal))
                return false;

            source._captured = true;
            return true;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _current = _previous;
        }
    }
}
