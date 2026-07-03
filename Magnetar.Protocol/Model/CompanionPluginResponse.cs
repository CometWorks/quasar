using System.Collections.Generic;

namespace Magnetar.Protocol.Model;

/// <summary>
/// Generic response from a Magnetar companion plugin to a Quasar UI plugin.
/// The payload is JSON owned by the target plugin and caller.
/// </summary>
public class CompanionPluginResponse
{
    public int SchemaVersion { get; set; } = 1;

    public string CorrelationId { get; set; } = string.Empty;

    public bool Success { get; set; }

    public string PayloadJson { get; set; } = string.Empty;

    public string Error { get; set; } = string.Empty;

    public List<string> Warnings { get; set; } = new List<string>();
}
