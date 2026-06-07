using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Magnetar.Protocol.Runtime;
using Quasar.Models;

namespace Quasar.Services.Analytics;

public sealed class MetricsStoreService : IHostedService, IDisposable
{
    private const int CompactEveryMinutes = 12 * 60;
    private const int CompactionMaxFileSizeBytes = 32 * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    private static readonly string[] ValidBuckets = ["r", "m", "h"];
    private const string RawBucket = "r";
    private const string OneMinuteBucket = "m";
    private const string OneHourBucket = "h";

    private readonly DedicatedServerCatalog _catalog;
    private readonly AnalyticsStoreOptions _analyticsStoreOptions;
    private readonly ILogger<MetricsStoreService> _logger;
    private readonly ConcurrentDictionary<string, ServerMetricsStore> _stores = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, PersistProgress> _persistProgress = new(StringComparer.OrdinalIgnoreCase);
    private readonly Channel<(string uniqueName, MetricSample sample)> _channel = Channel.CreateBounded<(string uniqueName, MetricSample sample)>(
        new BoundedChannelOptions(512)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });

    private readonly CancellationTokenSource _shutdown = new();
    private Task? _ingestLoopTask;
    private DateTimeOffset _lastPersistUtc = DateTimeOffset.UtcNow;
    private DateTimeOffset _nextCompactionUtc;
    private int _itemsSincePersistCheck;
    private int _persistInFlight;
    private int _disposed;

    public MetricsStoreService(
        DedicatedServerCatalog catalog,
        AnalyticsStoreOptions analyticsStoreOptions,
        ILogger<MetricsStoreService> logger)
    {
        _catalog = catalog;
        _analyticsStoreOptions = analyticsStoreOptions;
        _logger = logger;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        try
        {
            _shutdown.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        _shutdown.Dispose();
    }

    public event Action? Changed;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var server in _catalog.GetServers())
        {
            var store = GetOrCreateStore(server.UniqueName);
            var progress = _persistProgress.GetOrAdd(server.UniqueName, _ => new PersistProgress());
            await TryLoadFromDiskAsync(server.UniqueName, store, progress, cancellationToken);
        }

        var now = DateTimeOffset.UtcNow;
        _lastPersistUtc = now;
        _nextCompactionUtc = now.AddMinutes(CompactEveryMinutes);
        _ingestLoopTask = Task.Run(() => IngestLoopAsync(_shutdown.Token), CancellationToken.None);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _channel.Writer.TryComplete();

        if (_ingestLoopTask is not null)
        {
            try
            {
                await _ingestLoopTask.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                _shutdown.Cancel();
                throw;
            }
        }

        await PersistAllAsync(cancellationToken);
    }

    public void Enqueue(string uniqueName, in MetricSample sample)
    {
        if (string.IsNullOrWhiteSpace(uniqueName))
            return;

        GetOrCreateStore(uniqueName);
        _channel.Writer.TryWrite((uniqueName, sample));
    }

    public ServerMetricsStore? GetStore(string uniqueName)
    {
        if (string.IsNullOrWhiteSpace(uniqueName))
            return null;

        return _stores.TryGetValue(uniqueName, out var store) ? store : null;
    }

    public IReadOnlyList<string> GetUniqueNames()
    {
        return _stores.Keys
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task PersistAllAsync(CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _persistInFlight, 1) == 1)
            return;

        try
        {
            foreach (var pair in _stores.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            {
                if (_persistProgress.TryGetValue(pair.Key, out var progress))
                    await PersistStoreAsync(pair.Key, pair.Value, progress, ct);
            }

            _lastPersistUtc = DateTimeOffset.UtcNow;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to persist analytics store state.");
        }
        finally
        {
            Interlocked.Exchange(ref _persistInFlight, 0);
        }
    }

    private async Task IngestLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (await _channel.Reader.WaitToReadAsync(cancellationToken))
            {
                while (_channel.Reader.TryRead(out var item))
                {
                    var store = GetOrCreateStore(item.uniqueName);
                    store.Ingest(item.sample);
                    NotifyChanged();

                    _itemsSincePersistCheck++;
                    if (_itemsSincePersistCheck >= 100)
                    {
                        _itemsSincePersistCheck = 0;
                        if ((DateTimeOffset.UtcNow - _lastPersistUtc) > TimeSpan.FromMinutes(7))
                            _ = PersistAllAsync();
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Metrics ingest loop terminated unexpectedly.");
        }
    }

    private void NotifyChanged()
    {
        try
        {
            Changed?.Invoke();
        }
        catch
        {
        }
    }

    private ServerMetricsStore GetOrCreateStore(string uniqueName)
    {
        return _stores.GetOrAdd(
            uniqueName,
            key =>
            {
                _persistProgress.TryAdd(key, new PersistProgress());
                return new ServerMetricsStore(_analyticsStoreOptions);
            });
    }

    private async Task PersistStoreAsync(string uniqueName, ServerMetricsStore store, PersistProgress progress, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var retentionCutoff = GetRetentionCutoffUnixSeconds(now);
        var path = MagnetarPaths.GetQuasarServerAnalyticsPath(uniqueName);

        var rawSamples = store.Raw.ReadAll();
        var oneMinuteSamples = store.OneMinute.ReadAll();
        var oneHourSamples = store.OneHour.ReadAll();

        var rawLines = BuildSeriesLines(RawBucket, rawSamples, progress.RawLastPersistedTimestamp, retentionCutoff);
        var oneMinuteLines = BuildSeriesLines(OneMinuteBucket, oneMinuteSamples, progress.OneMinuteLastPersistedTimestamp, retentionCutoff);
        var oneHourLines = BuildSeriesLines(OneHourBucket, oneHourSamples, progress.OneHourLastPersistedTimestamp, retentionCutoff);

        if (rawLines.Count > 0 || oneMinuteLines.Count > 0 || oneHourLines.Count > 0)
        {
            await AppendSeriesLinesAsync(path, rawLines, oneMinuteLines, oneHourLines, cancellationToken);
            if (rawLines.Count > 0)
                progress.RawLastPersistedTimestamp = rawLines[^1].TimestampUnixSeconds;
            if (oneMinuteLines.Count > 0)
                progress.OneMinuteLastPersistedTimestamp = oneMinuteLines[^1].TimestampUnixSeconds;
            if (oneHourLines.Count > 0)
                progress.OneHourLastPersistedTimestamp = oneHourLines[^1].TimestampUnixSeconds;
        }

        if (now >= _nextCompactionUtc || IsAnalyticsFileOvergrown(path))
        {
            await RewriteStoreAsJsonlAsync(store, path, progress, now, cancellationToken);
            _nextCompactionUtc = now.AddMinutes(CompactEveryMinutes);
        }
    }

    private async Task AppendSeriesLinesAsync(
        string path,
        List<PersistedMetricLogLine> rawLines,
        List<PersistedMetricLogLine> oneMinuteLines,
        List<PersistedMetricLogLine> oneHourLines,
        CancellationToken cancellationToken)
    {
        if (rawLines.Count == 0 && oneMinuteLines.Count == 0 && oneHourLines.Count == 0)
            return;

        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(directory))
            throw new InvalidOperationException($"Cannot resolve directory for path '{path}'.");

        Directory.CreateDirectory(directory);

        await using var stream = new FileStream(
            path,
            FileMode.Append,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 16 * 1024,
            useAsync: true);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        foreach (var line in rawLines)
            await writer.WriteLineAsync(JsonSerializer.Serialize(line, JsonOptions));

        foreach (var line in oneMinuteLines)
            await writer.WriteLineAsync(JsonSerializer.Serialize(line, JsonOptions));

        foreach (var line in oneHourLines)
            await writer.WriteLineAsync(JsonSerializer.Serialize(line, JsonOptions));

        await writer.FlushAsync(cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private async Task RewriteStoreAsJsonlAsync(
        ServerMetricsStore store,
        string destinationPath,
        PersistProgress progress,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var retentionCutoff = GetRetentionCutoffUnixSeconds(now);
        var rawSamples = store.Raw.ReadAll();
        var oneMinuteSamples = store.OneMinute.ReadAll();
        var oneHourSamples = store.OneHour.ReadAll();

        var rawLines = BuildSeriesLines(RawBucket, rawSamples, 0, retentionCutoff);
        var oneMinuteLines = BuildSeriesLines(OneMinuteBucket, oneMinuteSamples, 0, retentionCutoff);
        var oneHourLines = BuildSeriesLines(OneHourBucket, oneHourSamples, 0, retentionCutoff);

        if (rawLines.Count == 0 && oneMinuteLines.Count == 0 && oneHourLines.Count == 0)
        {
            TryDelete(destinationPath);
            progress.RawLastPersistedTimestamp = 0;
            progress.OneMinuteLastPersistedTimestamp = 0;
            progress.OneHourLastPersistedTimestamp = 0;
            return;
        }

        var tempPath = BuildTempPath(destinationPath);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            await using (var stream = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 16 * 1024,
                useAsync: true))
            await using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                foreach (var line in rawLines)
                    await writer.WriteLineAsync(JsonSerializer.Serialize(line, JsonOptions));

                foreach (var line in oneMinuteLines)
                    await writer.WriteLineAsync(JsonSerializer.Serialize(line, JsonOptions));

                foreach (var line in oneHourLines)
                    await writer.WriteLineAsync(JsonSerializer.Serialize(line, JsonOptions));

                await writer.FlushAsync(cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(tempPath, destinationPath, overwrite: true);

            if (rawLines.Count > 0)
                progress.RawLastPersistedTimestamp = rawLines[^1].TimestampUnixSeconds;
            if (oneMinuteLines.Count > 0)
                progress.OneMinuteLastPersistedTimestamp = oneMinuteLines[^1].TimestampUnixSeconds;
            if (oneHourLines.Count > 0)
                progress.OneHourLastPersistedTimestamp = oneHourLines[^1].TimestampUnixSeconds;
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    private async Task TryLoadFromDiskAsync(
        string uniqueName,
        ServerMetricsStore store,
        PersistProgress progress,
        CancellationToken cancellationToken)
    {
        var path = MagnetarPaths.GetQuasarServerAnalyticsPath(uniqueName);
        var retentionCutoff = GetRetentionCutoffUnixSeconds(DateTimeOffset.UtcNow);

        var data = await TryLoadJsonlFromDiskAsync(path, retentionCutoff, cancellationToken);
        var shouldCompact = data?.HasOutOfRetentionData ?? false;

        if (data is null)
            return;

        try
        {
            store.Restore(
                raw: data.Raw.ToArray(),
                oneMinute: data.OneMinute.ToArray(),
                oneHour: data.OneHour.ToArray());

            if (data.Raw.Count > 0)
                progress.RawLastPersistedTimestamp = data.Raw[^1].TimestampUnixSeconds;
            if (data.OneMinute.Count > 0)
                progress.OneMinuteLastPersistedTimestamp = data.OneMinute[^1].TimestampUnixSeconds;
            if (data.OneHour.Count > 0)
                progress.OneHourLastPersistedTimestamp = data.OneHour[^1].TimestampUnixSeconds;

            if (shouldCompact)
                await RewriteStoreAsJsonlAsync(store, path, progress, DateTimeOffset.UtcNow, cancellationToken);

            if (shouldCompact || IsAnalyticsFileOvergrown(path))
                _nextCompactionUtc = DateTimeOffset.UtcNow.AddMinutes(CompactEveryMinutes);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to load analytics history for server {UniqueName}", uniqueName);
        }
    }

    private static async Task<LoadedAnalyticsData?> TryLoadJsonlFromDiskAsync(
        string path,
        long retentionCutoffUnix,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
            return null;

        var loaded = new LoadedAnalyticsData();

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 16 * 1024, useAsync: true);
        using var reader = new StreamReader(stream);
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            PersistedMetricLogLine? entry;
            try
            {
                entry = JsonSerializer.Deserialize<PersistedMetricLogLine>(line, JsonOptions);
            }
            catch (JsonException)
            {
                continue;
            }

            if (entry is null || string.IsNullOrWhiteSpace(entry.Bucket) || !IsValidBucket(entry.Bucket))
                continue;

            var sample = entry.ToMetricSample();
            if (sample.TimestampUnixSeconds < retentionCutoffUnix)
            {
                loaded.HasOutOfRetentionData = true;
                continue;
            }

            switch (entry.Bucket)
            {
                case RawBucket:
                    loaded.Raw.Add(sample);
                    break;
                case OneMinuteBucket:
                    loaded.OneMinute.Add(sample);
                    break;
                case OneHourBucket:
                    loaded.OneHour.Add(sample);
                    break;
            }
        }

        return loaded;
    }

    private static List<PersistedMetricLogLine> BuildSeriesLines(
        string bucket,
        IReadOnlyList<MetricSample> samples,
        long lastPersistedTimestamp,
        long retentionCutoffUnix)
    {
        var lines = new List<PersistedMetricLogLine>(Math.Max(0, samples.Count));
        foreach (var sample in samples)
        {
            if (sample.TimestampUnixSeconds < retentionCutoffUnix)
                continue;

            if (sample.TimestampUnixSeconds <= lastPersistedTimestamp)
                continue;

            lines.Add(PersistedMetricLogLine.From(bucket, sample));
        }

        return lines;
    }

    private static bool IsAnalyticsFileOvergrown(string path)
    {
        try
        {
            return File.Exists(path) && new FileInfo(path).Length > CompactionMaxFileSizeBytes;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsValidBucket(string bucket) => Array.IndexOf(ValidBuckets, bucket) >= 0;

    private static string BuildTempPath(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(directory))
            throw new InvalidOperationException($"Cannot resolve directory for path '{path}'.");

        return Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
        }
    }

    private long GetRetentionCutoffUnixSeconds(DateTimeOffset nowUtc) =>
        nowUtc.ToUnixTimeSeconds() - _analyticsStoreOptions.RetentionDays * 24L * 60L * 60L;

    private sealed class PersistProgress
    {
        public long RawLastPersistedTimestamp;
        public long OneMinuteLastPersistedTimestamp;
        public long OneHourLastPersistedTimestamp;
    }

    private sealed class LoadedAnalyticsData
    {
        public List<MetricSample> Raw { get; } = [];
        public List<MetricSample> OneMinute { get; } = [];
        public List<MetricSample> OneHour { get; } = [];
        public bool HasOutOfRetentionData { get; set; }
    }

    private sealed class PersistedMetricLogLine
    {
        [JsonPropertyName("b")]
        public string Bucket { get; set; } = string.Empty;

        [JsonPropertyName("T")]
        public long TimestampUnixSeconds { get; set; }

        [JsonPropertyName("Ss")]
        public float SimSpeed { get; set; }

        [JsonPropertyName("Cpu")]
        public float CpuPercent { get; set; }

        [JsonPropertyName("Mem")]
        public float MemoryMb { get; set; }

        [JsonPropertyName("Ft")]
        public float FrameTimeMs { get; set; }

        [JsonPropertyName("P")]
        public int PlayersOnline { get; set; }

        [JsonPropertyName("Pcu")]
        public int UsedPcu { get; set; }

        [JsonPropertyName("G")]
        public int ActiveGridCount { get; set; }

        [JsonPropertyName("E")]
        public int ActiveEntityCount { get; set; }

        public static PersistedMetricLogLine From(string bucket, MetricSample sample)
        {
            return new PersistedMetricLogLine
            {
                Bucket = bucket,
                TimestampUnixSeconds = sample.TimestampUnixSeconds,
                SimSpeed = sample.SimSpeed,
                CpuPercent = sample.CpuPercent,
                MemoryMb = sample.MemoryMb,
                FrameTimeMs = sample.FrameTimeMs,
                PlayersOnline = sample.PlayersOnline,
                UsedPcu = sample.UsedPcu,
                ActiveGridCount = sample.ActiveGridCount,
                ActiveEntityCount = sample.ActiveEntityCount,
            };
        }

        public MetricSample ToMetricSample()
        {
            return new MetricSample(
                timestampUnixSeconds: TimestampUnixSeconds,
                simSpeed: SimSpeed,
                cpuPercent: CpuPercent,
                memoryMb: MemoryMb,
                frameTimeMs: FrameTimeMs,
                playersOnline: PlayersOnline,
                usedPcu: UsedPcu,
                activeGridCount: ActiveGridCount,
                activeEntityCount: ActiveEntityCount);
        }
    }

}
