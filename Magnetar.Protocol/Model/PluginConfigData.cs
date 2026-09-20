namespace Magnetar.Protocol.Model;

/// <summary>
/// One plugin's configuration as exposed over the wire. <see cref="ConfigJson"/>
/// carries the full <c>ConfigStorage.SaveJson</c> envelope
/// (<c>schema</c> + <c>defaults</c> + <c>values</c>).
/// </summary>
public class PluginConfigData
{
    public string PluginId { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Exact SDK configuration type name, or empty when the provider cannot be converted.</summary>
    public string ConfigType { get; set; } = string.Empty;

    public string ConfigJson { get; set; } = string.Empty;
}
