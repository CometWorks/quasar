namespace Magnetar.Protocol.Model;

public class PluginRuntimeInfo
{
    public string PluginId { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string Version { get; set; } = string.Empty;

    public bool IsLoaded { get; set; }
}
