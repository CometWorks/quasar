using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using Magnetar.Protocol.Model;
using Sandbox;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using VRage.ModAPI;

namespace Quasar.Agent
{
    internal static class AgentProfiler
    {
        private const int PublishIntervalSeconds = 1;
        private const int TopLimit = 20;
        private const int RemoveAfterEmptyWindows = 60;

        private static readonly object Sync = new object();
        private static readonly object CallSitesSync = new object();
        private static readonly ConcurrentDictionary<ProfilerKey, Accumulator> Accumulators = new ConcurrentDictionary<ProfilerKey, Accumulator>();
        private static CallSite[] _callSites = new CallSite[128];
        private static int _nextCallSiteId;
        private static DateTime _windowStartUtc = DateTime.MinValue;
        private static DateTime _nextPublishUtc = DateTime.MinValue;
        private static ulong _windowStartFrame;
        private static volatile bool _enabled = true;
        private static AgentProfilerMode _mode = AgentProfilerMode.SafeContinuous;
        private static int _gameThreadId;
        private static ProfilerSnapshot _latestSnapshot;

        public static bool Enabled => _enabled;

        public static AgentProfilerMode Mode => _mode;

        public static void Configure(AgentOptions options)
        {
            var mode = options?.ProfilerMode ?? AgentProfilerMode.SafeContinuous;
            SetMode(mode);
        }

        public static void SetMode(AgentProfilerMode mode)
        {
            _mode = mode;
            _enabled = mode != AgentProfilerMode.Off;

            lock (Sync)
            {
                _windowStartUtc = DateTime.UtcNow;
                _nextPublishUtc = _windowStartUtc.AddSeconds(PublishIntervalSeconds);
                _windowStartFrame = MySandboxGame.Static?.SimulationFrameCounter ?? 0;
                if (!_enabled)
                    _latestSnapshot = null;
            }
        }

        public static void MarkGameThread()
        {
            _gameThreadId = Thread.CurrentThread.ManagedThreadId;
        }

        public static void Update()
        {
            if (!_enabled)
                return;

            var now = DateTime.UtcNow;
            if (_windowStartUtc == DateTime.MinValue)
            {
                lock (Sync)
                {
                    if (_windowStartUtc == DateTime.MinValue)
                    {
                        _windowStartUtc = now;
                        _nextPublishUtc = now.AddSeconds(PublishIntervalSeconds);
                        _windowStartFrame = MySandboxGame.Static?.SimulationFrameCounter ?? 0;
                    }
                }
            }

            if (now >= _nextPublishUtc)
                PublishSnapshot(now);
        }

        public static long Begin()
        {
            return _enabled ? Stopwatch.GetTimestamp() : 0L;
        }

        public static void End(long startTick, int callSiteId, object instance)
        {
            if (startTick <= 0 || !_enabled)
                return;

            var elapsed = Stopwatch.GetTimestamp() - startTick;
            if (elapsed <= 0)
                return;

            AddElapsed(GetCallSite(callSiteId), instance, elapsed);
        }

        public static AgentProfilerToken BeginCallSite(object instance, int callSiteId)
        {
            if (!_enabled)
                return default(AgentProfilerToken);

            return new AgentProfilerToken(callSiteId, instance, Stopwatch.GetTimestamp());
        }

        public static void EndCallSite(AgentProfilerToken token)
        {
            if (token.StartTick <= 0 || !_enabled)
                return;

            var elapsed = Stopwatch.GetTimestamp() - token.StartTick;
            if (elapsed <= 0)
                return;

            AddElapsed(GetCallSite(token.CallSiteId), token.Instance, elapsed);
        }

        public static int RegisterCallSite(string category, MethodBase method)
        {
            return RegisterCallSite(ParseCategory(category), FormatMethod(method), method);
        }

        public static int RegisterCallSite(string category, string methodName)
        {
            return RegisterCallSite(ParseCategory(category), methodName, null);
        }

        public static ProfilerSnapshot GetLatestSnapshot()
        {
            return _latestSnapshot;
        }

        private static int RegisterCallSite(ProfilerCategoryId category, string methodName, MethodBase method)
        {
            if (string.IsNullOrWhiteSpace(methodName))
                methodName = "<unknown>";

            lock (CallSitesSync)
            {
                var id = ++_nextCallSiteId;
                if (id >= _callSites.Length)
                    Array.Resize(ref _callSites, _callSites.Length * 2);

                _callSites[id] = new CallSite(id, category, methodName, method);
                return id;
            }
        }

        private static CallSite GetCallSite(int id)
        {
            var sites = _callSites;
            if (id > 0 && id < sites.Length && sites[id] != null)
                return sites[id];

            return CallSite.Unknown;
        }

        private static void AddElapsed(CallSite callSite, object instance, long elapsedTicks)
        {
            var mainThread = Thread.CurrentThread.ManagedThreadId == _gameThreadId;
            switch (callSite.Category)
            {
                case ProfilerCategoryId.Script:
                    AddToAccumulator(MakeScriptKey(callSite, instance), callSite, instance, elapsedTicks, mainThread);
                    return;

                case ProfilerCategoryId.Entity:
                    AddToAccumulator(MakeTypeKey(ProfilerCategoryId.Entity, instance), callSite, instance, elapsedTicks, mainThread);
                    if (TryGetGrid(instance, out var grid))
                        AddToAccumulator(MakeEntityKey(ProfilerCategoryId.Grid, grid.EntityId), CallSite.GridProjection, grid, elapsedTicks, mainThread);
                    return;

                case ProfilerCategoryId.Grid:
                    if (TryGetGrid(instance, out var directGrid))
                        AddToAccumulator(MakeEntityKey(ProfilerCategoryId.Grid, directGrid.EntityId), CallSite.GridProjection, directGrid, elapsedTicks, mainThread);
                    else
                        AddToAccumulator(MakeTypeKey(ProfilerCategoryId.Entity, instance), callSite, instance, elapsedTicks, mainThread);
                    return;

                case ProfilerCategoryId.Session:
                    AddToAccumulator(MakeTypeKey(ProfilerCategoryId.Session, instance), callSite, instance, elapsedTicks, mainThread);
                    return;

                default:
                    AddToAccumulator(MakeMethodKey(callSite.Category, callSite.Id), callSite, instance, elapsedTicks, mainThread);
                    return;
            }
        }

        private static void AddToAccumulator(ProfilerKey key, CallSite callSite, object instance, long elapsedTicks, bool mainThread)
        {
            var accumulator = Accumulators.GetOrAdd(key, _ => new Accumulator(key, callSite, instance));
            accumulator.Add(elapsedTicks, mainThread, instance);
        }

        private static void PublishSnapshot(DateTime nowUtc)
        {
            lock (Sync)
            {
                var endFrame = MySandboxGame.Static?.SimulationFrameCounter ?? _windowStartFrame;
                var frameCount = (int)Math.Max(1, Math.Min(int.MaxValue, endFrame >= _windowStartFrame ? endFrame - _windowStartFrame : 0));
                var entries = Accumulators
                    .Select(pair => pair.Value.ToSnapshotAndReset(frameCount))
                    .Where(entry => entry != null && IsUsable(entry))
                    .ToArray();

                PruneIdleAccumulators();

                _latestSnapshot = new ProfilerSnapshot
                {
                    CapturedAtUtc = DateTimeOffset.UtcNow,
                    WindowSeconds = Math.Max(1, (int)Math.Round((nowUtc - _windowStartUtc).TotalSeconds)),
                    StartFrame = _windowStartFrame,
                    EndFrame = endFrame,
                    FrameCount = frameCount,
                    GameLoop = BuildBreakdown(entries, frameCount),
                    TopGrids = Top(entries, "grid"),
                    TopScripts = Top(entries, "script"),
                    TopEntityTypes = Top(entries, "entity"),
                    TopMethods = TopSystems(entries),
                    TopPhysics = TopAny(entries, "physics", "physicsDetail"),
                    TopNetworkEvents = TopAny(entries, "network", "networkEvent", "replication", "session"),
                };

                _windowStartUtc = nowUtc;
                _nextPublishUtc = nowUtc.AddSeconds(PublishIntervalSeconds);
                _windowStartFrame = endFrame;
            }
        }

        private static void PruneIdleAccumulators()
        {
            foreach (var pair in Accumulators)
            {
                if (pair.Value.EmptyWindows > RemoveAfterEmptyWindows)
                    Accumulators.TryRemove(pair.Key, out _);
            }
        }

        private static ProfilerTimingBreakdown BuildBreakdown(IEnumerable<ProfilerEntrySnapshot> entries, int frameCount)
        {
            var byCategory = entries
                .GroupBy(entry => entry.Category, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Sum(entry => entry.TotalMs), StringComparer.OrdinalIgnoreCase);

            double PerFrame(string key) => byCategory.TryGetValue(key, out var ms) ? ms / frameCount : 0d;

            var frame = PerFrame("frame");
            var network = PerFrame("network") + PerFrame("networkEvent");
            var known = PerFrame("update")
                        + network
                        + PerFrame("replication")
                        + PerFrame("session")
                        + PerFrame("script")
                        + PerFrame("physics")
                        + PerFrame("parallelWait");

            return new ProfilerTimingBreakdown
            {
                FrameMs = frame,
                UpdateMs = PerFrame("update"),
                NetworkMs = network,
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

        private static ProfilerKey MakeScriptKey(CallSite callSite, object instance)
        {
            if (instance is MyProgrammableBlock block)
                return MakeEntityKey(ProfilerCategoryId.Script, block.EntityId);

            return MakeMethodKey(callSite.Category, callSite.Id);
        }

        private static ProfilerKey MakeMethodKey(ProfilerCategoryId category, int methodId)
        {
            return new ProfilerKey(category, methodId, 0L, null, null);
        }

        private static ProfilerKey MakeEntityKey(ProfilerCategoryId category, long entityId)
        {
            return new ProfilerKey(category, 0, entityId, null, null);
        }

        private static ProfilerKey MakeTypeKey(ProfilerCategoryId category, object instance)
        {
            return new ProfilerKey(category, 0, 0L, instance?.GetType(), null);
        }

        private static bool TryGetGrid(object instance, out MyCubeGrid grid)
        {
            switch (instance)
            {
                case MyCubeGrid cubeGrid:
                    grid = cubeGrid;
                    return true;
                case MyCubeBlock block when block.CubeGrid != null:
                    grid = block.CubeGrid;
                    return true;
                default:
                    grid = null;
                    return false;
            }
        }

        private static string FormatMethod(MethodBase method)
        {
            if (method == null)
                return "<unknown>";

            return $"{method.DeclaringType?.FullName ?? "<unknown>"}#{method.Name}";
        }

        private static ProfilerCategoryId ParseCategory(string category)
        {
            switch ((category ?? string.Empty).Trim())
            {
                case "frame":
                    return ProfilerCategoryId.Frame;
                case "update":
                    return ProfilerCategoryId.Update;
                case "network":
                    return ProfilerCategoryId.Network;
                case "networkEvent":
                    return ProfilerCategoryId.NetworkEvent;
                case "replication":
                    return ProfilerCategoryId.Replication;
                case "session":
                    return ProfilerCategoryId.Session;
                case "script":
                    return ProfilerCategoryId.Script;
                case "physics":
                    return ProfilerCategoryId.Physics;
                case "physicsDetail":
                    return ProfilerCategoryId.PhysicsDetail;
                case "parallelWait":
                    return ProfilerCategoryId.ParallelWait;
                case "parallelRun":
                    return ProfilerCategoryId.ParallelRun;
                case "lock":
                    return ProfilerCategoryId.Lock;
                case "grid":
                    return ProfilerCategoryId.Grid;
                case "entity":
                    return ProfilerCategoryId.Entity;
                case "gps":
                    return ProfilerCategoryId.Gps;
                default:
                    return ProfilerCategoryId.Method;
            }
        }

        private static string CategoryName(ProfilerCategoryId category)
        {
            switch (category)
            {
                case ProfilerCategoryId.Frame:
                    return "frame";
                case ProfilerCategoryId.Update:
                    return "update";
                case ProfilerCategoryId.Network:
                    return "network";
                case ProfilerCategoryId.NetworkEvent:
                    return "networkEvent";
                case ProfilerCategoryId.Replication:
                    return "replication";
                case ProfilerCategoryId.Session:
                    return "session";
                case ProfilerCategoryId.Script:
                    return "script";
                case ProfilerCategoryId.Physics:
                    return "physics";
                case ProfilerCategoryId.PhysicsDetail:
                    return "physicsDetail";
                case ProfilerCategoryId.ParallelWait:
                    return "parallelWait";
                case ProfilerCategoryId.ParallelRun:
                    return "parallelRun";
                case ProfilerCategoryId.Lock:
                    return "lock";
                case ProfilerCategoryId.Grid:
                    return "grid";
                case ProfilerCategoryId.Entity:
                    return "entity";
                case ProfilerCategoryId.Gps:
                    return "gps";
                default:
                    return "method";
            }
        }

        private enum ProfilerCategoryId
        {
            Method,
            Frame,
            Update,
            Network,
            NetworkEvent,
            Replication,
            Session,
            Script,
            Physics,
            PhysicsDetail,
            ParallelWait,
            ParallelRun,
            Lock,
            Grid,
            Entity,
            Gps,
        }

        private sealed class CallSite
        {
            public static readonly CallSite Unknown = new CallSite(0, ProfilerCategoryId.Method, "<unknown>", null);
            public static readonly CallSite GridProjection = new CallSite(0, ProfilerCategoryId.Grid, string.Empty, null);

            public CallSite(int id, ProfilerCategoryId category, string methodName, MethodBase method)
            {
                Id = id;
                Category = category;
                MethodName = methodName ?? string.Empty;
                Method = method;
            }

            public int Id { get; }

            public ProfilerCategoryId Category { get; }

            public string MethodName { get; }

            public MethodBase Method { get; }
        }

        private readonly struct ProfilerKey : IEquatable<ProfilerKey>
        {
            private readonly ProfilerCategoryId _category;
            private readonly int _methodId;
            private readonly long _entityId;
            private readonly Type _type;
            private readonly object _reference;

            public ProfilerKey(ProfilerCategoryId category, int methodId, long entityId, Type type, object reference)
            {
                _category = category;
                _methodId = methodId;
                _entityId = entityId;
                _type = type;
                _reference = reference;
            }

            public ProfilerCategoryId Category => _category;

            public int MethodId => _methodId;

            public long EntityId => _entityId;

            public Type Type => _type;

            public object Reference => _reference;

            public bool Equals(ProfilerKey other)
            {
                return _category == other._category
                       && _methodId == other._methodId
                       && _entityId == other._entityId
                       && _type == other._type
                       && ReferenceEquals(_reference, other._reference);
            }

            public override bool Equals(object obj)
            {
                return obj is ProfilerKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    var hash = (int)_category;
                    hash = (hash * 397) ^ _methodId;
                    hash = (hash * 397) ^ _entityId.GetHashCode();
                    hash = (hash * 397) ^ (_type?.GetHashCode() ?? 0);
                    hash = (hash * 397) ^ (_reference == null ? 0 : RuntimeHelpers.GetHashCode(_reference));
                    return hash;
                }
            }
        }

        private readonly struct EntryInfo
        {
            public EntryInfo(string key, string name, long? entityId, string gridName, string blockName, string typeName)
            {
                Key = key ?? string.Empty;
                Name = name ?? string.Empty;
                EntityId = entityId;
                GridName = gridName ?? string.Empty;
                BlockName = blockName ?? string.Empty;
                TypeName = typeName ?? string.Empty;
            }

            public string Key { get; }

            public string Name { get; }

            public long? EntityId { get; }

            public string GridName { get; }

            public string BlockName { get; }

            public string TypeName { get; }
        }

        private sealed class Accumulator
        {
            private readonly ProfilerKey _key;
            private readonly CallSite _callSite;
            private long _mainThreadTicks;
            private long _offThreadTicks;
            private int _calls;
            private object _lastInstance;
            private int _emptyWindows;

            public Accumulator(ProfilerKey key, CallSite callSite, object instance)
            {
                _key = key;
                _callSite = callSite;
                _lastInstance = instance;
            }

            public int EmptyWindows => _emptyWindows;

            public void Add(long elapsedTicks, bool mainThread, object instance)
            {
                if (instance != null)
                    _lastInstance = instance;

                if (mainThread)
                    Interlocked.Add(ref _mainThreadTicks, elapsedTicks);
                else
                    Interlocked.Add(ref _offThreadTicks, elapsedTicks);

                Interlocked.Increment(ref _calls);
            }

            public ProfilerEntrySnapshot ToSnapshotAndReset(int frameCount)
            {
                var calls = Interlocked.Exchange(ref _calls, 0);
                var mainTicks = Interlocked.Exchange(ref _mainThreadTicks, 0L);
                var offTicks = Interlocked.Exchange(ref _offThreadTicks, 0L);
                if (calls <= 0 || (mainTicks <= 0 && offTicks <= 0))
                {
                    _emptyWindows++;
                    return null;
                }

                _emptyWindows = 0;
                var mainMs = TicksToMs(mainTicks);
                var offMs = TicksToMs(offTicks);
                var totalMs = mainMs + offMs;
                var info = BuildEntryInfo(_key, _callSite, _lastInstance);
                return new ProfilerEntrySnapshot
                {
                    Key = info.Key,
                    Name = info.Name,
                    Category = CategoryName(_key.Category),
                    EntityId = info.EntityId,
                    GridName = info.GridName,
                    BlockName = info.BlockName,
                    TypeName = info.TypeName,
                    MethodName = _callSite.MethodName,
                    MainThreadMs = mainMs,
                    OffThreadMs = offMs,
                    TotalMs = totalMs,
                    MainThreadMsPerFrame = mainMs / frameCount,
                    OffThreadMsPerFrame = offMs / frameCount,
                    TotalMsPerFrame = totalMs / frameCount,
                    Calls = calls,
                };
            }

            private static EntryInfo BuildEntryInfo(ProfilerKey key, CallSite callSite, object instance)
            {
                switch (key.Category)
                {
                    case ProfilerCategoryId.Grid:
                        if (instance is MyCubeGrid grid)
                            return new EntryInfo(grid.EntityId.ToString(), grid.DisplayName ?? "<grid>", grid.EntityId, grid.DisplayName ?? string.Empty, string.Empty, grid.GetType().FullName);
                        return new EntryInfo(key.EntityId.ToString(), key.EntityId.ToString(), key.EntityId, string.Empty, string.Empty, string.Empty);

                    case ProfilerCategoryId.Script:
                        if (instance is MyProgrammableBlock block)
                        {
                            var gridName = block.CubeGrid?.DisplayName ?? string.Empty;
                            var blockName = block.DisplayNameText ?? "<script>";
                            return new EntryInfo(block.EntityId.ToString(), string.IsNullOrWhiteSpace(gridName) ? blockName : $"{gridName}/{blockName}", block.EntityId, gridName, blockName, block.GetType().FullName);
                        }

                        return new EntryInfo(key.EntityId.ToString(), key.EntityId.ToString(), key.EntityId, string.Empty, string.Empty, string.Empty);

                    case ProfilerCategoryId.Entity:
                    case ProfilerCategoryId.Session:
                        var type = key.Type ?? instance?.GetType();
                        var typeName = type?.FullName ?? "<unknown>";
                        return new EntryInfo(typeName, type?.Name ?? typeName, null, string.Empty, string.Empty, typeName);

                    default:
                        var methodKey = key.MethodId.ToString();
                        var methodName = string.IsNullOrWhiteSpace(callSite?.MethodName)
                            ? methodKey
                            : callSite.MethodName;
                        return new EntryInfo(methodKey, methodName, null, string.Empty, string.Empty, instance?.GetType().FullName ?? string.Empty);
                }
            }

            private static double TicksToMs(long ticks)
            {
                return ticks * 1000d / Stopwatch.Frequency;
            }
        }
    }

    internal readonly struct AgentProfilerToken
    {
        public AgentProfilerToken(int callSiteId, object instance, long startTick)
        {
            CallSiteId = callSiteId;
            Instance = instance;
            StartTick = startTick;
        }

        public int CallSiteId { get; }

        public object Instance { get; }

        public long StartTick { get; }
    }
}
