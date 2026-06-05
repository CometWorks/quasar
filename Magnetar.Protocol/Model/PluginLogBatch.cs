using System.Collections.Generic;

namespace Magnetar.Protocol.Model;

/// <summary>
/// A batch of plugin log lines the agent ships to Quasar over its WebSocket
/// channel. Each entry is one formatted JSON line exactly as the PluginSdk
/// <c>QuasarLogSink</c> renders it, so Quasar can reuse its existing sink-line
/// parser to turn them back into log entries.
///
/// <para>
/// This channel replaces standard-output capture for the live log panel: it
/// keeps flowing after Quasar restarts and reconnects to a detached server
/// daemon, and the agent buffers lines while Quasar is unreachable so they are
/// backfilled on reconnect.
/// </para>
/// </summary>
public class PluginLogBatch
{
    public List<string> Lines { get; set; } = new();
}
