using System.Text.Json;

namespace Quasar.Services.PluginSdk;

/// <summary>
/// In-memory ring buffer of recent plugin log entries, keyed by server unique
/// name. The supervisor feeds entries parsed from each dedicated server's
/// standard output (PluginSdk <c>QuasarLogSink</c> JSON lines); Blazor
/// components subscribe to <see cref="Changed"/> and read recent entries for
/// display. Mirrors the lightweight, lock-guarded, event-raising shape of the
/// other Quasar runtime services.
/// </summary>
public sealed class PluginLogStream
{
    /// <summary>Maximum entries retained per server instance.</summary>
    public const int MaxEntriesPerInstance = 10_000;

    private readonly object _sync = new();
    private readonly Dictionary<string, Queue<PluginLogEntry>> _byUniqueName =
        new(StringComparer.OrdinalIgnoreCase);

    public event Action? Changed;

    /// <summary>Appends one entry, evicting the oldest beyond the per-instance cap.</summary>
    public void Append(PluginLogEntry entry)
    {
        if (entry is null || string.IsNullOrWhiteSpace(entry.UniqueName))
            return;

        lock (_sync)
        {
            if (!_byUniqueName.TryGetValue(entry.UniqueName, out var queue))
            {
                queue = new Queue<PluginLogEntry>(MaxEntriesPerInstance);
                _byUniqueName[entry.UniqueName] = queue;
            }

            queue.Enqueue(entry);
            while (queue.Count > MaxEntriesPerInstance)
                queue.Dequeue();
        }

        Changed?.Invoke();
    }

    /// <summary>Recent entries for one instance, oldest first (empty when unknown).</summary>
    public IReadOnlyList<PluginLogEntry> GetEntries(string uniqueName)
    {
        if (string.IsNullOrWhiteSpace(uniqueName))
            return Array.Empty<PluginLogEntry>();

        lock (_sync)
        {
            return _byUniqueName.TryGetValue(uniqueName, out var queue)
                ? queue.ToArray()
                : Array.Empty<PluginLogEntry>();
        }
    }

    /// <summary>Recent entries across all instances, newest first, capped at <paramref name="limit"/>.</summary>
    public IReadOnlyList<PluginLogEntry> GetRecent(int limit = 200)
    {
        lock (_sync)
        {
            return _byUniqueName.Values
                .SelectMany(queue => queue)
                .OrderByDescending(entry => entry.TimestampUtc)
                .Take(limit)
                .ToList();
        }
    }

    public IReadOnlyList<string> GetUniqueNames()
    {
        lock (_sync)
        {
            return _byUniqueName.Keys
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    public IReadOnlyList<PluginLogEntry> Query(PluginLogQuery query)
    {
        var limit = Math.Clamp(query.Limit, 1, MaxEntriesPerInstance);
        lock (_sync)
        {
            IEnumerable<PluginLogEntry> entries = _byUniqueName.Values.SelectMany(queue => queue);

            if (!string.IsNullOrWhiteSpace(query.UniqueName))
                entries = entries.Where(entry => string.Equals(entry.UniqueName, query.UniqueName, StringComparison.OrdinalIgnoreCase));

            if (!string.IsNullOrWhiteSpace(query.Level))
                entries = entries.Where(entry => string.Equals(entry.Level, query.Level, StringComparison.OrdinalIgnoreCase));

            if (query.FromUtc.HasValue)
                entries = entries.Where(entry => entry.TimestampUtc >= query.FromUtc.Value);

            if (query.ToUtc.HasValue)
                entries = entries.Where(entry => entry.TimestampUtc <= query.ToUtc.Value);

            if (!string.IsNullOrWhiteSpace(query.Text))
            {
                var text = query.Text.Trim();
                entries = entries.Where(entry =>
                    entry.Plugin.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                    entry.Message.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                    (entry.Exception?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false));
            }

            return entries
                .OrderByDescending(entry => entry.TimestampUtc)
                .Take(limit)
                .ToList();
        }
    }

    /// <summary>Drops buffered entries for an instance (e.g. when it is removed).</summary>
    public void Clear(string uniqueName)
    {
        if (string.IsNullOrWhiteSpace(uniqueName))
            return;

        bool removed;
        lock (_sync)
        {
            removed = _byUniqueName.Remove(uniqueName);
        }

        if (removed)
            Changed?.Invoke();
    }

    /// <summary>
    /// Attempts to interpret one standard-output line as a PluginSdk
    /// <c>QuasarLogSink</c> JSON entry. Returns <c>true</c> and sets
    /// <paramref name="entry"/> only when the line is a JSON object carrying the
    /// sink's required fields (timestamp, level, plugin, message); ordinary game
    /// output is rejected cheaply so it can flow to the plain log file.
    /// </summary>
    public static bool TryParseSinkLine(string uniqueName, string? line, out PluginLogEntry? entry)
    {
        entry = null;

        if (string.IsNullOrEmpty(line))
            return false;

        // Cheap pre-filter: the sink writes compact JSON that includes the
        // "plugin" property. Avoid the parse cost for normal text output.
        var trimmed = line.TrimStart();
        if (trimmed.Length == 0 || trimmed[0] != '{' || !trimmed.Contains("\"plugin\""))
            return false;

        try
        {
            using var document = JsonDocument.Parse(trimmed);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return false;

            if (!root.TryGetProperty("timestamp", out var timestampElement) ||
                !root.TryGetProperty("level", out var levelElement) ||
                !root.TryGetProperty("plugin", out var pluginElement) ||
                !root.TryGetProperty("message", out var messageElement))
            {
                return false;
            }

            var timestamp = timestampElement.TryGetDateTimeOffset(out var parsed)
                ? parsed
                : DateTimeOffset.UtcNow;

            var threadId = 0;
            if (root.TryGetProperty("thread", out var threadElement) &&
                threadElement.ValueKind == JsonValueKind.Number)
            {
                threadElement.TryGetInt32(out threadId);
            }

            string? data = null;
            if (root.TryGetProperty("data", out var dataElement) &&
                dataElement.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
            {
                data = dataElement.GetRawText();
            }

            string? exception = null;
            if (root.TryGetProperty("exception", out var exceptionElement) &&
                exceptionElement.ValueKind == JsonValueKind.String)
            {
                exception = exceptionElement.GetString();
            }

            entry = new PluginLogEntry
            {
                UniqueName = uniqueName,
                TimestampUtc = timestamp,
                Level = levelElement.GetString() ?? string.Empty,
                Plugin = pluginElement.GetString() ?? string.Empty,
                ThreadId = threadId,
                Message = messageElement.GetString() ?? string.Empty,
                Data = data,
                Exception = exception,
            };
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

public sealed record PluginLogQuery
{
    public string UniqueName { get; init; } = string.Empty;

    public string Level { get; init; } = string.Empty;

    public string Text { get; init; } = string.Empty;

    public DateTimeOffset? FromUtc { get; init; } = DateTimeOffset.UtcNow.AddDays(-1);

    public DateTimeOffset? ToUtc { get; init; }

    public int Limit { get; init; } = 10_000;
}
