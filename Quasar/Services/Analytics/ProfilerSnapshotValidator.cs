using Magnetar.Protocol.Model;

namespace Quasar.Services.Analytics;

internal static class ProfilerSnapshotValidator
{
    private const int MaxWindowSeconds = 300;
    private const int MaxFrameCount = 60 * MaxWindowSeconds;
    private const double MaxMs = 10_000_000d;

    public static bool TryNormalize(ProfilerSnapshot snapshot, out ProfilerSnapshot normalized)
    {
        normalized = new ProfilerSnapshot();
        if (snapshot is null || snapshot.CapturedAtUtc == default)
            return false;

        var frameCount = Math.Clamp(snapshot.FrameCount <= 0 ? 1 : snapshot.FrameCount, 1, MaxFrameCount);
        normalized = new ProfilerSnapshot
        {
            CapturedAtUtc = snapshot.CapturedAtUtc,
            WindowSeconds = Math.Clamp(snapshot.WindowSeconds <= 0 ? 1 : snapshot.WindowSeconds, 1, MaxWindowSeconds),
            StartFrame = snapshot.StartFrame,
            EndFrame = snapshot.EndFrame,
            FrameCount = frameCount,
            GameLoop = NormalizeBreakdown(snapshot.GameLoop),
            TopGrids = NormalizeEntries(snapshot.TopGrids, frameCount),
            TopScripts = NormalizeEntries(snapshot.TopScripts, frameCount),
            TopEntityTypes = NormalizeEntries(snapshot.TopEntityTypes, frameCount),
            TopMethods = NormalizeEntries(snapshot.TopMethods, frameCount),
            TopPhysics = NormalizeEntries(snapshot.TopPhysics, frameCount),
            TopNetworkEvents = NormalizeEntries(snapshot.TopNetworkEvents, frameCount),
        };

        return true;
    }

    private static ProfilerTimingBreakdown NormalizeBreakdown(ProfilerTimingBreakdown? value)
    {
        value ??= new ProfilerTimingBreakdown();
        return new ProfilerTimingBreakdown
        {
            FrameMs = ClampMs(value.FrameMs),
            UpdateMs = ClampMs(value.UpdateMs),
            NetworkMs = ClampMs(value.NetworkMs),
            ReplicationMs = ClampMs(value.ReplicationMs),
            SessionComponentsMs = ClampMs(value.SessionComponentsMs),
            ScriptsMs = ClampMs(value.ScriptsMs),
            PhysicsMs = ClampMs(value.PhysicsMs),
            ParallelWaitMs = ClampMs(value.ParallelWaitMs),
            OtherMs = ClampMs(value.OtherMs),
        };
    }

    private static List<ProfilerEntrySnapshot> NormalizeEntries(IEnumerable<ProfilerEntrySnapshot>? entries, int frameCount)
    {
        return (entries ?? [])
            .Where(entry => entry is not null)
            .Select(entry => NormalizeEntry(entry, frameCount))
            .Where(entry => entry.TotalMs > 0d)
            .OrderByDescending(entry => entry.TotalMs)
            .Take(ProfilerStoreService.ClampTopCount(50))
            .ToList();
    }

    private static ProfilerEntrySnapshot NormalizeEntry(ProfilerEntrySnapshot entry, int frameCount)
    {
        var main = ClampMs(entry.MainThreadMs);
        var off = ClampMs(entry.OffThreadMs);
        var total = ClampMs(Math.Max(entry.TotalMs, main + off));
        return new ProfilerEntrySnapshot
        {
            Key = Clean(entry.Key),
            Name = Clean(entry.Name),
            Category = Clean(entry.Category),
            EntityId = entry.EntityId,
            GridName = Clean(entry.GridName),
            BlockName = Clean(entry.BlockName),
            TypeName = Clean(entry.TypeName),
            MethodName = Clean(entry.MethodName),
            MainThreadMs = main,
            OffThreadMs = off,
            TotalMs = total,
            MainThreadMsPerFrame = main / frameCount,
            OffThreadMsPerFrame = off / frameCount,
            TotalMsPerFrame = total / frameCount,
            Calls = Math.Max(0, entry.Calls),
        };
    }

    private static string Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        value = value.Trim();
        return value.Length <= 240 ? value : value[..240];
    }

    private static double ClampMs(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value < 0d)
            return 0d;

        return Math.Min(value, MaxMs);
    }
}
