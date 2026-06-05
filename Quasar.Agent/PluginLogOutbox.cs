using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
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

        private readonly ConcurrentQueue<string> _queue = new ConcurrentQueue<string>();
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

            _queue.Enqueue(line);

            // Trim from the front if we are over the cap (drop oldest first).
            if (Interlocked.Increment(ref _count) > MaxBufferedLines &&
                _queue.TryDequeue(out _))
            {
                Interlocked.Decrement(ref _count);
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
