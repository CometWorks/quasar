namespace Quasar.Services.PluginSdk;

/// <summary>
/// One structured log entry emitted by a plugin through the PluginSdk
/// <c>QuasarLogSink</c>. The sink writes a single JSON line per entry to the
/// dedicated server's standard output; the supervisor parses those lines into
/// this Quasar-side model. The field shape mirrors the sink's JSON:
/// <c>{ timestamp, level, plugin, thread, message, data?, exception? }</c>.
/// </summary>
public sealed class PluginLogEntry
{
    /// <summary>Quasar unique name of the server server that produced the entry.</summary>
    public string UniqueName { get; init; } = string.Empty;

    /// <summary>Entry time in UTC (parsed from the sink's ISO-8601 timestamp).</summary>
    public DateTimeOffset TimestampUtc { get; init; }

    /// <summary>Severity name as emitted by the SDK (Debug, Info, Warning, Error, Critical).</summary>
    public string Level { get; init; } = string.Empty;

    /// <summary>Name of the plugin logger.</summary>
    public string Plugin { get; init; } = string.Empty;

    /// <summary>Managed thread id that produced the entry.</summary>
    public int ThreadId { get; init; }

    /// <summary>The log message.</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>Optional structured data payload as raw JSON; null when absent.</summary>
    public string? Data { get; init; }

    /// <summary>Optional formatted exception text; null when absent.</summary>
    public string? Exception { get; init; }
}
