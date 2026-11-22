using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Magnetar.Protocol.Model;
using Magnetar.Protocol.Runtime;

namespace Quasar.Services.Analytics;

/// <summary>
/// Persisted history of plugin statistics, one series per (source, provider, group, label), so one
/// label's history (say one player's Steam ID) can be charted over days.
///
/// A source is a standalone server's unique name, or a whole cluster (<see cref="ClusterKey"/>): a
/// label's rows from different nodes form one history across node recycling and handovers.
///
/// Rows stay raw for <see cref="AnalyticsStoreOptions.PluginStatsRawRetentionHours"/>, are then
/// rolled up into 1-minute rows until <see cref="AnalyticsStoreOptions.PluginStatsMinuteRetentionDays"/>,
/// then into 1-hour rows until <see cref="AnalyticsStoreOptions.RetentionDays"/>. Each field is
/// folded by its own <c>OverTime</c> hint, so nothing here knows what any provider's numbers mean.
///
/// Like <see cref="MetricsStoreService"/>, each provider of a source has one JSONL file
/// (<c>Magnetars/&lt;source&gt;/plugin-stats/&lt;provider&gt;.jsonl</c>). New rows are appended in
/// batches once a minute; compaction rewrites the file hourly, and earlier when it passes
/// <see cref="AnalyticsStoreOptions.PluginStatsMaxFileBytes"/>.
/// </summary>
public sealed class PluginStatsHistoryService : IHostedService, IDisposable
{
    public const string MetricKeyPrefix = "plugin-history:";
    public const string ClusterKeyPrefix = "cluster-stats-";
    private const string FolderName = "plugin-stats";
    private static readonly string[] Buckets = ["r", "m", "h"];
    private static readonly TimeSpan PersistInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan CompactInterval = TimeSpan.FromHours(1);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly AnalyticsStoreOptions _options;
    private readonly ILogger<PluginStatsHistoryService> _logger;
    private readonly TimeProvider _clock;
    private readonly string? _root;
    private readonly object _sync = new();
    private readonly Dictionary<string, ProviderLog> _logs = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _persistLock = new(1, 1);
    private readonly CancellationTokenSource _stopping = new();
    private Task? _loop;
    private DateTimeOffset _nextCompaction;

    public PluginStatsHistoryService(
        AnalyticsStoreOptions options,
        ILogger<PluginStatsHistoryService> logger
    )
        : this(options, logger, TimeProvider.System, null) { }

    internal PluginStatsHistoryService(
        AnalyticsStoreOptions options,
        ILogger<PluginStatsHistoryService> logger,
        TimeProvider clock,
        string? root
    )
    {
        _options = options;
        _logger = logger;
        _clock = clock;
        _root = root;
        _nextCompaction = clock.GetUtcNow() + CompactInterval;
    }

    public int RetentionDays => _options.RetentionDays;

    private string Root => _root ?? MagnetarPaths.GetQuasarServersDirectory();

    /// <summary>The history source of a whole cluster, shared by all of its nodes.</summary>
    public static string ClusterKey(string clusterId) =>
        ClusterKeyPrefix
        + Convert
            .ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(clusterId)))
            .ToLowerInvariant()[..16];

    public static string BuildMetricKey(
        string provider,
        string group,
        string field,
        string label
    ) =>
        MetricKeyPrefix
        + string.Join(':', new[] { provider, group, field, label }.Select(Uri.EscapeDataString));

    /// <summary>Parses a <c>plugin-history:{provider}:{group}:{field}:{label}</c> metric key.</summary>
    public static bool TryParseMetricKey(
        string? key,
        out string provider,
        out string group,
        out string field,
        out string label
    )
    {
        provider = group = field = label = string.Empty;
        if (key is null || !key.StartsWith(MetricKeyPrefix, StringComparison.Ordinal))
            return false;

        var parts = key[MetricKeyPrefix.Length..].Split(':');
        if (parts.Length != 4)
            return false;

        provider = Uri.UnescapeDataString(parts[0]);
        group = Uri.UnescapeDataString(parts[1]);
        field = Uri.UnescapeDataString(parts[2]);
        label = Uri.UnescapeDataString(parts[3]);
        return provider.Length > 0 && group.Length > 0 && field.Length > 0;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Load();
        _loop = Task.Run(() => LoopAsync(_stopping.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _stopping.Cancel();
        if (_loop is not null)
            await _loop.WaitAsync(cancellationToken);
        await PersistAsync(cancellationToken);
    }

    public void Dispose() => _stopping.Cancel();

    /// <summary>Adds one provider snapshot (already validated and deduped) as one raw row per instance.</summary>
    public void Record(string source, PluginStatsProvider provider)
    {
        if (string.IsNullOrWhiteSpace(source))
            return;

        var time = provider.CapturedAtUtc.ToUnixTimeSeconds();
        lock (_sync)
        {
            var log = GetOrCreateLog(source, provider.Provider, null);
            foreach (var group in provider.Groups)
            {
                var schema = log.Resolve(group);
                foreach (var instance in group.Instances)
                {
                    var series = log.GetSeries(group.Name, instance.Label ?? string.Empty);
                    var row = new Row(time, 1, instance.Values, schema);
                    Insert(series.Tiers[0], row);
                    log.Pending.Add((series, 0, row));
                }
            }
        }
    }

    public IReadOnlyList<string> GetSources()
    {
        lock (_sync)
            return _logs
                .Values.Select(log => log.Source)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order()
                .ToList();
    }

    /// <summary>The fields one source has history for, from each group's latest schema.</summary>
    public IReadOnlyList<PluginStatHistoryMetric> GetFields(string source)
    {
        lock (_sync)
        {
            return _logs
                .Values.Where(log =>
                    string.Equals(log.Source, source, StringComparison.OrdinalIgnoreCase)
                )
                .SelectMany(log =>
                    log.Current.Values.SelectMany(schema =>
                        schema.Fields.Select(field => new PluginStatHistoryMetric(
                            log.Provider,
                            schema.Group,
                            schema.LabelDescription,
                            field.Name,
                            string.IsNullOrWhiteSpace(field.Description)
                                ? field.Name
                                : field.Description,
                            field.Unit
                        ))
                    )
                )
                .OrderBy(field => field.Provider, StringComparer.OrdinalIgnoreCase)
                .ThenBy(field => field.Group, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    /// <summary>The labels seen in one group, the most recently seen first.</summary>
    public IReadOnlyList<PluginStatHistoryLabel> GetLabels(
        string source,
        string provider,
        string group
    )
    {
        lock (_sync)
        {
            if (!_logs.TryGetValue(LogKey(source, provider), out var log))
                return [];

            return log
                .Series.Values.Where(series =>
                    string.Equals(series.Group, group, StringComparison.OrdinalIgnoreCase)
                )
                .Select(series => new PluginStatHistoryLabel(
                    series.Label,
                    series.Tiers.Max(tier => tier.Count > 0 ? tier[^1].Time : 0),
                    series.Tiers.Sum(tier => tier.Count)
                ))
                .OrderByDescending(label => label.LastSeenUnix)
                .ThenBy(label => label.Label, StringComparer.Ordinal)
                .ToList();
        }
    }

    /// <summary>One field of one label between two Unix times, from every tier, oldest first.</summary>
    public PluginStatHistoryRead? Read(
        string source,
        string provider,
        string group,
        string label,
        string field,
        long fromUnix,
        long toUnix
    )
    {
        lock (_sync)
        {
            if (
                !_logs.TryGetValue(LogKey(source, provider), out var log)
                || !log.Series.TryGetValue(SeriesKey(group, label), out var series)
            )
                return null;

            HistoryField? info = null;
            var labelDescription = string.Empty;
            var samples = new List<HistorySample>();
            foreach (var row in series.Tiers.SelectMany(tier => tier).OrderBy(row => row.Time))
            {
                var index = row.Schema.IndexOf(field);
                if (index < 0 || row.Time < fromUnix || row.Time > toUnix)
                    continue;
                info = row.Schema.Fields[index];
                labelDescription = row.Schema.LabelDescription;
                samples.Add(new HistorySample(row.Time, row.Count, row.Values[index]));
            }

            return info is null ? null : new PluginStatHistoryRead(info, labelDescription, samples);
        }
    }

    /// <summary>
    /// Folds samples of one field into one value by its <c>OverTime</c> hint, falling back to the
    /// kind's SDK default. Means are weighted by each sample's row count. Percentiles fold to the
    /// median, since one value per bucket can't carry the distribution.
    /// </summary>
    public static double Fold(HistoryField field, IReadOnlyList<HistorySample> samples)
    {
        switch (ResolveHint(field))
        {
            case "Sum":
                return samples.Sum(sample => sample.Value);
            case "Max":
                return samples.Max(sample => sample.Value);
            case "Min":
                return samples.Min(sample => sample.Value);
            case "Last":
                return samples.MaxBy(sample => sample.Time).Value;
            case "Median":
            case "Percentiles":
                var sorted = samples.Select(sample => sample.Value).Order().ToArray();
                var middle = sorted.Length / 2;
                return sorted.Length % 2 == 1
                    ? sorted[middle]
                    : (sorted[middle - 1] + sorted[middle]) / 2;
            default:
                return samples.Sum(sample => sample.Value * sample.Count)
                    / Math.Max(1, samples.Sum(sample => sample.Count));
        }
    }

    private static string ResolveHint(HistoryField field) =>
        field.OverTime is "Sum" or "Max" or "Min" or "Last" or "Median" or "Percentiles" or "Mean"
            ? field.OverTime
            : field.Kind.ToLowerInvariant() switch
            {
                "counter" => "Sum",
                "discrete" => "Median",
                _ => "Mean",
            };

    /// <summary>Appends new rows; compacts every hour, when asked, or when a file passed the cap.</summary>
    public async Task PersistAsync(
        CancellationToken cancellationToken = default,
        bool compact = false
    )
    {
        await _persistLock.WaitAsync(cancellationToken);
        try
        {
            var now = _clock.GetUtcNow();
            if (now >= _nextCompaction)
            {
                compact = true;
                _nextCompaction = now + CompactInterval;
            }

            List<ProviderLog> logs;
            lock (_sync)
                logs = _logs.Values.ToList();

            foreach (var log in logs)
            {
                try
                {
                    var rewrite = compact || log.Dirty || IsOvergrown(log.Path);
                    await PersistLogAsync(log, now.ToUnixTimeSeconds(), rewrite, cancellationToken);
                }
                catch (Exception exception)
                    when (exception is IOException or UnauthorizedAccessException)
                {
                    _logger.LogWarning(
                        exception,
                        "Failed to persist plugin statistics history to {Path}",
                        log.Path
                    );
                }
            }
        }
        finally
        {
            _persistLock.Release();
        }
    }

    private async Task PersistLogAsync(
        ProviderLog log,
        long now,
        bool rewrite,
        CancellationToken cancellationToken
    )
    {
        List<string> lines;
        Dictionary<string, Schema> fileSchemas;
        int appended;
        lock (_sync)
        {
            if (rewrite)
            {
                // Everything in memory goes into the new file, so the pending rows are covered. If
                // the write fails, Dirty makes the next pass try a full rewrite again.
                lines = Compact(log, now, out fileSchemas);
                log.Pending.Clear();
                log.Dirty = true;
                appended = 0;
            }
            else
            {
                fileSchemas = new Dictionary<string, Schema>(
                    log.FileSchemas,
                    StringComparer.OrdinalIgnoreCase
                );
                lines = Serialize(log, log.Pending, fileSchemas);
                appended = log.Pending.Count;
            }
        }

        if (lines.Count == 0)
        {
            if (rewrite && File.Exists(log.Path))
                File.Delete(log.Path);
        }
        else if (rewrite)
        {
            var text = string.Concat(lines.Select(line => line + "\n"));
            await AtomicFileWriter.WriteTextAsync(log.Path, text, cancellationToken);
        }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(log.Path)!);
            await File.AppendAllLinesAsync(log.Path, lines, cancellationToken);
        }

        lock (_sync)
        {
            log.FileSchemas = fileSchemas;
            if (rewrite)
                log.Dirty = false;
            else
                log.Pending.RemoveRange(0, appended);
        }
    }

    // Applies retention, then tightens it tier by tier until the file fits in 3/4 of the cap, so
    // the oldest detail is rolled up before any history is dropped. Returns the file's lines.
    private List<string> Compact(
        ProviderLog log,
        long now,
        out Dictionary<string, Schema> fileSchemas
    )
    {
        var ages = RetentionAges();
        ApplyRetention(log, now, ages);
        var lines = SerializeAll(log, out fileSchemas);
        var budget = _options.PluginStatsMaxFileBytes * 3 / 4;
        for (var tier = 0; tier < ages.Length && Size(lines) > budget; )
        {
            if (ages[tier] < 60)
            {
                tier++;
                continue;
            }

            ages[tier] /= 2;
            ApplyRetention(log, now, ages);
            lines = SerializeAll(log, out fileSchemas);
        }

        return lines;
    }

    private long[] RetentionAges() =>
        [
            _options.PluginStatsRawRetentionHours * 3600L,
            _options.PluginStatsMinuteRetentionDays * 86400L,
            _options.RetentionDays * 86400L,
        ];

    private static bool ApplyRetention(ProviderLog log, long now, long[] ages)
    {
        var changed = false;
        foreach (var series in log.Series.Values)
        {
            changed |= RollUp(series.Tiers[0], series.Tiers[1], now - ages[0], 60);
            changed |= RollUp(series.Tiers[1], series.Tiers[2], now - ages[1], 3600);
            var cutoff = now - ages[2];
            changed |= series.Tiers[2].RemoveAll(row => row.Time < cutoff) > 0;
        }

        foreach (
            var (key, _) in log
                .Series.Where(pair => pair.Value.Tiers.All(tier => tier.Count == 0))
                .ToList()
        )
            log.Series.Remove(key);

        return changed;
    }

    // Moves the rows of every whole window older than the cutoff into one folded row of the next tier.
    private static bool RollUp(List<Row> source, List<Row> target, long cutoff, long width)
    {
        cutoff -= cutoff % width;
        var count = source.FindIndex(row => row.Time >= cutoff);
        if (count < 0)
            count = source.Count;
        if (count == 0)
            return false;

        foreach (var window in source.Take(count).GroupBy(row => row.Time - row.Time % width))
            Insert(target, FoldRows(window.Key, window.ToList()));
        source.RemoveRange(0, count);
        return true;
    }

    // Folds a window's rows into one row with the newest row's schema, matching fields by name.
    private static Row FoldRows(long time, List<Row> rows)
    {
        var schema = rows[^1].Schema;
        var values = new double[schema.Fields.Length];
        for (var i = 0; i < values.Length; i++)
        {
            var samples = new List<HistorySample>(rows.Count);
            foreach (var row in rows)
            {
                var index = ReferenceEquals(row.Schema, schema)
                    ? i
                    : row.Schema.IndexOf(schema.Fields[i].Name);
                if (index >= 0)
                    samples.Add(new HistorySample(row.Time, row.Count, row.Values[index]));
            }

            values[i] = samples.Count == 0 ? 0 : Fold(schema.Fields[i], samples);
        }

        return new Row(time, rows.Sum(row => row.Count), values, schema);
    }

    private static List<string> SerializeAll(
        ProviderLog log,
        out Dictionary<string, Schema> fileSchemas
    )
    {
        fileSchemas = new Dictionary<string, Schema>(StringComparer.OrdinalIgnoreCase);
        var rows = log.Series.Values.SelectMany(series =>
            series.Tiers.SelectMany((tier, index) => tier.Select(row => (series, index, row)))
        );
        return Serialize(log, rows, fileSchemas);
    }

    // A schema line goes before the first row of a group, and again whenever the group's schema changes.
    private static List<string> Serialize(
        ProviderLog log,
        IEnumerable<(Series Series, int Tier, Row Row)> rows,
        Dictionary<string, Schema> fileSchemas
    )
    {
        var lines = new List<string>();
        foreach (var (series, tier, row) in rows)
        {
            if (
                !fileSchemas.TryGetValue(series.Group, out var written)
                || !ReferenceEquals(written, row.Schema)
            )
            {
                var schemaLine = new Line
                {
                    Provider = log.Provider,
                    Group = series.Group,
                    LabelDescription = row.Schema.LabelDescription,
                    Fields = row.Schema.Fields,
                };
                lines.Add(JsonSerializer.Serialize(schemaLine, JsonOptions));
                fileSchemas[series.Group] = row.Schema;
            }

            var rowLine = new Line
            {
                Bucket = Buckets[tier],
                Group = series.Group,
                Time = row.Time,
                Label = series.Label,
                Count = row.Count,
                Values = row.Values,
            };
            lines.Add(JsonSerializer.Serialize(rowLine, JsonOptions));
        }

        return lines;
    }

    private void Load()
    {
        if (!Directory.Exists(Root))
            return;

        var now = _clock.GetUtcNow().ToUnixTimeSeconds();
        foreach (var directory in Directory.EnumerateDirectories(Root))
        {
            var folder = Path.Combine(directory, FolderName);
            if (!Directory.Exists(folder))
                continue;

            foreach (var path in Directory.EnumerateFiles(folder, "*.jsonl"))
            {
                try
                {
                    LoadFile(Path.GetFileName(directory), path, now);
                }
                catch (Exception exception)
                    when (exception is IOException or UnauthorizedAccessException)
                {
                    _logger.LogWarning(
                        exception,
                        "Failed to load plugin statistics history from {Path}",
                        path
                    );
                }
            }
        }
    }

    private void LoadFile(string source, string path, long now)
    {
        ProviderLog? log = null;
        var schemas = new Dictionary<string, Schema>(StringComparer.OrdinalIgnoreCase);
        foreach (var text in File.ReadLines(path))
        {
            Line? line;
            try
            {
                line = JsonSerializer.Deserialize<Line>(text, JsonOptions);
            }
            catch (JsonException)
            {
                continue; // a line cut short by a crash
            }

            if (line?.Group is not { Length: > 0 } group)
                continue;

            if (line.Fields is not null && line.Provider is { Length: > 0 } provider)
            {
                log ??= GetOrCreateLog(source, provider, path);
                log.Current[group] = schemas[group] = new Schema(
                    group,
                    line.LabelDescription ?? string.Empty,
                    line.Fields
                );
                continue;
            }

            var tier = Array.IndexOf(Buckets, line.Bucket);
            if (
                log is null
                || tier < 0
                || !schemas.TryGetValue(group, out var schema)
                || line.Values?.Length != schema.Fields.Length
            )
                continue;

            var row = new Row(line.Time, Math.Max(1, line.Count), line.Values, schema);
            log.GetSeries(group, line.Label ?? string.Empty).Tiers[tier].Add(row);
        }

        if (log is null)
            return;

        foreach (var tier in log.Series.Values.SelectMany(series => series.Tiers))
            tier.Sort((a, b) => a.Time.CompareTo(b.Time));
        log.FileSchemas = schemas;
        log.Dirty = ApplyRetention(log, now, RetentionAges());
    }

    private async Task LoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(PersistInterval, _clock);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
                await PersistAsync(cancellationToken);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "Plugin statistics history persistence stopped unexpectedly."
            );
        }
    }

    private ProviderLog GetOrCreateLog(string source, string provider, string? path)
    {
        var key = LogKey(source, provider);
        if (!_logs.TryGetValue(key, out var log))
        {
            var name = Uri.EscapeDataString(provider);
            if (name.Length > 120)
                name = Convert
                    .ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(provider)))
                    .ToLowerInvariant();
            path ??= Path.Combine(
                Root,
                string.Join('-', source.Split(Path.GetInvalidFileNameChars())),
                FolderName,
                name + ".jsonl"
            );
            _logs[key] = log = new ProviderLog(source, provider, path);
        }

        return log;
    }

    private bool IsOvergrown(string path)
    {
        try
        {
            return File.Exists(path)
                && new FileInfo(path).Length > _options.PluginStatsMaxFileBytes;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static long Size(List<string> lines) =>
        lines.Sum(line => Encoding.UTF8.GetByteCount(line) + 1L);

    private static void Insert(List<Row> rows, Row row)
    {
        var index = rows.Count;
        while (index > 0 && rows[index - 1].Time > row.Time)
            index--;
        rows.Insert(index, row);
    }

    private static string LogKey(string source, string provider) => source + '\u001f' + provider;

    private static string SeriesKey(string group, string label) => group + '\u001f' + label;

    /// <summary>Row counts per tier (raw, minute, hour) of one label, for tests.</summary>
    internal int[] CountRows(string source, string provider, string group, string label)
    {
        lock (_sync)
        {
            return
                _logs.TryGetValue(LogKey(source, provider), out var log)
                && log.Series.TryGetValue(SeriesKey(group, label), out var series)
                ? series.Tiers.Select(tier => tier.Count).ToArray()
                : [0, 0, 0];
        }
    }

    internal string GetPath(string source, string provider)
    {
        lock (_sync)
            return GetOrCreateLog(source, provider, null).Path;
    }

    private sealed record Row(long Time, int Count, double[] Values, Schema Schema);

    private sealed record Schema(string Group, string LabelDescription, HistoryField[] Fields)
    {
        public int IndexOf(string field) =>
            Array.FindIndex(
                Fields,
                info => string.Equals(info.Name, field, StringComparison.OrdinalIgnoreCase)
            );
    }

    private sealed class Series(string group, string label)
    {
        public string Group { get; } = group;
        public string Label { get; } = label;
        public List<Row>[] Tiers { get; } =
        [
            [],
            [],
            [],
        ];
    }

    private sealed class ProviderLog(string source, string provider, string path)
    {
        public string Source { get; } = source;
        public string Provider { get; } = provider;
        public string Path { get; } = path;
        public Dictionary<string, Series> Series { get; } = new(StringComparer.OrdinalIgnoreCase);

        // Each group's latest schema, and the one the file's last schema line declares.
        public Dictionary<string, Schema> Current { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, Schema> FileSchemas { get; set; } =
            new(StringComparer.OrdinalIgnoreCase);
        public List<(Series Series, int Tier, Row Row)> Pending { get; } = [];
        public bool Dirty { get; set; }

        public Series GetSeries(string group, string label)
        {
            var key = SeriesKey(group, label);
            if (!Series.TryGetValue(key, out var series))
                Series[key] = series = new Series(group, label);
            return series;
        }

        // Reuses the group's current schema while it is unchanged, so rows share one instance.
        public Schema Resolve(PluginStatGroup group)
        {
            var fields = group
                .Fields.Select(field => new HistoryField(
                    field.Name,
                    field.Kind,
                    field.Description,
                    field.Unit,
                    field.OverTime
                ))
                .ToArray();
            if (
                Current.TryGetValue(group.Name, out var current)
                && current.LabelDescription == group.LabelDescription
                && current.Fields.SequenceEqual(fields)
            )
                return current;

            return Current[group.Name] = new Schema(group.Name, group.LabelDescription, fields);
        }
    }

    // One JSONL line: a schema line has Provider and Fields, a row line has Bucket and Values.
    private sealed class Line
    {
        [JsonPropertyName("p")]
        public string? Provider { get; set; }

        [JsonPropertyName("g")]
        public string? Group { get; set; }

        [JsonPropertyName("d")]
        public string? LabelDescription { get; set; }

        [JsonPropertyName("f")]
        public HistoryField[]? Fields { get; set; }

        [JsonPropertyName("b")]
        public string? Bucket { get; set; }

        [JsonPropertyName("t")]
        public long Time { get; set; }

        [JsonPropertyName("l")]
        public string? Label { get; set; }

        [JsonPropertyName("n")]
        public int Count { get; set; }

        [JsonPropertyName("v")]
        public double[]? Values { get; set; }
    }
}

/// <summary>One field's schema: its name, kind and display hints, and how it folds over time.</summary>
public sealed record HistoryField(
    [property: JsonPropertyName("n")] string Name,
    [property: JsonPropertyName("k")] string Kind,
    [property: JsonPropertyName("d")] string Description,
    [property: JsonPropertyName("u")] string Unit,
    [property: JsonPropertyName("o")] string OverTime
);

/// <summary>A value at a time, standing for <see cref="Count"/> raw rows.</summary>
public readonly record struct HistorySample(long Time, int Count, double Value);

public sealed record PluginStatHistoryRead(
    HistoryField Field,
    string LabelDescription,
    IReadOnlyList<HistorySample> Samples
);
