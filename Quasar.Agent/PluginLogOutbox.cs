using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PluginSdk.Logging;

namespace Quasar.Agent
{
    /// <summary>
    /// Buffers the plugin log lines emitted in-process by the PluginSdk Quasar
    /// sink and hands them to the agent connection in batches.
    ///
    /// <para>
    /// The buffer is bounded and survives Quasar outages: lines accumulate while
    /// the agent is disconnected and are flushed once it reconnects, so the
    /// "Recent plugin logs" panel is backfilled instead of losing everything
    /// captured while Quasar was down. When the cap is reached the oldest lines
    /// are dropped first, matching Quasar's own per-server ring buffer policy.
    /// </para>
    /// </summary>
    public sealed class PluginLogOutbox : IDisposable
    {
        // Caps so a long outage or a chatty plugin cannot grow memory without
        // bound, and so a single wire message stays a reasonable size.
        private const int MaxBufferedLines = 10000;
        private const int MaxBatchLines = 500;
        private const string SuppressedPluginName = "Magnetar";

        private readonly ConcurrentQueue<string> _queue = new ConcurrentQueue<string>();
        private readonly object _fileSync = new object();
        private string _infoLogPath;
        private bool _fileWriteDisabled;
        private int _count;
        private int _subscribed;

        /// <summary>Begins capturing emitted plugin log lines. Idempotent.</summary>
        public void Start()
        {
            if (Interlocked.Exchange(ref _subscribed, 1) == 1)
                return;

            LogEnvironment.LineEmitted += Enqueue;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _subscribed, 0) == 0)
                return;

            LogEnvironment.LineEmitted -= Enqueue;
        }

        private void Enqueue(string line)
        {
            if (string.IsNullOrEmpty(line))
                return;

            AppendToMagnetarInfoLog(line);

            if (IsSuppressedPluginLog(line))
                return;

            _queue.Enqueue(line);

            // Trim from the front if we are over the cap (drop oldest first).
            if (Interlocked.Increment(ref _count) > MaxBufferedLines &&
                _queue.TryDequeue(out _))
            {
                Interlocked.Decrement(ref _count);
            }
        }

        private void AppendToMagnetarInfoLog(string line)
        {
            if (_fileWriteDisabled)
                return;

            var path = ResolveInfoLogPath();
            if (string.IsNullOrWhiteSpace(path))
                return;

            try
            {
                lock (_fileSync)
                {
                    var directory = Path.GetDirectoryName(path);
                    if (!string.IsNullOrWhiteSpace(directory))
                        Directory.CreateDirectory(directory);

                    using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
                    using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                    {
                        writer.WriteLine(FormatForMagnetarInfoLog(line));
                    }
                }
            }
            catch
            {
                _fileWriteDisabled = true;
            }
        }

        private string ResolveInfoLogPath()
        {
            if (_infoLogPath != null)
                return _infoLogPath;

            var appDataPath = Environment.GetEnvironmentVariable("QUASAR_MAGNETAR_APPDATA_PATH");
            if (string.IsNullOrWhiteSpace(appDataPath))
            {
                _infoLogPath = string.Empty;
                return _infoLogPath;
            }

            _infoLogPath = Path.Combine(appDataPath.Trim(), "info.log");
            return _infoLogPath;
        }

        private static string FormatForMagnetarInfoLog(string line)
        {
            var trimmed = line.TrimStart();
            if (trimmed.Length == 0 ||
                trimmed[0] != '{' ||
                trimmed.IndexOf("\"plugin\"", StringComparison.Ordinal) < 0)
            {
                return line;
            }

            try
            {
                var root = JObject.Parse(trimmed);
                var timestamp = FormatTimestamp(root["timestamp"]);
                var level = root.Value<string>("level");
                var plugin = root.Value<string>("plugin");
                var thread = root.Value<string>("thread");
                var message = root.Value<string>("message") ?? string.Empty;
                var data = FormatData(root["data"]);
                var exception = root.Value<string>("exception");

                var builder = new StringBuilder();
                if (!string.IsNullOrWhiteSpace(timestamp))
                    builder.Append(timestamp).Append(' ');

                if (!string.IsNullOrWhiteSpace(level))
                    builder.Append(level).Append(": ");

                if (!string.IsNullOrWhiteSpace(plugin))
                    builder.Append('[').Append(plugin).Append("] ");

                if (!string.IsNullOrWhiteSpace(thread))
                    builder.Append("[thread ").Append(thread).Append("] ");

                builder.Append(message);

                if (!string.IsNullOrWhiteSpace(data))
                    builder.Append(' ').Append(data);

                if (!string.IsNullOrWhiteSpace(exception))
                    builder.AppendLine().Append(exception);

                return builder.Length == 0 ? line : builder.ToString();
            }
            catch (Exception)
            {
                return line;
            }
        }

        private static string FormatTimestamp(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null || token.Type == JTokenType.Undefined)
                return string.Empty;

            DateTimeOffset timestamp;
            if (token.Type == JTokenType.Date)
            {
                var value = token.Value<object>();
                if (value is DateTimeOffset offset)
                {
                    timestamp = offset;
                }
                else if (value is DateTime dateTime)
                {
                    timestamp = dateTime.Kind == DateTimeKind.Unspecified
                        ? new DateTimeOffset(DateTime.SpecifyKind(dateTime, DateTimeKind.Utc))
                        : new DateTimeOffset(dateTime.ToUniversalTime());
                }
                else
                {
                    return string.Empty;
                }
            }
            else if (!DateTimeOffset.TryParse(
                token.Value<string>(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out timestamp))
            {
                return string.Empty;
            }

            return timestamp
                .ToUniversalTime()
                .ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture);
        }

        private static string FormatData(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null || token.Type == JTokenType.Undefined)
                return string.Empty;

            return token.Type == JTokenType.String
                ? token.Value<string>()
                : token.ToString(Formatting.None);
        }

        private static bool IsSuppressedPluginLog(string line)
        {
            var trimmed = line.TrimStart();
            if (trimmed.Length == 0 ||
                trimmed[0] != '{' ||
                trimmed.IndexOf("\"plugin\"", StringComparison.Ordinal) < 0)
            {
                return false;
            }

            try
            {
                var plugin = JObject.Parse(trimmed).Value<string>("plugin");
                return string.Equals(plugin, SuppressedPluginName, StringComparison.OrdinalIgnoreCase);
            }
            catch (JsonException)
            {
                return false;
            }
        }

        /// <summary>
        /// Removes and returns up to <see cref="MaxBatchLines"/> buffered lines,
        /// oldest first. Returns an empty list when nothing is queued.
        /// </summary>
        public List<string> DrainBatch()
        {
            var batch = new List<string>();
            while (batch.Count < MaxBatchLines && _queue.TryDequeue(out var line))
            {
                Interlocked.Decrement(ref _count);
                batch.Add(line);
            }

            return batch;
        }

        /// <summary>
        /// Returns lines to the buffer after a failed send so they are retried on
        /// the next connection. Order is not preserved, which is fine: the panel
        /// sorts entries by their embedded timestamp.
        /// </summary>
        public void Requeue(List<string> lines)
        {
            if (lines == null)
                return;

            foreach (var line in lines)
                Enqueue(line);
        }
    }
}
