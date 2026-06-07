using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using Magnetar.Protocol.Model;
using Sandbox;

namespace Quasar.Agent
{
    internal static class AgentProfiler
    {
        private const int SampleWindowSeconds = 10;
        private const int SampleIntervalSeconds = 60;
        private const int TopLimit = 20;

        private static readonly object Sync = new object();
        private static readonly ConcurrentDictionary<string, Accumulator> Accumulators = new ConcurrentDictionary<string, Accumulator>();
        private static DateTime _nextWindowStartUtc = DateTime.MinValue;
        private static DateTime _windowEndUtc;
        private static ulong _startFrame;
        private static volatile bool _active;
        private static int _gameThreadId;
        private static ProfilerSnapshot _latestSnapshot;

        public static bool Active => _active;

        public static void MarkGameThread()
        {
            _gameThreadId = Thread.CurrentThread.ManagedThreadId;
        }

        public static void Update()
        {
            var now = DateTime.UtcNow;
            if (!_active && now >= _nextWindowStartUtc)
                StartWindow(now);

            if (_active && now >= _windowEndUtc)
                FinishWindow(now);
        }

        public static long Begin()
        {
            return _active ? Stopwatch.GetTimestamp() : 0L;
        }

        public static void End(long startTick, string category, object instance, MethodBase method)
        {
            if (startTick <= 0 || !_active)
                return;

            var elapsed = Stopwatch.GetTimestamp() - startTick;
            if (elapsed <= 0)
                return;

            var descriptor = ProfilerDescriptor.From(category, instance, method);
            var accumulator = Accumulators.GetOrAdd(descriptor.AccumulatorKey, _ => new Accumulator(descriptor));
            accumulator.Add(elapsed, Thread.CurrentThread.ManagedThreadId == _gameThreadId);
        }

        public static ProfilerSnapshot GetLatestSnapshot()
        {
            return _latestSnapshot;
        }

        private static void StartWindow(DateTime nowUtc)
        {
            lock (Sync)
            {
                if (_active)
                    return;

                Accumulators.Clear();
                _startFrame = MySandboxGame.Static?.SimulationFrameCounter ?? 0;
                _windowEndUtc = nowUtc.AddSeconds(SampleWindowSeconds);
                _active = true;
            }
        }

        private static void FinishWindow(DateTime nowUtc)
        {
            lock (Sync)
            {
                if (!_active)
                    return;

                _active = false;
                _nextWindowStartUtc = nowUtc.AddSeconds(Math.Max(1, SampleIntervalSeconds - SampleWindowSeconds));

                var endFrame = MySandboxGame.Static?.SimulationFrameCounter ?? _startFrame;
                var frameCount = (int)Math.Max(1, Math.Min(int.MaxValue, endFrame >= _startFrame ? endFrame - _startFrame : 0));
                var entries = Accumulators.Values
                    .Select(value => value.ToSnapshot(frameCount))
                    .Where(IsUsable)
                    .ToArray();

                _latestSnapshot = new ProfilerSnapshot
                {
                    CapturedAtUtc = DateTimeOffset.UtcNow,
                    WindowSeconds = SampleWindowSeconds,
                    StartFrame = _startFrame,
                    EndFrame = endFrame,
                    FrameCount = frameCount,
                    GameLoop = BuildBreakdown(entries, frameCount),
                    TopGrids = Top(entries, "grid"),
                    TopScripts = Top(entries, "script"),
                    TopEntityTypes = Top(entries, "entity"),
                    TopMethods = TopSystems(entries),
                    TopPhysics = Top(entries, "physics"),
                    TopNetworkEvents = TopAny(entries, "network", "replication", "session"),
                };
            }
        }

        private static ProfilerTimingBreakdown BuildBreakdown(IEnumerable<ProfilerEntrySnapshot> entries, int frameCount)
        {
            var byCategory = entries
                .GroupBy(entry => entry.Category, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Sum(entry => entry.TotalMs), StringComparer.OrdinalIgnoreCase);

            double PerFrame(string key) => byCategory.TryGetValue(key, out var ms) ? ms / frameCount : 0d;

            var frame = PerFrame("frame");
            var known = PerFrame("update")
                        + PerFrame("network")
                        + PerFrame("replication")
                        + PerFrame("session")
                        + PerFrame("script")
                        + PerFrame("physics")
                        + PerFrame("parallelWait");

            return new ProfilerTimingBreakdown
            {
                FrameMs = frame,
                UpdateMs = PerFrame("update"),
                NetworkMs = PerFrame("network"),
                ReplicationMs = PerFrame("replication"),
                SessionComponentsMs = PerFrame("session"),
                ScriptsMs = PerFrame("script"),
                PhysicsMs = PerFrame("physics"),
                ParallelWaitMs = PerFrame("parallelWait"),
                OtherMs = Math.Max(0d, frame - known),
            };
        }

        private static List<ProfilerEntrySnapshot> Top(IEnumerable<ProfilerEntrySnapshot> entries, string category)
        {
            return entries
                .Where(entry => string.Equals(entry.Category, category, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(entry => entry.TotalMs)
                .Take(TopLimit)
                .ToList();
        }

        private static List<ProfilerEntrySnapshot> TopAny(IEnumerable<ProfilerEntrySnapshot> entries, params string[] categories)
        {
            var categorySet = new HashSet<string>(categories, StringComparer.OrdinalIgnoreCase);
            return entries
                .Where(entry => categorySet.Contains(entry.Category))
                .OrderByDescending(entry => entry.TotalMs)
                .Take(TopLimit)
                .ToList();
        }

        private static List<ProfilerEntrySnapshot> TopSystems(IEnumerable<ProfilerEntrySnapshot> entries)
        {
            return entries
                .Where(entry =>
                    !string.Equals(entry.Category, "grid", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(entry.Category, "script", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(entry.Category, "entity", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(entry => entry.TotalMs)
                .Take(TopLimit)
                .ToList();
        }

        private static bool IsUsable(ProfilerEntrySnapshot entry)
        {
            return entry.TotalMs >= 0
                   && !double.IsNaN(entry.TotalMs)
                   && !double.IsInfinity(entry.TotalMs)
                   && !string.IsNullOrWhiteSpace(entry.Category);
        }

        private sealed class Accumulator
        {
            private readonly ProfilerDescriptor _descriptor;
            private long _mainThreadTicks;
            private long _offThreadTicks;
            private int _calls;

            public Accumulator(ProfilerDescriptor descriptor)
            {
                _descriptor = descriptor;
            }

            public void Add(long elapsedTicks, bool mainThread)
            {
                if (mainThread)
                    Interlocked.Add(ref _mainThreadTicks, elapsedTicks);
                else
                    Interlocked.Add(ref _offThreadTicks, elapsedTicks);

                Interlocked.Increment(ref _calls);
            }

            public ProfilerEntrySnapshot ToSnapshot(int frameCount)
            {
                var mainMs = TicksToMs(Interlocked.Read(ref _mainThreadTicks));
                var offMs = TicksToMs(Interlocked.Read(ref _offThreadTicks));
                var totalMs = mainMs + offMs;
                return new ProfilerEntrySnapshot
                {
                    Key = _descriptor.Key,
                    Name = _descriptor.Name,
                    Category = _descriptor.Category,
                    EntityId = _descriptor.EntityId,
                    GridName = _descriptor.GridName,
                    BlockName = _descriptor.BlockName,
                    TypeName = _descriptor.TypeName,
                    MethodName = _descriptor.MethodName,
                    MainThreadMs = mainMs,
                    OffThreadMs = offMs,
                    TotalMs = totalMs,
                    MainThreadMsPerFrame = mainMs / frameCount,
                    OffThreadMsPerFrame = offMs / frameCount,
                    TotalMsPerFrame = totalMs / frameCount,
                    Calls = Math.Max(0, _calls),
                };
            }

            private static double TicksToMs(long ticks) => ticks * 1000d / Stopwatch.Frequency;
        }
    }
}
