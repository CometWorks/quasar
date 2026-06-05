using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Magnetar.Protocol.Runtime;
using Quasar.Models;

namespace Quasar.Services.Analytics;

public sealed class MetricsStoreService : IHostedService, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    private readonly DedicatedServerCatalog _catalog;
    private readonly ILogger<MetricsStoreService> _logger;
    private readonly ConcurrentDictionary<string, ServerMetricsStore> _stores = new(StringComparer.OrdinalIgnoreCase);
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
    private int _itemsSincePersistCheck;
    private int _persistInFlight;
    private int _disposed;

    public MetricsStoreService(
        DedicatedServerCatalog catalog,
        ILogger<MetricsStoreService> logger)
    {
        _catalog = catalog;
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

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var server in _catalog.GetServers())
        {
            var store = _stores.GetOrAdd(server.UniqueName, _ => new ServerMetricsStore());
            await TryLoadFromDiskAsync(server.UniqueName, store, cancellationToken);
        }

        _lastPersistUtc = DateTimeOffset.UtcNow;
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
                await PersistStoreAsync(pair.Key, pair.Value, ct);

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
                    var store = _stores.GetOrAdd(item.uniqueName, _ => new ServerMetricsStore());
                    store.Ingest(item.sample);

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

    private async Task PersistStoreAsync(string uniqueName, ServerMetricsStore store, CancellationToken cancellationToken)
    {
        var payload = new PersistedAnalyticsDocument
        {
            Raw = store.Raw.ReadAll().Select(PersistedMetricSample.FromMetricSample).ToArray(),
            OneMinute = store.OneMinute.ReadAll().Select(PersistedMetricSample.FromMetricSample).ToArray(),
            OneHour = store.OneHour.ReadAll().Select(PersistedMetricSample.FromMetricSample).ToArray(),
        };

        var json = JsonSerializer.Serialize(payload, JsonOptions);
        var path = MagnetarPaths.GetQuasarServerAnalyticsPath(uniqueName);
        await AtomicFileWriter.WriteTextAsync(path, json, cancellationToken);
    }

    private async Task TryLoadFromDiskAsync(string uniqueName, ServerMetricsStore store, CancellationToken cancellationToken)
    {
        var path = MagnetarPaths.GetQuasarServerAnalyticsPath(uniqueName);
        if (!File.Exists(path))
            return;

        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken);
            var payload = JsonSerializer.Deserialize<PersistedAnalyticsDocument>(json, JsonOptions);
            if (payload is null)
                return;

            store.Restore(
                raw: payload.Raw.Select(item => item.ToMetricSample()).ToArray(),
                oneMinute: payload.OneMinute.Select(item => item.ToMetricSample()).ToArray(),
                oneHour: payload.OneHour.Select(item => item.ToMetricSample()).ToArray());
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

    private sealed class PersistedAnalyticsDocument
    {
        [JsonPropertyName("r")]
        public PersistedMetricSample[] Raw { get; set; } = [];

        [JsonPropertyName("m")]
        public PersistedMetricSample[] OneMinute { get; set; } = [];

        [JsonPropertyName("h")]
        public PersistedMetricSample[] OneHour { get; set; } = [];
    }

    private sealed class PersistedMetricSample
    {
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

        public static PersistedMetricSample FromMetricSample(MetricSample sample)
        {
            return new PersistedMetricSample
            {
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
