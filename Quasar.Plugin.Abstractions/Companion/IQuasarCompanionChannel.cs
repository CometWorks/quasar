namespace Quasar.Plugin.Abstractions.Companion;

/// <summary>
/// Sends typed requests from a Quasar UI plugin to a companion Magnetar plugin
/// running inside a connected Dedicated Server.
/// </summary>
public interface IQuasarCompanionChannel
{
    /// <param name="serverId">
    /// Connected server identifier. Current Quasar accepts the live agent id,
    /// server key, or server unique name.
    /// </param>
    Task<TResponse> SendAsync<TRequest, TResponse>(
        string serverId,
        string companionPluginId,
        string operation,
        TRequest request,
        CancellationToken cancellationToken);
}
