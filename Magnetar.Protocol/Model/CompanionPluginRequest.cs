namespace Magnetar.Protocol.Model;

/// <summary>
/// Generic request from a Quasar UI plugin to a Magnetar companion plugin.
/// The payload is JSON owned by the caller and target plugin.
/// </summary>
public class CompanionPluginRequest
{
    public string PluginId { get; set; } = string.Empty;

    public string Operation { get; set; } = string.Empty;

    public int SchemaVersion { get; set; } = 1;

    public string CorrelationId { get; set; } = string.Empty;

    public string PayloadJson { get; set; } = string.Empty;
}
