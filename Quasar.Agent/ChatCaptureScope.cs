using System;

namespace Quasar.Agent
{
    /// <summary>One accepted receive, with scoped origin tracking for Agent broadcasts.</summary>
    internal sealed class ChatCaptureScope : IDisposable
    {
        [ThreadStatic] private static ChatCaptureScope _current;
        private readonly ChatCaptureScope _previous;
        private readonly string _content;
        private readonly byte _channel;
        private readonly long _targetId;
        private readonly bool _isBroadcastSend;
        private bool _accepted;
        private bool _captured;
        private bool _disposed;

        public ulong Sender { get; }
        public string AuthorName { get; }
        public bool IsServerMessage { get; }
        public bool IsQuasarBroadcast { get; }

        public ChatCaptureScope(ulong sender, string authorName, bool isServerMessage,
            string content, byte channel, long targetId, bool isBroadcastSend = false)
        {
            _content = content ?? string.Empty;
            _channel = channel;
            _targetId = targetId;
            _isBroadcastSend = isBroadcastSend;
            _previous = _current;
            // Only the receive immediately inside the Agent's matching local send inherits
            // this origin. Recipient callbacks and unrelated/nested messages cannot claim it.
            IsQuasarBroadcast = !isBroadcastSend && _previous?._isBroadcastSend == true &&
                _previous.Matches(_content, channel, targetId);
            Sender = IsQuasarBroadcast ? _previous.Sender : sender;
            AuthorName = authorName;
            IsServerMessage = IsQuasarBroadcast || isServerMessage;
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
            if (source == null || source._isBroadcastSend || !source._accepted || source._captured ||
                !source.Matches(content, channel, targetId))
                return false;

            source._captured = true;
            return true;
        }

        private bool Matches(string content, byte channel, long targetId) =>
            _channel == channel && _targetId == targetId &&
            string.Equals(_content, content ?? string.Empty, StringComparison.Ordinal);

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _current = _previous;
        }
    }
}
