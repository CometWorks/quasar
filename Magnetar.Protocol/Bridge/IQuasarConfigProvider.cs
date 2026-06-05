namespace Magnetar.Protocol.Bridge;

/// <summary>
/// Implemented by a Space Engineers plugin that wants its configuration to be
/// editable from Quasar. The contract intentionally exchanges only JSON
/// strings so that <c>Magnetar.Protocol</c> stays free of any dependency on
/// Magnetar's <c>PluginSdk</c>: the plugin calls
/// <c>ConfigStorage.SaveJson</c> / <c>ConfigStorage.LoadJson</c> internally.
/// </summary>
public interface IQuasarConfigProvider
{
    /// <summary>
    /// Stable identifier for this plugin's configuration. Used to route
    /// update requests back to the correct provider. Should be unique within
    /// a single dedicated server process.
    /// </summary>
    string PluginId { get; }

    /// <summary>
    /// Returns the full <c>ConfigStorage.SaveJson</c> envelope
    /// (<c>schema</c> + <c>defaults</c> + <c>values</c>) describing the
    /// plugin's current configuration. Quasar renders its editor from this.
    /// </summary>
    string GetConfigJson();

    /// <summary>
    /// Applies a configuration document produced by the editor. The document
    /// may be a full <c>SaveJson</c> envelope or a flat values-only object;
    /// implementations typically pass it to <c>ConfigStorage.LoadJson</c> and
    /// copy the result onto the live config server.
    /// </summary>
    void ApplyConfigJson(string json);
}
