using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Magnetar.Protocol.Transport;

namespace Quasar.Agent
{
    /// <summary>
    /// Runs Quasar's game-thread commands from the agent's own per-tick <see cref="Drain"/> instead of one
    /// <c>MySandboxGame.Invoke</c> each, so many commands finish in the same tick and none waits for the
    /// one before it. Save and stop always run first and never expire. Other commands share a small time
    /// budget per tick, run in arrival order, and are dropped unrun once Quasar has stopped waiting for
    /// them, so a backlog after a stall doesn't pile up on the game thread.
    /// </summary>
    internal sealed class GameThreadCommandQueue
    {
        internal static readonly TimeSpan DefaultBudget = TimeSpan.FromMilliseconds(2);
        private static readonly TimeSpan BusyReportThreshold = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan BusyReportInterval = TimeSpan.FromSeconds(60);

        private readonly ConcurrentQueue<Work> _lifecycle = new ConcurrentQueue<Work>();
        private readonly ConcurrentQueue<Work> _commands = new ConcurrentQueue<Work>();
        private readonly Func<ServerCommandEnvelope, ServerCommandResult> _execute;
        private readonly Func<ServerCommandEnvelope, bool, string, ServerCommandResult> _result;
        private readonly Action<string> _log;
        private readonly Func<long> _clock;
        private readonly long _budgetTicks;
        private long? _lastBusyReport;

        /// <param name="execute">Runs one command on the game thread.</param>
        /// <param name="result">Builds a result for a command that is answered without running.</param>
        internal GameThreadCommandQueue(
            Func<ServerCommandEnvelope, ServerCommandResult> execute,
            Func<ServerCommandEnvelope, bool, string, ServerCommandResult> result,
            Action<string> log,
            TimeSpan? budget = null,
            Func<long> clock = null)
        {
            _execute = execute;
            _result = result;
            _log = log ?? (_ => { });
            _clock = clock ?? Stopwatch.GetTimestamp;
            _budgetTicks = ToTicks(budget ?? DefaultBudget);
        }

        internal int Pending => _lifecycle.Count + _commands.Count;

        /// <summary>Save and stop: Quasar's lifecycle control, which must not wait behind anything.</summary>
        internal static bool IsLifecycle(ServerCommandType type) =>
            type == ServerCommandType.SaveWorld || type == ServerCommandType.StopServer;

        /// <summary>Queues a command for the next tick. Safe from any thread.</summary>
        internal Task<ServerCommandResult> Enqueue(ServerCommandEnvelope command, CancellationToken cancellationToken)
        {
            var now = _clock();
            var lifecycle = IsLifecycle(command.CommandType);
            var work = new Work
            {
                Command = command,
                Completion = new TaskCompletionSource<ServerCommandResult>(TaskCreationOptions.RunContinuationsAsynchronously),
                EnqueuedAt = now,
                // Quasar's own wait for the answer; after it nobody reads the result.
                ExpiresAt = !lifecycle && command.TimeoutMilliseconds > 0
                    ? now + ToTicks(TimeSpan.FromMilliseconds(command.TimeoutMilliseconds))
                    : long.MaxValue,
            };
            if (cancellationToken.CanBeCanceled)
                work.Cancellation = cancellationToken.Register(() => work.Completion.TrySetCanceled());

            (lifecycle ? _lifecycle : _commands).Enqueue(work);
            return work.Completion.Task;
        }

        /// <summary>Runs queued commands. Call once per tick on the game thread.</summary>
        internal void Drain()
        {
            var start = _clock();
            var longestWait = 0L;
            var waited = 0;

            while (_lifecycle.TryDequeue(out var work))
                Run(work, start, ref longestWait, ref waited);

            var deadline = start + _budgetTicks;
            var ran = false;
            // Always make progress, even when one command alone overruns the budget.
            while ((!ran || _clock() < deadline) && _commands.TryDequeue(out var work))
                ran |= Run(work, _clock(), ref longestWait, ref waited);

            if (longestWait >= ToTicks(BusyReportThreshold) &&
                (_lastBusyReport is not long last || start - last >= ToTicks(BusyReportInterval)))
            {
                _lastBusyReport = start;
                _log($"The game thread was busy: {waited} Quasar command(s) waited up to {ToSeconds(longestWait):0.#} s to run.");
            }
        }

        private bool Run(Work work, long now, ref long longestWait, ref int waited)
        {
            work.Cancellation.Dispose();
            if (work.Completion.Task.IsCompleted)
                return false;

            var wait = now - work.EnqueuedAt;
            if (now >= work.ExpiresAt)
            {
                work.Completion.TrySetResult(_result(work.Command, false,
                    $"Not run: it waited {ToSeconds(wait):0.#} s for the game thread and Quasar had stopped waiting for it."));
                return false;
            }

            if (wait > longestWait)
                longestWait = wait;
            if (wait >= ToTicks(BusyReportThreshold))
                waited++;

            try
            {
                work.Completion.TrySetResult(_execute(work.Command));
            }
            catch (Exception exception)
            {
                work.Completion.TrySetResult(_result(work.Command, false, exception.Message));
            }

            return true;
        }

        private static long ToTicks(TimeSpan span) => (long)(span.TotalSeconds * Stopwatch.Frequency);

        private static double ToSeconds(long ticks) => (double)ticks / Stopwatch.Frequency;

        private sealed class Work
        {
            public ServerCommandEnvelope Command;
            public TaskCompletionSource<ServerCommandResult> Completion;
            public CancellationTokenRegistration Cancellation;
            public long EnqueuedAt;
            public long ExpiresAt;
        }
    }
}
